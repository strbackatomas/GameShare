using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using GameShare.Core.Data;
using GameShare.Discovery;
using GameShare.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace GameShare.Agent;

/// <summary>
/// Remote management: one PC drives the downloads of others that were paired with it, so nobody has to walk to each PC to click Install.
///
/// As a target, this PC listens on its own TLS port, and only while the person at it has turned remote management on. A request is
/// served only when it comes from the LAN, shows the certificate of a paired PC, and is on <see cref="RemoteWhitelist"/>. It is then
/// passed to this PC's own control API, so it gets exactly the checks the local client gets. As a controller, this PC passes requests
/// of its own client on to a paired target, after checking the target's certificate against the one it pinned when pairing.
/// See <see cref="RemotePairing"/> for how pairing proves both sides.
/// </summary>
public sealed class RemoteAccessService : IHostedService, IAsyncDisposable
{
    private const string StateKey = "remote.state";
    private const string IdentityKey = "remote.identity";
    private const int MaxBodyBytes = 64 * 1024; // the API takes small JSON bodies only
    private const int MaxNameLength = 64;

    internal sealed record PairedMachine(
        string MachineId, string MachineName, string Fingerprint, DateTimeOffset PairedAt, DateTimeOffset? LastUsed, int? RemotePort = null,
        List<string>? MacAddresses = null);
    internal sealed record StoredState(bool Enabled, List<PairedMachine> Controllers, List<PairedMachine> Targets);
    private sealed record OpenPairing(string Code, DateTimeOffset ExpiresAt);

    /// <summary>What is kept in the database, loaded before the agent's services are built.</summary>
    public sealed class Stored
    {
        internal StoredState State { get; init; } = new(false, [], []);
        internal byte[] Identity { get; init; } = [];
    }

    private readonly AgentOptions _options;
    private readonly AgentIdentity _me;
    private readonly GameShareDb _db;
    private readonly DiscoveryService _discovery;
    private readonly IHttpClientFactory _http;
    private readonly IWakeOnLan _wake;
    private readonly ILogger<RemoteAccessService> _log;
    private readonly X509Certificate2 _certificate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _pinned = new(StringComparer.Ordinal);
    private readonly object _pairingLock = new();

    private volatile StoredState _state;

    /// <summary>A backup was put in the database and the agent is about to restart: nothing may write over it until then.</summary>
    private volatile bool _replaced;
    private OpenPairing? _pairing;
    private WebApplication? _server;

    public RemoteAccessService(
        Stored stored, AgentOptions options, AgentIdentity me, GameShareDb db, DiscoveryService discovery, IHttpClientFactory http, IWakeOnLan wake,
        ILogger<RemoteAccessService> log)
    {
        _wake = wake;
        _options = options;
        _me = me;
        _db = db;
        _discovery = discovery;
        _http = http;
        _log = log;
        _state = stored.State;
        _certificate = RemotePairing.LoadIdentity(stored.Identity);
        Fingerprint = RemotePairing.Fingerprint(_certificate);
    }

    /// <summary>Reads the saved state and this PC's certificate, making the certificate the first time.</summary>
    public static async Task<Stored> LoadAsync(GameShareDb db, string machineId, CancellationToken ct = default)
    {
        var identity = await db.GetSettingAsync(IdentityKey, ct).ConfigureAwait(false);
        if (identity is null)
        {
            identity = Convert.ToBase64String(RemotePairing.NewIdentity(machineId));
            await db.SetSettingAsync(IdentityKey, identity, ct).ConfigureAwait(false);
        }
        var json = await db.GetSettingAsync(StateKey, ct).ConfigureAwait(false);
        var state = json is null ? new StoredState(false, [], []) : GameShareJson.Deserialize<StoredState>(json);
        return new Stored { State = state with { Controllers = state.Controllers ?? [], Targets = state.Targets ?? [] }, Identity = Convert.FromBase64String(identity) };
    }

    public string Fingerprint { get; }

    /// <summary>The port paired PCs reach this one on, while it is listening. Told to other PCs in the peer hello.</summary>
    public int? ListeningPort => _server is null ? null : _options.RemoteApiPort;

    /// <summary>Raised when what <see cref="Status"/> returns has changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a paired PC changed something here.</summary>
    public event EventHandler<RemoteActionDto>? ActionPerformed;

