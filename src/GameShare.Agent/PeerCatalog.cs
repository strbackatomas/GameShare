using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using GameShare.Discovery;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent;

public sealed record RemoteOffer(PeerInfo Peer, OfferedGameDto Game);

/// <param name="ContentHashes">Versions that appeared or disappeared from the LAN.</param>
public sealed record CatalogChange(IReadOnlyList<string> ContentHashes);

/// <summary>
/// What the other PCs on the LAN currently offer. Fed by discovery, filled by asking each peer's read-only API.
/// Everything a peer sends is validated before it is used.
/// </summary>
public sealed partial class PeerCatalog
{
    private const int MaxOffersPerPeer = 5000;
    private const long MaxManifestBytes = 64L << 20;
    private const long MaxTorrentBytes = 16L << 20;
    private const int FailuresBeforeDropped = 3;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    private sealed record PeerState(PeerInfo Peer, IReadOnlyList<OfferedGameDto> Offers, int Failures);

    private readonly DiscoveryService _discovery;
    private readonly IHttpClientFactory _http;
    private readonly AgentOptions _options;
    private readonly ILogger<PeerCatalog> _log;
    private readonly ConcurrentDictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private readonly Channel<string> _refreshRequests = Channel.CreateUnbounded<string>();

    public PeerCatalog(DiscoveryService discovery, IHttpClientFactory http, AgentOptions options, ILogger<PeerCatalog> log)
    {
        _discovery = discovery;
        _http = http;
        _options = options;
        _log = log;
    }

    /// <summary>Raised on a background thread when the set of offered versions changes.</summary>
    public event EventHandler<CatalogChange>? Changed;

    public IReadOnlyList<RemoteOffer> Offers =>
        _peers.Values.SelectMany(p => p.Offers.Select(o => new RemoteOffer(p.Peer, o))).ToList();

    public int OfferCount(string machineId) => _peers.TryGetValue(machineId, out var p) ? p.Offers.Count : 0;

