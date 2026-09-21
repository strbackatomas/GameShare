using GameShare.Core;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>Runs library scans one at a time. Hashing a large library takes minutes and two scans would fight over the same disk.</summary>
public sealed class ScanService
{
    private readonly GameLibrary _library;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly ILogger<ScanService> _log;

    public ScanService(GameLibrary library, SettingsService settings, ILogger<ScanService> log)
    {
        _library = library;
        _settings = settings;
        _log = log;
    }

    public bool IsRunning => _running.CurrentCount == 0;

    /// <exception cref="InvalidOperationException">A scan is already running.</exception>
    public async Task<ScanResultDto> ScanAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("A scan is already running. Wait for it to finish.");
        try
        {
            var roots = _settings.Current.GameRoots;
            if (roots.Count == 0)
            {
                _log.LogInformation("Scan skipped: no game folders are configured");
                return new ScanResultDto(0, 0, 0, [], [], []);
            }
            var s = await _library.ScanAsync(roots, ct).ConfigureAwait(false);
            return new ScanResultDto(s.Added, s.Unchanged, s.Skipped, s.Errors, s.MissingRoots, s.Damaged);
        }
        finally { _running.Release(); }
    }
}