    public RemoteStatusDto Status()
    {
        var state = _state;
        var online = _discovery.Peers.Select(p => p.MachineId).ToHashSet(StringComparer.Ordinal);
        var pairing = CurrentPairing();
        return new RemoteStatusDto(
            _options.RemoteManagementAllowed, state.Enabled && _options.RemoteManagementAllowed, ListeningPort is not null, _options.RemoteApiPort, Fingerprint,
            pairing is null ? null : new RemotePairingDto(pairing.Code, pairing.ExpiresAt),
            state.Controllers.Select(c => ToDto(c, false)).ToList(),
            state.Targets.Select(t => ToDto(t, online.Contains(t.MachineId))).ToList());
    }

    private static PairedMachineDto ToDto(PairedMachine m, bool online) =>
        new(m.MachineId, m.MachineName, m.Fingerprint, m.PairedAt, m.LastUsed, online, m.MacAddresses is { Count: > 0 });

    /// <summary>What this PC tells others in its peer hello: how to wake it, only while it may be managed.</summary>
    public IReadOnlyList<string>? WakeAddresses => _server is null ? null : _wake.OwnMacAddresses();

    // ---- lifetime ----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _discovery.PeerEventRaised += OnPeer;
        if (!_state.Enabled) return;
        if (!_options.RemoteManagementAllowed)
        {
            _log.LogInformation("Remote management is turned on in the saved state, but this build does not take it. Not listening.");
            return;
        }
        try { await StartServerAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Never keep the agent from starting: downloads and seeding matter more. The status shows it is not listening.
            _log.LogError(ex, "Remote management could not listen on port {Port}", _options.RemoteApiPort);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _discovery.PeerEventRaised -= OnPeer;
        await StopServerAsync().ConfigureAwait(false);
    }

    /// <summary>A managed PC came onto the network: learn how to wake it, it may have a new adapter since.</summary>
    private void OnPeer(object? sender, PeerEvent e)
    {
        if (e.Kind == PeerEventKind.Left || _state.Targets.All(t => t.MachineId != e.Peer.MachineId)) return;
        _ = Task.Run(async () =>
        {
            try { await AskRemotePortAsync(e.Peer, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                _log.LogDebug("Could not ask {Name} how to wake it: {Message}", e.Peer.MachineName, ex.Message);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await StopServerAsync().ConfigureAwait(false);
        foreach (var client in _pinned.Values) client.Dispose();
        _pinned.Clear();
        _certificate.Dispose();
    }

    // ---- as a target: what the person at this PC does ----

    /// <summary>Turns taking remote management on or off. Off closes the port; the paired PCs are kept for when it is turned on again.</summary>
    public async Task<RemoteStatusDto> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled && !_options.RemoteManagementAllowed)
            throw new InvalidOperationException("This build of GameShare does not take remote management.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (enabled && _server is null) await StartServerAsync().ConfigureAwait(false); // fails before anything is saved
            if (!enabled)
            {
                ClosePairing();
                await StopServerAsync().ConfigureAwait(false);
            }
            if (_state.Enabled != enabled) await SaveAsync(_state with { Enabled = enabled }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        _log.LogInformation("Remote management turned {State}", enabled ? "on" : "off");
        RaiseChanged();
        return Status();
    }

    /// <summary>Opens a pairing: a new code, valid for a few minutes and for one attempt. Replaces a code that was open.</summary>
    public RemotePairingDto StartPairing()
    {
        if (_server is null) throw new InvalidOperationException("Turn remote management on first, then pair.");
        var pairing = new OpenPairing(RemotePairing.NewCode(), DateTimeOffset.UtcNow + _options.RemotePairingLifetime);
        lock (_pairingLock) _pairing = pairing;
        _log.LogInformation("Pairing opened, valid until {Until:HH:mm:ss}", pairing.ExpiresAt.ToLocalTime());
        RaiseChanged();
        return new RemotePairingDto(pairing.Code, pairing.ExpiresAt);
    }

    public void CancelPairing()
    {
        if (ClosePairing()) RaiseChanged();
    }

    /// <summary>A PC that may manage this one no more. Takes effect with its next request.</summary>
    public async Task RemoveControllerAsync(string machineId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var controller = _state.Controllers.FirstOrDefault(c => c.MachineId == machineId)
                ?? throw new KeyNotFoundException("No PC with that id may manage this one.");
            await SaveAsync(_state with { Controllers = _state.Controllers.Where(c => c != controller).ToList() }, ct).ConfigureAwait(false);
            _log.LogInformation("Remote management by {Name} ({Id}) removed", controller.MachineName, controller.MachineId);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    private OpenPairing? CurrentPairing()
    {
        lock (_pairingLock)
        {
            if (_pairing is not null && _pairing.ExpiresAt <= DateTimeOffset.UtcNow) _pairing = null;
            return _pairing;
        }
    }

    private bool ClosePairing()
    {
        lock (_pairingLock)
        {
            bool was = _pairing is not null;
            _pairing = null;
            return was;
        }
    }

    /// <summary>The open pairing, taken: right or wrong, a code is good for one attempt only.</summary>
    private OpenPairing? TakePairing()
    {
        lock (_pairingLock)
        {
            var pairing = CurrentPairing();
            _pairing = null;
            return pairing;
        }
    }

    // ---- as a target: the TLS listener ----

    private async Task StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(RemoteAccessService).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders(); // what matters is logged by this class into the agent's own log
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxBodyBytes;
            k.Listen(IPAddress.Any, _options.RemoteApiPort, l =>
            {
                l.Protocols = HttpProtocols.Http1;
                l.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = _certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    // Every certificate is let through the handshake. Which one it is decides each request: a paired PC's, or only pairing.
                    ClientCertificateValidation = (_, _, _) => true,
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                });
            });
        });

        var app = builder.Build();
        app.Run(HandleAsync);
        try { await app.StartAsync().ConfigureAwait(false); }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _server = app;
        _log.LogInformation("Remote management listening on port {Port}, certificate {Fingerprint}", _options.RemoteApiPort, Fingerprint);
    }

