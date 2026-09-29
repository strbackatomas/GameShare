using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using GameShare.Discovery;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;

namespace GameShare.Agent;

/// <summary>
/// Finds newer versions of GameShare itself, downloads them and checks them, so that the user only has to say "update".
///
/// A release is described by <c>update-&lt;build&gt;.json</c>, signed with the release key built into the program (<see cref="ReleaseKey"/>),
/// and only one for this build, newer than what runs, is taken. It is found at the configured places (GitHub by default) or at another
/// PC on the LAN that already has it. Its package comes from the LAN over the transfer port when a PC there holds it, otherwise as the zip
/// next to the description, after a random wait that gives the LAN the chance to have it first. Files the running program already has are
/// copied rather than downloaded. The zip's SHA-256 is checked before it is opened, only the files the signed manifest lists are unpacked,
/// and every file is checked again at the end, whichever way it came.
///
/// What passes lies in <c>updates\&lt;version&gt;\</c> in the data folder until it is applied, and is offered to the other PCs, as is the
/// package of the version that runs. Nothing is ever put in place from here; see AppUpdateFiles for that.
/// </summary>
public sealed class AppUpdateService
{
    public const string FolderName = "updates";

    /// <summary>The signed description, kept next to the package it describes.</summary>
    public const string ReleaseFileName = "release.json";

    /// <summary>What <see cref="AppUpdateStatusDto.Source"/> says for a package from another PC.</summary>
    public const string LanSource = "LAN";

    /// <summary>A stalled zip download is given up after this long without a byte, and tried again at the next check.</summary>
    private static readonly TimeSpan ZipStallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>A PC that offered a version counts as having it for this long.</summary>
    private static readonly TimeSpan LanSeenFor = TimeSpan.FromMinutes(10);

    private const int MaxOffers = 16;

    private readonly AgentOptions _options;
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _peerHttp;
    private readonly TorrentEngine _engine;
    private readonly DiscoveryService _discovery;
    private readonly SettingsService _settings;
    private readonly IAppUpdateApplier _applier;
    private readonly ILogger<AppUpdateService> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly SemaphoreSlim _seeding = new(1, 1);
    private readonly object _lock = new();

    // All guarded by _lock.
    private AppUpdateState _state;
    private Candidate? _known;
    private StagedRelease? _ready;
    private StagedRelease? _running;
    private long _done, _total;
    private string? _error;
    private string? _lastApplyError; // from the result the last update left, shown until the next attempt
    private DateTimeOffset? _lastChecked;
    private Task? _download;
    private CancellationToken _stopping;

    /// <summary>Versions of this build some PC on the LAN offered, and when it last did.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lanSeen = new(StringComparer.Ordinal);

    /// <summary>Where each version was found outside the LAN, so its zip can be fetched when the LAN does not deliver.</summary>
    private readonly ConcurrentDictionary<string, string> _zipSources = new(StringComparer.Ordinal);

    /// <summary>Packages offered to the other PCs, by version.</summary>
    private readonly Dictionary<string, TorrentTransfer> _seeds = new(StringComparer.Ordinal);

    private sealed record Candidate(AppRelease Release, byte[] Document, string Source);

    /// <summary>A version that is downloaded and checked. <see cref="PackageDir"/> holds exactly the files of its manifest.</summary>
    public sealed record StagedRelease(AppRelease Release, byte[] Document, string PackageDir, string? Source);

    public AppUpdateService(
        AgentOptions options, HttpClient http, IHttpClientFactory peerHttp, TorrentEngine engine, DiscoveryService discovery, SettingsService settings,
        IAppUpdateApplier applier, ILogger<AppUpdateService> log, TimeProvider? time = null)
    {
        _options = options;
        _http = http;
        _peerHttp = peerHttp;
        _engine = engine;
        _discovery = discovery;
        _settings = settings;
        _applier = applier;
        _log = log;
        _time = time ?? TimeProvider.System;
        UpdatesDir = Path.Combine(options.ResolveDataDir(), FolderName);
        _state = DisabledReason is null ? AppUpdateState.UpToDate : AppUpdateState.Disabled;
    }

