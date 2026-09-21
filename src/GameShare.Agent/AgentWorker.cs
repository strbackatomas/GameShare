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
    private readonly GameLibrary _library;
    private readonly ScanService _scan;
    private readonly DiscoveryService _discovery;
    private readonly PeerCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly TorrentEngine _engine;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _log;

    public AgentWorker(
        DownloadManager downloads, SeedManager seeds, GameLibrary library, ScanService scan, DiscoveryService discovery,
        PeerCatalog catalog, SettingsService settings, TorrentEngine engine, AgentOptions options, ILogger<AgentWorker> log)
    {
        _downloads = downloads;
        _seeds = seeds;
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
                SeedResumeLoopAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
            _library.GameDiscovered -= OnGameDiscovered;
            _library.InstallationChanged -= OnInstallationChanged;

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
