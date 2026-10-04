using GameShare.Core;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>Runs library scans one at a time. Hashing a large library takes minutes and two scans would fight over the same disk.</summary>
public sealed class ScanService
{
    private readonly GameLibrary _library;
    private readonly SourceLibrary _sources;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly ILogger<ScanService> _log;

    public ScanService(GameLibrary library, SourceLibrary sources, SettingsService settings, ILogger<ScanService> log)
    {
        _library = library;
        _sources = sources;
        _settings = settings;
        _log = log;
    }

    public bool IsRunning => _running.CurrentCount == 0;

    private volatile ScanProgressDto? _progress;

    /// <summary>How far the running scan got, null when none runs. Whoever started it, the client can show it.</summary>
    public ScanProgressDto? Progress => IsRunning ? _progress : null;

    /// <exception cref="InvalidOperationException">A scan is already running.</exception>
    public async Task<ScanResultDto> ScanAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("A scan is already running. Wait for it to finish.");
        try
        {
            var roots = _settings.Current.GameRoots;
            var source = string.IsNullOrEmpty(_settings.Current.SourceRoot) ? null : _settings.Current.SourceRoot;
            _progress = new ScanProgressDto(0, 0, "", 0, 0);
            // Written from the hashing thread for every block, read by whoever asks. Only the latest matters.
            var progress = new Reporter(p => _progress = new ScanProgressDto(p.Folder, p.Folders, p.Name, p.Bytes, p.TotalBytes));
            var s = roots.Count == 0 ? null : await _library.ScanAsync(roots, ct, progress).ConfigureAwait(false);
            if (s is null) _log.LogInformation("Scan of game folders skipped: none are configured");

            // After the game folders, so a copy of a game played here is recognised by the version the scan just found.
            // Always, also without a source folder: copies of one that was removed from the settings are forgotten.
            var errors = new List<string>(s?.Errors ?? []);
            var missing = new List<string>(s?.MissingRoots ?? []);
            if (source is not null && !Directory.Exists(source)) missing.Add(source); // its copies are forgotten until it is back
            var sources = await _sources.ScanAsync(source, ct, progress).ConfigureAwait(false);
            errors.AddRange(sources.Problems.Select(p => $"Zdrojová složka: {p}"));
            return new ScanResultDto(s?.Added ?? 0, s?.Unchanged ?? 0, s?.Skipped ?? 0, errors, missing, s?.Damaged ?? []);
        }
        finally
        {
            _progress = null;
            _running.Release();
        }
    }

    private sealed class Reporter(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