    public async Task RunAsync(CancellationToken ct)
    {
        _discovery.PeerEventRaised += OnPeerEvent;
        try
        {
            // Peers found before we subscribed still need a first look.
            foreach (var p in _discovery.Peers) _refreshRequests.Writer.TryWrite(p.MachineId);
            await Task.WhenAll(RefreshLoopAsync(ct), PeriodicLoopAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally { _discovery.PeerEventRaised -= OnPeerEvent; }
    }

    private void OnPeerEvent(object? sender, PeerEvent e)
    {
        if (e.Kind == PeerEventKind.Left)
        {
            if (_peers.TryRemove(e.Peer.MachineId, out var gone) && gone.Offers.Count > 0)
                Raise(gone.Offers.Select(o => o.ContentHash));
        }
        else
        {
            _refreshRequests.Writer.TryWrite(e.Peer.MachineId);
        }
    }

    private async Task PeriodicLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.CatalogRefreshInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            foreach (var p in _discovery.Peers) _refreshRequests.Writer.TryWrite(p.MachineId);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        await foreach (var machineId in _refreshRequests.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var peer = _discovery.Peers.FirstOrDefault(p => p.MachineId == machineId);
            if (peer is null) continue; // left in the meantime
            try { await RefreshAsync(peer, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogError(ex, "Refreshing offers of {Peer} failed unexpectedly", peer.MachineName); }
        }
    }

    private async Task RefreshAsync(PeerInfo peer, CancellationToken ct)
    {
        _peers.TryGetValue(peer.MachineId, out var before);
        try
        {
            var offers = await GetOffersAsync(peer, ct).ConfigureAwait(false);
            _peers[peer.MachineId] = new PeerState(peer, offers, 0);

            var oldHashes = before?.Offers.Select(o => o.ContentHash).ToHashSet() ?? [];
            var newHashes = offers.Select(o => o.ContentHash).ToHashSet();
            var changed = oldHashes.Union(newHashes).Where(h => oldHashes.Contains(h) != newHashes.Contains(h)).ToList();
            if (before is null) _log.LogInformation("Peer {Peer} offers {Count} games", peer.MachineName, offers.Count);
            if (changed.Count > 0) Raise(changed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            int failures = (before?.Failures ?? 0) + 1;
            _log.LogWarning("Cannot read offers from {Peer} at {Address}:{Port}: {Reason} (attempt {Failures})",
                peer.MachineName, peer.Address, peer.AgentPort, ex.Message, failures);

            if (failures >= FailuresBeforeDropped && before is not null)
            {
                _peers.TryRemove(peer.MachineId, out _);
                if (before.Offers.Count > 0) Raise(before.Offers.Select(o => o.ContentHash));
            }
            else if (before is not null)
            {
                _peers[peer.MachineId] = before with { Failures = failures };
            }
        }
    }

    private async Task<IReadOnlyList<OfferedGameDto>> GetOffersAsync(PeerInfo peer, CancellationToken ct)
    {
        EnsureLan(peer);
        using var client = _http.CreateClient("peer");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await client.GetAsync(Url(peer, "/peer/games"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await ReadLimitedAsync(response, 4 << 20, timeout.Token).ConfigureAwait(false);
        var offers = GameShareJson.Deserialize<List<OfferedGameDto>>(System.Text.Encoding.UTF8.GetString(json));

        if (offers.Count > MaxOffersPerPeer) throw new InvalidDataException($"peer sent {offers.Count} offers, more than the limit of {MaxOffersPerPeer}");
        foreach (var o in offers)
        {
            if (!Sha256Hex().IsMatch(o.ContentHash) || !GameDefinitionFile.IsValidGameId(o.GameId) || string.IsNullOrWhiteSpace(o.Name)
                || o.Name.Length > 200 || o.TotalSize < 0)
                throw new InvalidDataException($"peer sent a malformed offer for '{o.Name}'");
        }
        return offers;
    }

    /// <summary>Downloads a manifest and its torrent from any peer that offers the version. Both are validated.</summary>
    /// <exception cref="KeyNotFoundException">Nobody offers it.</exception>
    /// <exception cref="HttpRequestException">Peers offer it but none could deliver a valid copy.</exception>
    public async Task<(GameManifest Manifest, byte[] Torrent)> FetchAsync(string contentHash, CancellationToken ct)
    {
        var sources = Offers.Where(o => o.Game.ContentHash == contentHash).Select(o => o.Peer).OrderBy(_ => Random.Shared.Next()).ToList();
        if (sources.Count == 0)
            throw new KeyNotFoundException("No PC on the LAN offers this game right now.");

        var failures = new List<string>();
        foreach (var peer in sources)
        {
            try
            {
                EnsureLan(peer);
                using var client = _http.CreateClient("peer");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                var manifestJson = await GetBytesAsync(client, Url(peer, $"/peer/games/{contentHash}/manifest"), MaxManifestBytes, cts.Token).ConfigureAwait(false);
                var manifest = GameShareJson.Deserialize<GameManifest>(System.Text.Encoding.UTF8.GetString(manifestJson));
                var problems = ManifestValidator.Validate(manifest);
                if (problems.Count > 0) throw new InvalidDataException(string.Join(" ", problems));
                if (manifest.ContentHash != contentHash) throw new InvalidDataException("the manifest is for a different game version than requested");

                var torrent = await GetBytesAsync(client, Url(peer, $"/peer/games/{contentHash}/torrent"), MaxTorrentBytes, cts.Token).ConfigureAwait(false);
                _log.LogInformation("Fetched manifest and torrent of {Name} from {Peer}", manifest.Name, peer.MachineName);
                return (manifest, torrent);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                _log.LogWarning("Could not fetch {Hash} from {Peer}: {Reason}", contentHash[..12], peer.MachineName, ex.Message);
                failures.Add($"{peer.MachineName}: {ex.Message}");
            }
        }
        throw new HttpRequestException($"None of the {sources.Count} PCs offering this game could deliver it. {string.Join("; ", failures)}");
    }

    private void EnsureLan(PeerInfo peer)
    {
        if (_options.LanOnly && !LanAddress.IsPrivate(peer.Address))
            throw new InvalidDataException($"{peer.Address} is not a local network address");
    }

    private static string Url(PeerInfo peer, string path) =>
        $"http://{(peer.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{peer.Address}]" : peer.Address.ToString())}:{peer.AgentPort}{path}";

    private static async Task<byte[]> GetBytesAsync(HttpClient client, string url, long limit, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response, limit, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a body but never more than <paramref name="limit"/> bytes, so a broken or hostile peer cannot exhaust memory.</summary>
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException($"response of {response.Content.Headers.ContentLength} bytes exceeds the limit of {limit}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > limit) throw new InvalidDataException($"response exceeds the limit of {limit} bytes");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private void Raise(IEnumerable<string> hashes)
    {
        try { Changed?.Invoke(this, new CatalogChange(hashes.Distinct().ToList())); }
        catch (Exception ex) { _log.LogError(ex, "A catalog change handler threw"); }
    }
}