    public string UpdatesDir { get; }

    /// <summary>Raised when anything in <see cref="Status"/> changed, so the GUI can show it.</summary>
    public event EventHandler? Changed;

    /// <summary>Null when this build updates itself, otherwise why it does not.</summary>
    public string? DisabledReason =>
        _options.UpdateFlavor is null ? "Toto sestavení nebylo vydáno přes scripts\\publish.ps1 a samo se neaktualizuje."
        : string.IsNullOrWhiteSpace(_options.UpdatePublicKey) ? "Toto sestavení nemá zabudovaný klíč vydání a samo se neaktualizuje."
        : null;

    /// <summary>The newer version that is downloaded and checked, if there is one.</summary>
    public StagedRelease? Ready { get { lock (_lock) return _ready; } }

    public AppUpdateStatusDto Status()
    {
        lock (_lock)
        {
            var release = _ready?.Release ?? _known?.Release;
            var cannot = _ready is not null && _state == AppUpdateState.Ready ? _applier.CannotApplyReason(_ready) : null;
            return new AppUpdateStatusDto(
                _options.RunningVersion, _options.UpdateFlavor, _state, release?.Version, release?.Notes, release?.ReleasedAt,
                _done, _total, _ready is not null ? _ready.Source : _known?.Source, _lastApplyError ?? _error, _lastChecked,
                CanApply: _ready is not null && _state == AppUpdateState.Ready && cannot is null, cannot, DisabledReason);
        }
    }

    // ---- offering to other PCs ----

    /// <summary>The packages this PC holds whole and checked, for the peer API. None while the user turned sharing off.</summary>
    public IReadOnlyList<AppUpdateOfferDto> Offers()
    {
        if (DisabledReason is not null || !_settings.Current.SeedingEnabled) return [];
        lock (_lock)
            return new[] { _running, _ready }.OfType<StagedRelease>().Select(s => new AppUpdateOfferDto(s.Release.Flavor, s.Release.Version)).ToList();
    }

    /// <summary>The signed description of a package in <see cref="Offers"/>, or null.</summary>
    public byte[]? OfferedDocument(string flavor, string version)
    {
        if (!Offers().Any(o => o.Flavor == flavor && o.Version == version)) return null;
        lock (_lock) return new[] { _running, _ready }.OfType<StagedRelease>().First(s => s.Release.Version == version).Document;
    }

    // ---- running ----

    /// <summary>Picks up versions downloaded before a restart, then looks for newer ones on the configured places and on the LAN, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (DisabledReason is { } reason)
        {
            _log.LogInformation("Updates of GameShare are off: {Reason}", reason);
            return;
        }
        lock (_lock) _stopping = ct;

        Directory.CreateDirectory(UpdatesDir);
        LockDown(UpdatesDir);
        AppUpdateFiles.CleanupLeftovers(_options.ResolveUpdateInstallDir());
        ReadLastResult();
        await LoadStagedAsync(ct).ConfigureAwait(false);
        await SeedStagedAsync(ct).ConfigureAwait(false);

        _discovery.PeerEventRaised += OnPeer;
        try
        {
            await Task.WhenAll(CheckLoopAsync(ct), LanLoopAsync(ct)).ConfigureAwait(false);
        }
        finally
        {
            _discovery.PeerEventRaised -= OnPeer;
            Task? download;
            lock (_lock) download = _download;
            if (download is not null) await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private async Task CheckLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.UpdateCheckInterval);
        do
        {
            try { await CheckAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Looking for a newer GameShare failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task LanLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.UpdateLanPollInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await Task.WhenAll(_discovery.Peers.Select(p => AskPeerAsync(p, ct))).ConfigureAwait(false);
            // A seed that could not start earlier (sharing was off) starts once it is on again, and stops when it is turned off.
            await SeedStagedAsync(ct).ConfigureAwait(false);
            // Old files the last update could not delete, because the client still ran from them, go once it has restarted.
            AppUpdateFiles.CleanupLeftovers(_options.ResolveUpdateInstallDir());
        }
    }

