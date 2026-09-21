using GameShare.Core;
using GameShare.Discovery;
using GameShare.Protocol;
using GameShare.Torrent;

namespace GameShare.Agent;

/// <summary>Runs the agent's background work for as long as the service lives, with or without a GUI connected.</summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly DownloadManager _downloads;
    private readonly SeedManager _seeds;
    private readonly GameChangeTracker _changes;
    private readonly TrustService _trust;
    private readonly GameLibrary _library;
    private readonly ScanService _scan;
    private readonly DiscoveryService _discovery;
    private readonly PeerCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly TorrentEngine _engine;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _log;

    public AgentWorker(
        DownloadManager downloads, SeedManager seeds, GameChangeTracker changes, TrustService trust, GameLibrary library, ScanService scan, DiscoveryService discovery,
        PeerCatalog catalog, SettingsService settings, TorrentEngine engine, AgentOptions options, ILogger<AgentWorker> log)
    {
        _downloads = downloads;
        _seeds = seeds;
        _changes = changes;
        _trust = trust;
        _library = library;
        _scan = scan;
        _discovery = discovery;
        _catalog = catalog;
        _settings = settings;
        _engine = engine;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _seeds.Enabled = _settings.Current.SeedingEnabled;
        _settings.Changed += OnSettingsChanged;
        _library.GameDiscovered += OnGameDiscovered;
        _library.InstallationChanged += OnInstallationChanged;
        _changes.Changed += OnTrackedChange;
        _downloads.DownloadEventRaised += OnDownloadEvent;
        try
        {
            await _downloads.RecoverAsync(ct).ConfigureAwait(false);
            if (_seeds.Enabled) await _seeds.StartAllAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Agent running. Local API on 127.0.0.1:{Local}, peer API on port {Peer}, {Roots} game folder(s), seeding {Seeding}",
                _options.LocalApiPort, _options.PeerApiPort, _settings.Current.GameRoots.Count, _seeds.Enabled ? "on" : "off");

            await Task.WhenAll(
                _downloads.RunAsync(ct),
                _discovery.RunAsync(ct),
                _catalog.RunAsync(ct),
                ScanLoopAsync(ct),
                SeedResumeLoopAsync(ct),
                _changes.RunAsync(_options.ChangeWatchSyncInterval, ct),
                _trust.RunAsync(ct),
                DamagedSeedRecheckLoopAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
            _library.GameDiscovered -= OnGameDiscovered;
            _library.InstallationChanged -= OnInstallationChanged;
            _changes.Changed -= OnTrackedChange;
            _downloads.DownloadEventRaised -= OnDownloadEvent;

            // So the next start can skip re-hashing the library. Bounded, shutdown must not hang on it.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _seeds.SaveResumeDataAsync(limit.Token).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not store seed resume data before shutdown"); }
        }
    }

    private async Task SeedResumeLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.SeedResumeInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { await _seeds.SaveResumeDataAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Could not store seed resume data"); }
        }
    }

    /// <summary>A finished install, repair or update is a game folder to watch, or one that changed under the watcher.</summary>
    private void OnDownloadEvent(object? sender, DownloadEvent e)
    {
        if (e.Kind == DownloadEventKind.Completed) _changes.RequestSync();
    }

    // Damaged games that changed further since their seed was last checked. The seed still claims pieces that no longer match.
    private readonly Dictionary<long, Installation> _seedStale = [];

    /// <summary>
    /// A damaged game that keeps changing is re-checked now and then, not at every change, so a game that writes all the time
    /// does not make the disk read the whole game over and over. The first damage already re-checked it.
    /// </summary>
    private void OnTrackedChange(object? sender, TrackedChange change)
    {
        if (change.BecameDamaged) return;
        lock (_seedStale) _seedStale[change.Installation.Id] = change.Installation;
    }

    private async Task DamagedSeedRecheckLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.DamagedSeedRecheckInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Installation[] stale;
            lock (_seedStale) { stale = [.. _seedStale.Values]; _seedStale.Clear(); }
            foreach (var inst in stale)
            {
                try { await _seeds.RecheckAsync(inst, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Could not re-check the seed of {Path}", inst.InstallPath); }
            }
        }
    }

    /// <summary>
    /// A game whose files changed is not withdrawn. Its seed is re-checked so it stops claiming pieces that no longer match,
    /// and it keeps offering the ones that do. A game registered again with other content replaces the old seed.
    /// </summary>
    private void OnInstallationChanged(object? sender, InstallationChange change)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (change.Kind == InstallationChangeKind.Replaced)
                {
                    if (change.Previous is not null) await _seeds.StopAsync(change.Previous).ConfigureAwait(false); // its torrent no longer matches the files
                    await _seeds.StartAsync(change.Current).ConfigureAwait(false);
                }
                else
                {
                    await _seeds.RecheckAsync(change.Current).ConfigureAwait(false); // damaged or recovered, either way the files were looked at again
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not update seeding for {Path}", change.Current.InstallPath); }
        });
    }

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.RescanInterval);
        do
        {
            try
            {
                var r = await _scan.ScanAsync(ct).ConfigureAwait(false);
                foreach (var error in r.Errors) _log.LogWarning("Scan problem: {Error}", error);
            }
            catch (InvalidOperationException) { /* a manual scan is already running */ }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogError(ex, "Scheduled scan failed, will try again in {Interval}", _options.RescanInterval); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>A game found on disk, or finished downloading, is offered right away.</summary>
    private void OnGameDiscovered(object? sender, LibraryGame g)
    {
        if (g.Installation is null) return;
        _ = Task.Run(async () =>
        {
            try { await _seeds.StartAsync(g.Installation).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Cannot start seeding {Name}", g.Stored.Manifest.Name); }
        });
    }

    private void OnSettingsChanged(object? sender, SettingsDto s)
    {
        _engine.SetLimits(SettingsService.ToBytesPerSecond(s.MaxUploadMBps), SettingsService.ToBytesPerSecond(s.MaxDownloadMBps));
        _log.LogInformation("Settings changed: upload limit {Up} MB/s, download limit {Down} MB/s, seeding {Seeding}, {Roots} game folder(s)",
            s.MaxUploadMBps?.ToString() ?? "none", s.MaxDownloadMBps?.ToString() ?? "none", s.SeedingEnabled ? "on" : "off", s.GameRoots.Count);

        if (s.SeedingEnabled == _seeds.Enabled) return;
        _seeds.Enabled = s.SeedingEnabled;
        _ = Task.Run(async () =>
        {
            try
            {
                if (s.SeedingEnabled) await _seeds.StartAllAsync().ConfigureAwait(false);
                else await _seeds.StopAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not apply the seeding switch"); }
        });
    }
}