    private async Task StopServerAsync()
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is null) return;
        try { await server.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally { await server.DisposeAsync().ConfigureAwait(false); }
        _log.LogInformation("Remote management stopped listening");
    }

    private async Task HandleAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        if (_options.LanOnly && !LanAddress.IsPrivate(remote))
        {
            await DenyAsync(context, StatusCodes.Status403Forbidden, "Remote management only answers the local network.").ConfigureAwait(false);
            return;
        }
        if (context.Connection.ClientCertificate is not { } certificate)
        {
            await DenyAsync(context, StatusCodes.Status403Forbidden, "Remote management needs the certificate of a paired PC.").ConfigureAwait(false);
            return;
        }
        var fingerprint = RemotePairing.Fingerprint(certificate);

        // Before pairing: a request that only makes the TLS connection, so the controller has seen this PC's certificate, and the pairing.
        if (path == "/remote/hello" && HttpMethods.IsGet(method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        if (path == "/remote/pair" && HttpMethods.IsPost(method))
        {
            await PairIncomingAsync(context, fingerprint, remote).ConfigureAwait(false);
            return;
        }

        var controller = _state.Controllers.FirstOrDefault(c => c.Fingerprint == fingerprint);
        if (controller is null)
        {
            await DenyAsync(context, StatusCodes.Status403Forbidden, $"Your PC is not paired with {_me.MachineName}, or the pairing was removed there.").ConfigureAwait(false);
            return;
        }

        var relative = path.StartsWith("/api/", StringComparison.Ordinal) ? path["/api/".Length..] : null;
        if (relative is null || !RemoteWhitelist.Allows(method, relative))
        {
            _log.LogWarning("Refused remote {Method} {Path} from {Name} ({Remote}): not allowed remotely", method, path, controller.MachineName, remote);
            await DenyAsync(context, StatusCodes.Status403Forbidden, $"{method} {path} cannot be done remotely, only at {_me.MachineName} itself.").ConfigureAwait(false);
            return;
        }

        int status = await ForwardToLocalAsync(context, relative).ConfigureAwait(false);
        await NoteUseAsync(controller, method, relative, status).ConfigureAwait(false);
    }

    /// <summary>Passes the request to this PC's own control API, over loopback, and its answer back unchanged.</summary>
    private async Task<int> ForwardToLocalAsync(HttpContext context, string relative)
    {
        var ct = context.RequestAborted;
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            $"http://127.0.0.1:{_options.LocalApiPort}/api/{relative}{context.Request.QueryString}");
        await CopyBodyAsync(context.Request, request, ct).ConfigureAwait(false);

        using var response = await _http.CreateClient("remote-local").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await CopyResponseAsync(response, context.Response, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    private async Task NoteUseAsync(PairedMachine controller, string method, string relative, int status)
    {
        bool action = RemoteWhitelist.IsAction(method);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _state.Controllers.FirstOrDefault(c => c.MachineId == controller.MachineId);
            if (current is not null)
            {
                var updated = _state with { Controllers = _state.Controllers.Select(c => c == current ? c with { LastUsed = now } : c).ToList() };
                if (action) await SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
                else _state = updated; // looking around is frequent, kept in memory only
            }
        }
        finally { _gate.Release(); }

        if (!action) return;
        _log.LogInformation("Remote: {Name} ({Id}) {Method} {Path}, answered {Status}", controller.MachineName, controller.MachineId, method, relative, status);
        ActionPerformed?.Invoke(this, new RemoteActionDto(controller.MachineId, controller.MachineName, $"{method} {relative}", status, now));
    }

    private async Task PairIncomingAsync(HttpContext context, string controllerFingerprint, IPAddress remote)
    {
        RemotePairOfferDto? offer;
        try { offer = await context.Request.ReadFromJsonAsync<RemotePairOfferDto>(GameShareJson.Options, context.RequestAborted).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException or InvalidOperationException) { offer = null; }
        if (offer is null || !IsMachineId(offer.MachineId) || string.IsNullOrWhiteSpace(offer.MachineName) || offer.Proof is null)
        {
            await DenyAsync(context, StatusCodes.Status400BadRequest, "Not a pairing request.").ConfigureAwait(false);
            return;
        }
        var name = Shorten(offer.MachineName);

        var pairing = TakePairing();
        if (pairing is null)
        {
            _log.LogWarning("Pairing attempt by {Name} ({Remote}) while no pairing is open", name, remote);
            await DenyAsync(context, StatusCodes.Status409Conflict, $"No pairing is open on {_me.MachineName}. Start one in its settings, then enter the code it shows.").ConfigureAwait(false);
            return;
        }
        RaiseChanged(); // the code is gone from the screen either way

        var expected = RemotePairing.OfferProof(pairing.Code, offer.MachineId, controllerFingerprint, _me.MachineId, Fingerprint);
        if (!RemotePairing.ProofsEqual(expected, offer.Proof))
        {
            _log.LogWarning("Pairing attempt by {Name} ({Remote}) with a wrong code. The code is no longer valid.", name, remote);
            await DenyAsync(context, StatusCodes.Status403Forbidden, $"The code does not match the one {_me.MachineName} showed. Start a new pairing there, the old code is no longer valid.").ConfigureAwait(false);
            return;
        }

        var paired = new PairedMachine(offer.MachineId, name, controllerFingerprint, DateTimeOffset.UtcNow, null);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A PC paired again replaces what was kept about it, also when it got a new certificate.
            var others = _state.Controllers.Where(c => c.MachineId != paired.MachineId && c.Fingerprint != paired.Fingerprint);
            await SaveAsync(_state with { Controllers = [.. others, paired] }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        _log.LogInformation("Paired: {Name} ({Id}, {Remote}) may now manage this PC, certificate {Fingerprint}", name, offer.MachineId, remote, controllerFingerprint);
        RaiseChanged();

        await context.Response.WriteAsJsonAsync(new RemotePairAcceptDto(_me.MachineId, _me.MachineName,
            RemotePairing.AcceptProof(pairing.Code, offer.MachineId, controllerFingerprint, _me.MachineId, Fingerprint)), GameShareJson.Options).ConfigureAwait(false);
    }

    // ---- as a controller ----

    /// <summary>Pairs with a PC on the LAN that shows <paramref name="code"/>. Afterwards this PC may manage it.</summary>
    public async Task<PairedMachineDto> PairAsync(string machineId, string code, CancellationToken ct = default)
    {
        code = RemotePairing.NormalizeCode(code);
        if (machineId == _me.MachineId) throw new ArgumentException("This PC cannot pair with itself.");
        var peer = FindPeer(machineId) ?? throw new KeyNotFoundException("That PC is not on the network now.");
        var port = await AskRemotePortAsync(peer, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{peer.MachineName} does not take remote management. Turn it on in its settings first.");

        // The target's certificate is whatever the first connection shows. Every later connection of this pairing must show the same.
        string? seen = null;
        using var client = new HttpMessageInvoker(NewHandler(certificate =>
        {
            var fingerprint = RemotePairing.Fingerprint(certificate);
            return Interlocked.CompareExchange(ref seen, fingerprint, null) is null || seen == fingerprint;
        }));
        var baseUri = new Uri($"https://{peer.Address}:{port}");
        using (var hello = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/remote/hello")))
        using (var answer = await SendAsync(client, hello, peer.MachineName, ct).ConfigureAwait(false))
            if (!answer.IsSuccessStatusCode || seen is null)
                throw new InvalidOperationException($"{peer.MachineName} did not answer as a GameShare PC that takes remote management.");
        var targetFingerprint = seen!;

        var offer = new RemotePairOfferDto(_me.MachineId, _me.MachineName,
            RemotePairing.OfferProof(code, _me.MachineId, Fingerprint, machineId, targetFingerprint));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/remote/pair")) { Content = JsonContent.Create(offer, options: GameShareJson.Options) };
        using var response = await SendAsync(client, request, peer.MachineName, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await ProblemDetailAsync(response, ct).ConfigureAwait(false) ?? $"{peer.MachineName} refused the pairing ({(int)response.StatusCode}).";
            throw response.StatusCode == HttpStatusCode.Forbidden ? new ArgumentException(reason) : new InvalidOperationException(reason);
        }

        var accept = await response.Content.ReadFromJsonAsync<RemotePairAcceptDto>(GameShareJson.Options, ct).ConfigureAwait(false);
        if (accept is null || accept.MachineId != machineId
            || !RemotePairing.ProofsEqual(RemotePairing.AcceptProof(code, _me.MachineId, Fingerprint, machineId, targetFingerprint), accept.Proof))
        {
            _log.LogWarning("Pairing with {Name} ({Address}): its answer does not prove it knew the code. Not paired.", peer.MachineName, peer.Address);
            throw new InvalidDataException($"The answer from {peer.Address} does not prove it is the PC that showed the code. Not paired.");
        }

        var paired = new PairedMachine(machineId, Shorten(accept.MachineName), targetFingerprint, DateTimeOffset.UtcNow, null, port);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveAsync(_state with { Targets = [.. _state.Targets.Where(t => t.MachineId != machineId), paired] }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        _log.LogInformation("Paired: this PC may now manage {Name} ({Id}), certificate {Fingerprint}", paired.MachineName, machineId, targetFingerprint);
        try { await AskRemotePortAsync(peer, ct).ConfigureAwait(false); } // its MAC addresses, now that there is a record to keep them in
        catch (HttpRequestException) { /* learnt the next time it comes onto the network */ }
        RaiseChanged();
        return ToDto(_state.Targets.First(t => t.MachineId == machineId), true);
    }

    /// <summary>Forgets a PC this one managed. The target keeps its side until the person there removes it, but it can no longer be reached from here.</summary>
    public async Task RemoveTargetAsync(string machineId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = _state.Targets.FirstOrDefault(t => t.MachineId == machineId)
                ?? throw new KeyNotFoundException("This PC is not paired with a PC with that id.");
            await SaveAsync(_state with { Targets = _state.Targets.Where(t => t != target).ToList() }, ct).ConfigureAwait(false);
            if (_pinned.TryRemove(target.Fingerprint, out var client)) client.Dispose();
            _log.LogInformation("No longer managing {Name} ({Id})", target.MachineName, machineId);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    /// <summary>
    /// Passes a request of this PC's client on to a paired target's control API, and the answer back. Which requests the target
    /// serves is up to the target, see <see cref="RemoteWhitelist"/>.
    /// </summary>
    public async Task ForwardToTargetAsync(HttpContext context, string machineId, string relative)
    {
        var ct = context.RequestAborted;
        var target = _state.Targets.FirstOrDefault(t => t.MachineId == machineId)
            ?? throw new KeyNotFoundException("This PC is not paired with a PC with that id. Pair with it first.");
        var peer = FindPeer(machineId) ?? throw new HttpRequestException($"{target.MachineName} is not on the network now.");
        var client = _pinned.GetOrAdd(target.Fingerprint, fingerprint =>
            new HttpMessageInvoker(NewHandler(certificate => RemotePairing.Fingerprint(certificate) == fingerprint)));

        var body = await ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        HttpRequestMessage Build(int port)
        {
            var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                $"https://{peer.Address}:{port}/api/{relative}{context.Request.QueryString}");
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (context.Request.ContentType is { } type) request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }
            return request;
        }

        int port = target.RemotePort ?? _options.RemoteApiPort;
        HttpResponseMessage response;
        try
        {
            using var request = Build(port);
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // The target may have been set up with another port since, or turned remote management off. Its hello says which.
            var asked = await AskRemotePortAsync(peer, ct).ConfigureAwait(false)
                ?? throw new HttpRequestException($"{target.MachineName} does not take remote management now. It can be turned on in its settings.");
            if (asked == port) throw;
            await RememberPortAsync(machineId, asked, ct).ConfigureAwait(false);
            using var request = Build(asked);
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }

        using (response) await CopyResponseAsync(response, context.Response, ct).ConfigureAwait(false);
    }

    private async Task RememberPortAsync(string machineId, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveAsync(_state with { Targets = _state.Targets.Select(t => t.MachineId == machineId ? t with { RemotePort = port } : t).ToList() }, ct)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The target's remote port from its peer hello, null when it does not take remote management.</summary>
    private async Task<int?> AskRemotePortAsync(PeerInfo peer, CancellationToken ct)
    {
        var hello = await _http.CreateClient("peer")
            .GetFromJsonAsync<PeerHelloDto>(new Uri($"http://{peer.Address}:{peer.AgentPort}/peer/hello"), GameShareJson.Options, ct).ConfigureAwait(false);
        if (hello is null || hello.MachineId != peer.MachineId)
            throw new HttpRequestException($"The PC at {peer.Address} is not {peer.MachineName} any more.");
        await RememberMacsAsync(peer.MachineId, hello.MacAddresses, ct).ConfigureAwait(false);
        return hello.RemotePort is > 0 and <= 65535 ? hello.RemotePort : null;
    }

    /// <summary>Keeps the MAC addresses a managed PC told, the last that were told: a PC with remote management off tells none.</summary>
    private async Task RememberMacsAsync(string machineId, IReadOnlyList<string>? told, CancellationToken ct)
    {
        var macs = (told ?? []).Where(IsMac).Take(8).ToList();
        if (macs.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = _state.Targets.FirstOrDefault(t => t.MachineId == machineId);
            if (target is null || (target.MacAddresses ?? []).SequenceEqual(macs)) return;
            await SaveAsync(_state with { Targets = _state.Targets.Select(t => t == target ? t with { MacAddresses = macs } : t).ToList() }, ct)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    private static bool IsMac(string? mac)
    {
        try { return mac is not null && WakeOnLan.Packet(mac).Length > 0; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Wakes a managed PC that is off, with the MAC addresses it told while it was on.</summary>
    public async Task WakeAsync(string machineId, CancellationToken ct = default)
    {
        var target = _state.Targets.FirstOrDefault(t => t.MachineId == machineId)
            ?? throw new KeyNotFoundException("This PC is not paired with a PC with that id.");
        if (target.MacAddresses is not { Count: > 0 } macs)
            throw new InvalidOperationException($"How to wake {target.MachineName} is not known yet. It tells that while it is on with remote management turned on.");
        await _wake.SendAsync(macs, ct).ConfigureAwait(false);
        _log.LogInformation("Waking {Name} ({Id})", target.MachineName, machineId);
    }

    private PeerInfo? FindPeer(string machineId) => _discovery.Peers.FirstOrDefault(p => p.MachineId == machineId);

    /// <summary>TLS that shows this PC's certificate and accepts the other side's only when <paramref name="accept"/> says so.</summary>
    private SocketsHttpHandler NewHandler(Func<X509Certificate, bool> accept) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = [_certificate],
            LocalCertificateSelectionCallback = (_, _, _, _, _) => _certificate,
            // The certificate is self-signed and the address is whatever DHCP gave, so neither the chain nor the name mean anything here.
            // What counts is that it is the one certificate pinned for this PC.
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && accept(certificate),
        },
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpMessageInvoker client, HttpRequestMessage request, string name, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { return await client.SendAsync(request, timeout.Token).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new HttpRequestException($"{name} could not be reached for pairing: {ex.Message}", ex); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException($"{name} did not answer in time."); }
    }

    // ---- shared plumbing ----

    private async Task SaveAsync(StoredState state, CancellationToken ct)
    {
        if (!_replaced) await _db.SetSettingAsync(StateKey, GameShareJson.Serialize(state), ct).ConfigureAwait(false);
        _state = state;
    }

    // ---- backup ----

    /// <summary>This PC's identity, certificate and pairings, encrypted with the password. See <see cref="PairingBackup"/>.</summary>
    public async Task<byte[]> ExportAsync(string password, CancellationToken ct = default)
    {
        var identity = await _db.GetSettingAsync(IdentityKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This PC has no identity to back up yet.");
        var backup = PairingBackup.Create(new PairingBackupPayload(_me.MachineId, identity, GameShareJson.Serialize(_state)), password, _me.MachineName);
        _log.LogInformation("Pairing backup made ({Controllers} PCs may manage this one, it manages {Targets})", _state.Controllers.Count, _state.Targets.Count);
        return backup;
    }

    /// <summary>
    /// Puts a backup's identity, certificate and pairings in place of this PC's. Takes effect when the agent restarts, which the
    /// caller does. Refused while the PC the backup was made on is on the network: two PCs must never share one identity.
    /// </summary>
    public async Task<RemoteRestoreResultDto> ImportAsync(byte[] data, string password, CancellationToken ct = default)
    {
        if (!_options.RemoteManagementAllowed)
            throw new InvalidOperationException("This build of GameShare does not take remote management, so it cannot restore pairings either.");
        var payload = PairingBackup.Open(data, password);

        StoredState state;
        try
        {
            state = GameShareJson.Deserialize<StoredState>(payload.State);
            using var check = RemotePairing.LoadIdentity(Convert.FromBase64String(payload.Identity));
            if (!check.HasPrivateKey) throw new System.Security.Cryptography.CryptographicException("no private key");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new ArgumentException("This backup is damaged.");
        }

        var name = PairingBackup.MachineNameOf(data) ?? payload.MachineId;
        if (payload.MachineId != _me.MachineId && _discovery.Peers.FirstOrDefault(p => p.MachineId == payload.MachineId) is { } original)
            throw new InvalidOperationException(
                $"The PC this backup was made on is on the network now as {original.MachineName} ({original.Address}). Restore a backup only on the "
                + "same PC after it was reinstalled; two PCs with one identity would confuse every other PC.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _replaced = true;
            await _db.SetSettingAsync("machine.id", payload.MachineId, ct).ConfigureAwait(false);
            await _db.SetSettingAsync(IdentityKey, payload.Identity, ct).ConfigureAwait(false);
            await _db.SetSettingAsync(StateKey, payload.State, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        _log.LogWarning("Pairings restored from the backup of {Name}: identity {Id}, {Controllers} PCs may manage this one, it manages {Targets}. Restarting.",
            name, payload.MachineId, state.Controllers?.Count ?? 0, state.Targets?.Count ?? 0);
        return new RemoteRestoreResultDto(name, state.Controllers?.Count ?? 0, state.Targets?.Count ?? 0);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is 0 || (request.ContentLength is null && !request.Headers.ContainsKey("Transfer-Encoding"))) return null;
        if (request.ContentLength > MaxBodyBytes) throw new BadHttpRequestException("The request is too large to pass on.", StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) throw new BadHttpRequestException("The request is too large to pass on.", StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task CopyBodyAsync(HttpRequest from, HttpRequestMessage to, CancellationToken ct)
    {
        if (await ReadBodyAsync(from, ct).ConfigureAwait(false) is not { } body) return;
        to.Content = new ByteArrayContent(body);
        if (from.ContentType is { } type) to.Content.Headers.TryAddWithoutValidation("Content-Type", type);
    }

    /// <summary>Status, content type and body. Nothing else: no cookies, no headers of the other side.</summary>
    private static async Task CopyResponseAsync(HttpResponseMessage from, HttpResponse to, CancellationToken ct)
    {
        to.StatusCode = (int)from.StatusCode;
        if (from.Content.Headers.ContentType is { } type) to.ContentType = type.ToString();
        await from.Content.CopyToAsync(to.Body, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<ProblemDetails>(GameShareJson.Options, ct).ConfigureAwait(false))?.Detail; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { return null; }
    }

    private static Task DenyAsync(HttpContext context, int status, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = "Refused", Detail = detail, Instance = context.Request.Path.Value });
    }

    private static bool IsMachineId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Shorten(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }
}