    /// <summary>A PC that joins, or starts announcing another version, is asked at once rather than at the next round.</summary>
    private void OnPeer(object? sender, PeerEvent e)
    {
        if (e.Kind == PeerEventKind.Left) return;
        if (e.Kind == PeerEventKind.Changed && e.Previous?.AppVersion == e.Peer.AppVersion) return;
        CancellationToken ct;
        lock (_lock) ct = _stopping;
        _ = Task.Run(() => AskPeerAsync(e.Peer, ct), CancellationToken.None);
    }

    /// <summary>Which packages a PC holds, and the description of one that is newer than anything known here. Never throws.</summary>
    private async Task AskPeerAsync(PeerInfo peer, CancellationToken ct)
    {
        try
        {
            if (_options.LanOnly && !LanAddress.IsPrivate(peer.Address)) return;
            using var client = _peerHttp.CreateClient("peer");
            var list = await SourceFetch.ReadAsync(client, PeerCatalog.Url(peer, "/peer/app-update"), 64 * 1024, "list of update packages", ct).ConfigureAwait(false);
            var offers = GameShareJson.Deserialize<List<AppUpdateOfferDto>>(System.Text.Encoding.UTF8.GetString(list));

            foreach (var offer in offers.Where(o => o.Flavor == _options.UpdateFlavor).Take(MaxOffers))
            {
                if (!SemVer.TryParse(offer.Version, out _)) continue;
                _lanSeen[offer.Version] = _time.GetUtcNow();
                if (!WouldTake(offer.Version)) continue;

                var document = await SourceFetch.ReadAsync(client,
                    PeerCatalog.Url(peer, $"/peer/app-update/{Uri.EscapeDataString(offer.Flavor)}/{Uri.EscapeDataString(offer.Version)}"),
                    TrustSigning.MaxEnvelopeBytes, "release description", ct).ConfigureAwait(false);
                var release = ReleaseSigning.Open(document, _options.UpdatePublicKey!);
                if (release.Version != offer.Version) throw new InvalidDataException($"It offered {offer.Version} and sent {release.Version}.");
                Offer(release, document, LanSource);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogDebug("Could not ask {Peer} about updates: {Reason}", peer.MachineName, ex.Message);
        }
    }

    /// <summary>Whether a version would be taken: newer than what runs, what is here and what is being fetched.</summary>
    private bool WouldTake(string version)
    {
        lock (_lock)
            return SemVer.IsNewer(version, _options.RunningVersion)
                && (_ready is null || SemVer.IsNewer(version, _ready.Release.Version))
                && _download is null;
    }

    /// <summary>Asks every configured place for the release of this build. A failure never throws, it is recorded in the status.</summary>
    public async Task<AppUpdateStatusDto> CheckAsync(CancellationToken ct = default)
    {
        if (DisabledReason is not null) return Status();

        await _checking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sources = _options.UpdateSources();
            Candidate? best = null;
            var failures = new List<string>();
            foreach (var source in sources)
            {
                try
                {
                    var location = SourceFetch.Combine(source, AppRelease.FileName(_options.UpdateFlavor!));
                    var document = await SourceFetch.ReadAsync(_http, location, TrustSigning.MaxEnvelopeBytes, "release description", ct).ConfigureAwait(false);
                    var release = ReleaseSigning.Open(document, _options.UpdatePublicKey!);
                    if (release.ZipName is not null) _zipSources[release.Version] = source;
                    if (best is null || SemVer.IsNewer(release.Version, best.Release.Version)) best = new Candidate(release, document, source);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                {
                    var reason = ex is TaskCanceledException ? "did not answer in time." : ex.Message;
                    failures.Add(sources.Count > 1 ? $"{source}: {reason}" : reason);
                }
            }

            string? error = best is null && failures.Count > 0 ? $"Nepodařilo se zjistit, zda je novější verze: {string.Join(" ", failures)}" : null;
            if (error is not null) _log.LogWarning("Could not look for a newer GameShare: {Reasons}", string.Join("; ", failures));
            else if (failures.Count > 0) _log.LogInformation("Some update places could not be read, another one was: {Reasons}", string.Join("; ", failures));

            lock (_lock)
            {
                _lastChecked = _time.GetUtcNow();
                if (_state != AppUpdateState.Downloading) _error = error;
            }
            if (best is null || !Offer(best.Release, best.Document, best.Source)) RaiseChanged();
        }
        finally { _checking.Release(); }
        return Status();
    }

    /// <summary>
    /// Takes a release found somewhere, if it is for this build and newer than both what runs and what is already here,
    /// and starts downloading it. The signature was checked by whoever found it.
    /// </summary>
    /// <returns>Whether it was taken.</returns>
    internal bool Offer(AppRelease release, byte[] document, string source)
    {
        lock (_lock)
        {
            try { ReleaseSigning.CheckApplies(release, _options.UpdateFlavor!, _options.RunningVersion, heldVersion: _ready?.Release.Version); }
            catch (InvalidDataException ex)
            {
                _log.LogDebug("Release {Version} from {Source} is not taken: {Reason}", release.Version, source, ex.Message);
                return false;
            }
            if (_ready?.Release.Version == release.Version) return false;
            if (_download is not null) return false; // one at a time; a newer one is taken at the next check, when this one is done

            _known = new Candidate(release, document, source);
            _state = AppUpdateState.Downloading;
            _done = 0;
            _total = release.Manifest.TotalSize;
            _error = null;
            var ct = _stopping;
            _download = Task.Run(() => DownloadAsync(_known, ct), CancellationToken.None);
        }
        _log.LogInformation("GameShare {Version} found at {Source}, downloading it", release.Version, source);
        RaiseChanged();
        return true;
    }

    // ---- applying ----

    /// <summary>
    /// Puts the version that is ready in place, when the user asks. Its files are checked once more first, all of them: they have lain
    /// on disk since they were downloaded. Returns once the build's applier has taken over; the agent usually stops moments later.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is ready, or it cannot be applied here. The message says why.</exception>
    /// <exception cref="InvalidDataException">The files changed since they were checked. They are removed, and downloaded again later.</exception>
    public async Task<AppUpdateStatusDto> ApplyAsync(CancellationToken ct = default)
    {
        StagedRelease staged;
        IReadOnlyList<string>? previous;
        lock (_lock)
        {
            if (DisabledReason is { } disabled) throw new InvalidOperationException(disabled);
            if (_state == AppUpdateState.Applying) throw new InvalidOperationException("Aktualizace se už instaluje.");
            staged = _ready ?? throw new InvalidOperationException("Žádná novější verze zatím není stažená.");
            if (_applier.CannotApplyReason(staged) is { } reason) throw new InvalidOperationException(reason);
            previous = _running?.Release.Manifest.Files.Select(f => f.Path).ToList();
            _state = AppUpdateState.Applying;
            _error = null;
            _lastApplyError = null;
        }
        RaiseChanged();

        try
        {
            var check = await ManifestVerifier.VerifyAsync(staged.Release.Manifest, staged.PackageDir, VerifyMode.Full, cancellationToken: ct).ConfigureAwait(false);
            if (!check.IsValid)
            {
                await StopSeedAsync(staged.Release.Version).ConfigureAwait(false);
                lock (_lock) _ready = null;
                DeleteQuietly(Path.GetDirectoryName(staged.PackageDir)!);
                throw new InvalidDataException($"Stažené soubory verze {staged.Release.Version} se od kontroly změnily ({check}), stáhnou se znovu.");
            }

            _log.LogInformation("Applying GameShare {Version}", staged.Release.Version);
            await _applier.ApplyAsync(new AppUpdateApplyContext(staged, previous, UpdatesDir), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _state = _ready is null ? AppUpdateState.UpToDate : AppUpdateState.Ready;
                _error = ex.Message;
            }
            RaiseChanged();
            throw;
        }
        return Status();
    }

    /// <summary>What the last update did, written by whoever applied it. A failure is shown until something else happens; the version stays ready to try again.</summary>
    private void ReadLastResult()
    {
        var path = Path.Combine(UpdatesDir, AppUpdateResult.FileName);
        try
        {
            if (!File.Exists(path)) return;
            var result = GameShareJson.Deserialize<AppUpdateResult>(File.ReadAllText(path));
            File.Delete(path);
            if (result.Ok) _log.LogInformation("The update to GameShare {Version} succeeded", result.Version);
            else
            {
                _log.LogWarning("The update to GameShare {Version} failed: {Error}", result.Version, result.Error);
                lock (_lock) _lastApplyError = result.Error;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.LogWarning("Could not read what the last update did: {Reason}", ex.Message);
        }
    }

    // ---- downloading ----

    private bool OnLan(string version) =>
        _lanSeen.TryGetValue(version, out var seen) && _time.GetUtcNow() - seen < LanSeenFor;

    private async Task DownloadAsync(Candidate candidate, CancellationToken ct)
    {
        var release = candidate.Release;
        var versionDir = Path.Combine(UpdatesDir, release.Version);
        var partial = versionDir + ".partial";
        var packageDir = Path.Combine(partial, release.Manifest.FolderName);
        string? source = candidate.Source;
        try
        {
            DeleteQuietly(partial);
            Directory.CreateDirectory(packageDir);
            bool complete = await PrefillAsync(release.Manifest, packageDir, ct).ConfigureAwait(false);

            if (!complete && candidate.Source != LanSource && !OnLan(release.Version))
                await WaitForLanAsync(release.Version, ct).ConfigureAwait(false);

            if (!complete && OnLan(release.Version))
            {
                try
                {
                    await FetchFromLanAsync(release, partial, ct).ConfigureAwait(false);
                    complete = true;
                    source = LanSource;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && _zipSources.ContainsKey(release.Version) && release.ZipName is not null)
                {
                    _log.LogInformation("GameShare {Version} did not come from the LAN, fetching the zip instead: {Reason}", release.Version, ex.Message);
                }
            }

            if (!complete)
            {
                if (release.ZipName is null || !_zipSources.TryGetValue(release.Version, out var zipSource))
                    throw new InvalidDataException("No PC on the LAN sends it, and there is no zip to download it from.");
                source = zipSource;
                lock (_lock) _known = _known! with { Source = zipSource };
                var zip = partial + ".zip";
                try
                {
                    await FetchZipAsync(SourceFetch.Combine(zipSource, release.ZipName), release, zip, ct).ConfigureAwait(false);
                    Unpack(zip, release.Manifest, packageDir);
                }
                finally { TryDelete(zip); }
            }

            var check = await ManifestVerifier.VerifyAsync(release.Manifest, packageDir, VerifyMode.Full, cancellationToken: ct).ConfigureAwait(false);
            if (!check.IsValid) throw new InvalidDataException($"The downloaded files of version {release.Version} are not what was signed: {check}.");
            await File.WriteAllBytesAsync(Path.Combine(partial, ReleaseFileName), candidate.Document, ct).ConfigureAwait(false);

            DeleteQuietly(versionDir);
            Directory.Move(partial, versionDir);
            var staged = new StagedRelease(release, candidate.Document, Path.Combine(versionDir, release.Manifest.FolderName), source);
            StagedRelease? replaced;
            lock (_lock)
            {
                replaced = _ready;
                _ready = staged;
                _state = AppUpdateState.Ready;
                _done = _total;
                _error = null;
                _download = null;
            }
            if (replaced is not null) await StopSeedAsync(replaced.Release.Version).ConfigureAwait(false);
            RemoveOtherVersions();
            _log.LogInformation("GameShare {Version} is downloaded and checked ({Source}), it is applied when the user asks", release.Version, source);
            await SeedStagedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DeleteQuietly(partial);
            lock (_lock)
            {
                _download = null;
                _state = _ready is null ? AppUpdateState.Available : AppUpdateState.Ready;
                _error = ct.IsCancellationRequested ? null : $"Stažení verze {release.Version} se nepovedlo: {(ex is TaskCanceledException ? "zdroj přestal odpovídat." : ex.Message)}";
            }
            if (!ct.IsCancellationRequested) _log.LogWarning("Downloading GameShare {Version} failed, it is tried again later: {Reason}", release.Version, ex.Message);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Copies the files of the new version that the running program already has, so they are not downloaded again. Usually most of them:
    /// the .NET runtime and the libraries rarely change between two versions.
    /// </summary>
    /// <returns>Whether that was every file.</returns>
    private async Task<bool> PrefillAsync(GameManifest manifest, string packageDir, CancellationToken ct)
    {
        var install = _options.ResolveUpdateInstallDir();
        if (!Directory.Exists(install)) return false;
        int copied = 0;
        foreach (var file in manifest.Files)
        {
            var have = Path.Combine(install, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var info = new FileInfo(have);
                if (!info.Exists || info.Length != file.Size) continue;
                if (await ManifestVerifier.HashFileAsync(have, ct).ConfigureAwait(false) != file.Hash) continue;
                var target = Path.Combine(packageDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(have, target, overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* downloaded instead */ }
        }
        if (copied > 0) _log.LogInformation("{Copied} of {Total} files of the new version are unchanged and were copied, not downloaded", copied, manifest.Files.Count);
        return copied == manifest.Files.Count;
    }

    /// <summary>
    /// Every PC finds a new release on the internet at about the same time. Each waits a random while first, and one that meanwhile
    /// sees a PC on the LAN with it takes it from there: the internet connection of a LAN party carries it once, not thirty times.
    /// </summary>
    private async Task WaitForLanAsync(string version, CancellationToken ct)
    {
        var wait = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * _options.UpdateInternetDelay.TotalMilliseconds);
        var until = _time.GetUtcNow() + wait;
        while (_time.GetUtcNow() < until && !OnLan(version))
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, (until - _time.GetUtcNow()).TotalMilliseconds))), _time, ct).ConfigureAwait(false);
    }

    /// <summary>The package over the transfer port from the PCs that hold it, into <paramref name="savePath"/>, over what <see cref="PrefillAsync"/> put there.</summary>
    private async Task FetchFromLanAsync(AppRelease release, string savePath, CancellationToken ct)
    {
        var transfer = await _engine.AddAsync(release.Torrent, savePath, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            // The same check a game's download gets: the torrent is the one the signed manifest names.
            if (!string.Equals(transfer.InfoHash, release.Manifest.TorrentInfoHash, StringComparison.OrdinalIgnoreCase)
                || transfer.Name != release.Manifest.FolderName || transfer.TotalSize != release.Manifest.TotalSize)
                throw new InvalidDataException($"The torrent of version {release.Version} does not match its manifest.");

            transfer.Start();
            long lastBytes = -1;
            var lastProgress = _time.GetUtcNow();
            while (true)
            {
                var status = transfer.GetStatus();
                if (status.State == TransferState.Complete) break;
                if (status.State == TransferState.Error) throw new IOException($"The transfer of version {release.Version} failed.");
                lock (_lock) _done = Math.Min(status.BytesDone, _total);
                RaiseChangedThrottled();

                // Checking what is already there counts as moving: it is local work, not a silent LAN.
                if (status.BytesDone != lastBytes || status.State == TransferState.Checking)
                {
                    lastBytes = status.BytesDone;
                    lastProgress = _time.GetUtcNow();
                }
                else if (_time.GetUtcNow() - lastProgress > _options.UpdateLanStall)
                    throw new TimeoutException($"No PC on the LAN sent anything for {_options.UpdateLanStall.TotalSeconds:N0} s.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Let go of the files before they are checked and moved. The package is offered again from its final folder.
            await _engine.RemoveAsync(transfer, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The zip, with its SHA-256 checked before anything in it is looked at, and never more than the package could need.</summary>
    private async Task FetchZipAsync(string location, AppRelease release, string target, CancellationToken ct)
    {
        long limit = release.Manifest.TotalSize + 64L * 1024 * 1024; // compressed it is smaller; this only stops a server that never ends
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Stream source;
        HttpResponseMessage? response = null;
        if (SourceFetch.IsWeb(location, out var uri))
        {
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException($"{release.ZipName} is larger than the package it holds.");
            source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        else
        {
            if (!File.Exists(location)) throw new FileNotFoundException($"{location} does not exist or cannot be reached.");
            source = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        try
        {
            await using (source)
            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long total = 0;
                int read;
                while (true)
                {
                    stall.CancelAfter(ZipStallTimeout);
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > limit) throw new InvalidDataException($"{release.ZipName} is larger than the package it holds.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    lock (_lock) _done = Math.Min(total, _total);
                    RaiseChangedThrottled();
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), release.ZipSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{release.ZipName} is not the file that was signed, it was changed or damaged on the way.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{location} sent nothing for {ZipStallTimeout.TotalMinutes:N0} minutes.");
        }
        finally { response?.Dispose(); }
    }

    /// <summary>
    /// Unpacks only the files the signed manifest lists, each with the size it lists. Any other entry, a path outside the folder among
    /// them, fails the whole package instead of being skipped: a zip that does not match its description is not used at all.
    /// Files already copied from the running program are replaced, the zip is what was signed.
    /// </summary>
    private static void Unpack(string zipPath, GameManifest manifest, string targetDir)
    {
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue; // a folder
            if (!expected.TryGetValue(entry.FullName, out var file))
                throw new InvalidDataException($"The zip holds '{entry.FullName}', which is not part of the signed package.");
            if (entry.Length != file.Size || !seen.Add(file.Path))
                throw new InvalidDataException($"'{entry.FullName}' in the zip is not the file of the signed package.");
        }
        if (seen.Count != expected.Count)
            throw new InvalidDataException($"The zip lacks {expected.Count - seen.Count} file(s) of the signed package.");

        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            // Safe: every name was matched against the manifest above, whose paths were checked to stay inside the folder when the release was opened.
            var target = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    // ---- what lies on disk ----

    /// <summary>
    /// A version downloaded before a restart is still offered to be applied, after a quick look that its files are all there. The package
    /// of the version that runs, the one applied last, is kept too: it is what the other PCs of the LAN may still need.
    /// </summary>
    private async Task LoadStagedAsync(CancellationToken ct)
    {
        foreach (var leftover in Directory.EnumerateDirectories(UpdatesDir, "*.partial")) DeleteQuietly(leftover);
        foreach (var leftover in Directory.EnumerateFiles(UpdatesDir, "*.partial.zip")) TryDelete(leftover);

        StagedRelease? best = null, running = null;
        foreach (var dir in Directory.EnumerateDirectories(UpdatesDir))
        {
            try
            {
                var document = await File.ReadAllBytesAsync(Path.Combine(dir, ReleaseFileName), ct).ConfigureAwait(false);
                var release = ReleaseSigning.Open(document, _options.UpdatePublicKey!);
                bool isRunning = release.Version == _options.RunningVersion && release.Flavor == _options.UpdateFlavor;
                if (!isRunning) ReleaseSigning.CheckApplies(release, _options.UpdateFlavor!, _options.RunningVersion);
                if (!string.Equals(Path.GetFileName(dir), release.Version, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"It holds version {release.Version}.");
                var packageDir = Path.Combine(dir, release.Manifest.FolderName);
                var check = await ManifestVerifier.VerifyAsync(release.Manifest, packageDir, VerifyMode.Quick, cancellationToken: ct).ConfigureAwait(false);
                if (!check.IsValid) throw new InvalidDataException(check.ToString());

                var staged = new StagedRelease(release, document, packageDir, null);
                if (isRunning) running = staged;
                else if (best is null || SemVer.IsNewer(release.Version, best.Release.Version)) best = staged;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // An older version, another build, or damaged: of no use, and a newer one is downloaded again if there is one.
                _log.LogInformation("Removing {Dir}, it holds no usable update: {Reason}", dir, ex.Message);
                DeleteQuietly(dir);
            }
        }

        lock (_lock)
        {
            _running = running;
            if (best is not null)
            {
                _ready = best;
                _state = AppUpdateState.Ready;
                _done = _total = best.Release.Manifest.TotalSize;
            }
        }
        RemoveOtherVersions();
        if (best is not null)
        {
            _log.LogInformation("GameShare {Version} was downloaded earlier and is ready to be applied", best.Release.Version);
            RaiseChanged();
        }
    }

    /// <summary>Offers the packages here to the other PCs over the transfer port, as long as sharing is on. Stops them when it is turned off.</summary>
    private async Task SeedStagedAsync(CancellationToken ct)
    {
        await _seeding.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StagedRelease[] staged;
            lock (_lock) staged = new[] { _running, _ready }.OfType<StagedRelease>().ToArray();
            bool on = _settings.Current.SeedingEnabled;

            foreach (var version in _seeds.Keys.ToList())
                if (!on || staged.All(s => s.Release.Version != version))
                    await StopSeedLockedAsync(version).ConfigureAwait(false);
            if (!on) return;

            foreach (var s in staged.Where(s => !_seeds.ContainsKey(s.Release.Version)))
            {
                try
                {
                    // Upload only: the files are never written, and the library checks them first, so it offers only what matches.
                    var transfer = await _engine.AddAsync(s.Release.Torrent, Path.GetDirectoryName(s.PackageDir)!, uploadOnly: true, cancellationToken: ct).ConfigureAwait(false);
                    transfer.Start();
                    _seeds[s.Release.Version] = transfer;
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
                {
                    _log.LogWarning("Could not offer the update package {Version} to the LAN: {Reason}", s.Release.Version, ex.Message);
                }
            }
        }
        finally { _seeding.Release(); }
    }

    private async Task StopSeedAsync(string version)
    {
        await _seeding.WaitAsync().ConfigureAwait(false);
        try { await StopSeedLockedAsync(version).ConfigureAwait(false); }
        finally { _seeding.Release(); }
    }

    private async Task StopSeedLockedAsync(string version)
    {
        if (!_seeds.Remove(version, out var transfer)) return;
        try { await _engine.RemoveAsync(transfer, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not stop offering the update package {Version}", version); }
    }

    /// <summary>Only the version that runs and the one that is ready are kept.</summary>
    private void RemoveOtherVersions()
    {
        HashSet<string> keep;
        lock (_lock) keep = new[] { _running, _ready }.OfType<StagedRelease>().Select(s => s.Release.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(UpdatesDir))
            if (!keep.Contains(Path.GetFileName(dir)) && !dir.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                DeleteQuietly(dir);
    }

    /// <summary>
    /// When the agent runs as the service, what lies here is later run as SYSTEM. The data folder under ProgramData lets every user
    /// create files, so this folder is closed to everyone but SYSTEM and the administrators: nobody can slip a file in between the check and the use.
    /// </summary>
    private void LockDown(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var me = WindowsIdentity.GetCurrent();
        if (!me.IsSystem) return; // a portable build runs as the user, who owns the folder anyway
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _log.LogWarning("Could not restrict access to {Dir}: {Reason}", dir, ex.Message);
        }
    }

    private long _lastProgressEvent;

    /// <summary>Progress is pushed to the GUI at most twice a second, not for every megabyte.</summary>
    private void RaiseChangedThrottled()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgressEvent) < 500) return;
        Interlocked.Exchange(ref _lastProgressEvent, now);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "A listener to update changes failed"); }
    }

    private static void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tried again next time */ }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tried again next time */ }
    }
}
