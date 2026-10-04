using GameShare.Core.Data;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>The settings a user can change while the agent runs. Stored in the database, validated on the way in.</summary>
public sealed class SettingsService
{
    private const string Key = "agent.settings";
    private const int MaxRoots = 32;

    private readonly GameShareDb _db;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile SettingsDto _current;

    private SettingsService(GameShareDb db, SettingsDto current)
    {
        _db = db;
        _current = current;
    }

    public static async Task<SettingsService> LoadAsync(GameShareDb db, IEnumerable<string> initialRoots, CancellationToken ct = default)
    {
        var json = await db.GetSettingAsync(Key, ct).ConfigureAwait(false);
        var settings = json is null
            ? Normalize(new SettingsDto([.. initialRoots], SeedingEnabled: true, MaxUploadMBps: null, MaxDownloadMBps: null, TorrentDebugLogging: false))
            : Normalize(GameShareJson.Deserialize<SettingsDto>(json));
        return new SettingsService(db, settings);
    }

    public SettingsDto Current => _current;

    /// <summary>Raised after a change has been saved.</summary>
    public event EventHandler<SettingsDto>? Changed;

    /// <exception cref="ArgumentException">A value is invalid. The message says which and why.</exception>
    public async Task<SettingsDto> UpdateAsync(SettingsDto requested, CancellationToken ct = default)
    {
        // A client that does not know the tuning sends none: keep what there is rather than clearing it.
        if (requested.Tuning is null) requested = requested with { Tuning = _current.Tuning };
        if (requested.SourceRoot is null) requested = requested with { SourceRoot = _current.SourceRoot ?? "" };
        var settings = Normalize(requested);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.SetSettingAsync(Key, GameShareJson.Serialize(settings), ct).ConfigureAwait(false);
            _current = settings;
        }
        finally { _writeLock.Release(); }

        Changed?.Invoke(this, settings);
        return settings;
    }

    /// <summary>Whether <paramref name="root"/> is one of the configured game roots. Both sides are compared as full paths.</summary>
    public bool IsConfiguredRoot(string root)
    {
        var full = TrimRoot(Path.GetFullPath(root));
        return _current.GameRoots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));
    }

    private static SettingsDto Normalize(SettingsDto s)
    {
        if (s.GameRoots is null) throw new ArgumentException("GameRoots is required, send an empty list for none.");
        if (s.GameRoots.Count > MaxRoots) throw new ArgumentException($"At most {MaxRoots} game folders are supported.");

        var roots = new List<string>();
        foreach (var raw in s.GameRoots)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("A game folder is empty.");
            if (!Path.IsPathFullyQualified(raw)) throw new ArgumentException($"Game folder '{raw}' must be a full path such as D:\\Games.");
            var full = TrimRoot(Path.GetFullPath(raw));
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        CheckLimit(s.MaxUploadMBps, nameof(s.MaxUploadMBps));
        CheckLimit(s.MaxDownloadMBps, nameof(s.MaxDownloadMBps));

        var unignored = (s.AllowedVirtualAdapterIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        return new SettingsDto(roots, s.SeedingEnabled, s.MaxUploadMBps, s.MaxDownloadMBps, unignored, s.TorrentDebugLogging)
        {
            Tuning = CheckTuning(s.Tuning ?? new TransferTuningDto()),
            SourceRoot = CheckSourceRoot(s.SourceRoot, roots),
        };
    }

    /// <summary>"" when there is none. Never inside a game folder or around one: a copy there would be played, and that is what it must not be.</summary>
    private static string CheckSourceRoot(string? raw, IReadOnlyList<string> gameRoots)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (!Path.IsPathFullyQualified(raw)) throw new ArgumentException($"Source folder '{raw}' must be a full path such as E:\\GameShare-zdroj.");
        var full = TrimRoot(Path.GetFullPath(raw));
        foreach (var root in gameRoots)
            if (Contains(root, full) || Contains(full, root))
                throw new ArgumentException($"Source folder '{full}' must be apart from the game folder '{root}': neither may be inside the other.");
        return full;
    }

    private static bool Contains(string outer, string inner) =>
        string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase)
        || inner.StartsWith(outer.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static TransferTuningDto CheckTuning(TransferTuningDto t)
    {
        static void Range(int? value, int min, int max, string what)
        {
            if (value is { } v && (v < min || v > max)) throw new ArgumentException($"{what} must be between {min} and {max}, or empty for the default.");
        }
        Range(t.OpenFiles, 8, 10_000, "Open files");
        Range(t.SendBufferKb, 16, 256 * 1024, "Send buffer (KiB)");
        Range(t.DiskQueueKb, 64, 1024 * 1024, "Disk queue (KiB)");
        if (t.UploadSlots is { } slots && slots != -1) Range(slots, 1, 1000, "Upload slots");
        Range(t.RequestQueue, 10, 10_000, "Request queue");
        Range(t.DiskThreads, 1, 64, "Disk threads");
        return t;
    }

    private static void CheckLimit(int? value, string name)
    {
        // The transfer engine takes bytes per second as a 32-bit number, so about 2100 MB/s is the ceiling. 10 GbE is about 1250 MB/s.
        if (value is < 1 or > 2000) throw new ArgumentException($"{name} must be between 1 and 2000 MB/s, or empty for unlimited.");
    }

    /// <summary>"D:\Games\" and "D:\Games" are the same folder. A drive root such as "D:\" keeps its slash.</summary>
    private static string TrimRoot(string path) =>
        path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    /// <summary>Megabytes per second in the settings, bytes per second for the transfer engine.</summary>
    public static int? ToBytesPerSecond(int? megabytes) => megabytes is null ? null : checked(megabytes.Value * 1_000_000);
}
