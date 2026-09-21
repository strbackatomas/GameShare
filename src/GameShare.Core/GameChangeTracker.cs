using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;
using Microsoft.Extensions.Logging;

namespace GameShare.Core;

/// <summary>What was seen changing in a game's folder since the game was last intact.</summary>
/// <param name="Modified">Files of the game that changed size or content, or were deleted.</param>
/// <param name="Added">Files the game created that are not part of it.</param>
/// <param name="SuggestedPatterns">Volatile patterns that would make these files stop counting. Suggestions only, never applied by themselves.</param>
public sealed record ObservedChanges(IReadOnlyList<string> Modified, IReadOnlyList<string> Added, IReadOnlyList<string> SuggestedPatterns)
{
    public static readonly ObservedChanges None = new([], [], []);
    public int Count => Modified.Count + Added.Count;
}

/// <param name="Installation">The game the change was seen in.</param>
/// <param name="BecameDamaged">This change is what turned the game from installed to damaged. Its seed was re-checked because of that already.</param>
public sealed record TrackedChange(Installation Installation, bool BecameDamaged);

/// <summary>
/// Notices while a game is played which files it rewrites, so the volatile patterns can be suggested without anyone running a check.
/// It watches every installed game folder, waits until the folder has been quiet for a moment, then looks only at the files
/// that were touched. A content file that really differs marks the game damaged. Nothing here ever repairs, deletes or registers.
/// </summary>
public sealed class GameChangeTracker : IDisposable
{
    private const int MaxPendingPaths = 5000;
    private const int MaxSuggestions = 20;

    private readonly GameShareDb _db;
    private readonly GameLibrary _library;
    private readonly ILogger _log;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxWait;
    private readonly Dictionary<long, Watch> _watches = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private volatile bool _syncRequested;

    /// <summary>Raised when what was seen for a game changed.</summary>
    public event EventHandler<TrackedChange>? Changed;

    /// <param name="quiet">How long a folder must be still before the touched files are looked at.</param>
    /// <param name="maxWait">A folder that never goes quiet is looked at after this long anyway.</param>
    public GameChangeTracker(GameShareDb db, GameLibrary library, ILogger<GameChangeTracker> log, TimeSpan? quiet = null, TimeSpan? maxWait = null)
    {
        _db = db;
        _library = library;
        _log = log;
        _quiet = quiet ?? TimeSpan.FromSeconds(5);
        _maxWait = maxWait ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Picks up a game that was just installed or found, without waiting for the next periodic sync.</summary>
    public void RequestSync() => _syncRequested = true;

    /// <summary>What was seen for an installation, or <see cref="ObservedChanges.None"/>.</summary>
    public ObservedChanges Get(long installationId)
    {
        Watch? watch;
        lock (_gate) _watches.TryGetValue(installationId, out watch);
        if (watch is null) return ObservedChanges.None;
        lock (watch.Gate)
        {
            if (watch.Modified.Count == 0 && watch.Added.Count == 0) return ObservedChanges.None;
            var modified = watch.Modified.Order(StringComparer.Ordinal).ToList();
            var added = watch.Added.Order(StringComparer.Ordinal).ToList();
            return new ObservedChanges(modified, added, VolatileRules.Suggest(modified.Concat(added)).Take(MaxSuggestions).ToList());
        }
    }

    /// <summary>Follows the installed games until cancelled. <paramref name="syncInterval"/> is how often new and removed games are picked up.</summary>
    public async Task RunAsync(TimeSpan syncInterval, CancellationToken ct)
    {
        _library.InstallationChanged += OnInstallationChanged;
        _library.GameDiscovered += OnGameDiscovered;
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(_quiet.TotalMilliseconds / 4, 20, 1000));
        var nextSync = DateTime.MinValue;
        try
        {
            using var timer = new PeriodicTimer(tick);
            do
            {
                try
                {
                    if (_syncRequested || DateTime.UtcNow >= nextSync)
                    {
                        _syncRequested = false;
                        await SyncAsync(ct).ConfigureAwait(false);
                        nextSync = DateTime.UtcNow + syncInterval;
                    }
                    foreach (var watch in DueWatches()) await FlushAsync(watch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Watching game folders for changes failed, will try again"); }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            _library.InstallationChanged -= OnInstallationChanged;
            _library.GameDiscovered -= OnGameDiscovered;
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _watches.Values) w.Dispose();
            _watches.Clear();
        }
    }

    /// <summary>Starts watching games that appeared and stops watching games that are gone or moved.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _syncing.WaitAsync(ct).ConfigureAwait(false);
        try { await SyncCoreAsync(ct).ConfigureAwait(false); }
        finally { _syncing.Release(); }
    }

    private async Task SyncCoreAsync(CancellationToken ct)
    {
        var installs = (await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
            .Where(i => Directory.Exists(i.InstallPath))
            .ToDictionary(i => i.Id);

        List<Watch> gone;
        lock (_gate)
        {
            gone = _watches.Values.Where(w => !installs.TryGetValue(w.InstallationId, out var i) || !SamePath(i.InstallPath, w.Root)).ToList();
            foreach (var w in gone) _watches.Remove(w.InstallationId);
        }
        foreach (var w in gone) w.Dispose();

        foreach (var i in installs.Values)
        {
            lock (_gate) if (_watches.ContainsKey(i.Id)) continue;
            Watch watch;
            try { watch = new Watch(i.Id, Path.GetFullPath(i.InstallPath)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _log.LogWarning(ex, "Cannot watch {Path} for changes. A scan and a check still find them.", i.InstallPath);
                continue;
            }
            lock (_gate) _watches[i.Id] = watch;
        }
    }

    private void OnGameDiscovered(object? sender, LibraryGame game) => RequestSync();

    /// <summary>The game is intact again, or is a different version now. What was seen no longer applies.</summary>
    private void OnInstallationChanged(object? sender, InstallationChange change)
    {
        if (change.Kind == InstallationChangeKind.Replaced) { Forget(change.Previous?.Id); RequestSync(); }
        else if (change.Current.State == InstallationState.Installed) Forget(change.Current.Id);
    }

    private void Forget(long? installationId)
    {
        if (installationId is not { } id) return;
        Watch? watch;
        lock (_gate) _watches.TryGetValue(id, out watch);
        watch?.Reset();
    }

    private List<Watch> DueWatches()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
            return _watches.Values.Where(w => w.IsDue(now, _quiet, _maxWait)).ToList();
    }

    private async Task FlushAsync(Watch watch, CancellationToken ct)
    {
        var (paths, overflow) = watch.TakePending();
        if (paths.Count == 0 && !overflow) return;

        var inst = await _db.GetInstallationAsync(watch.InstallationId, ct).ConfigureAwait(false);
        if (inst is null) return;

        // A repair or an update writes into the folder itself. That is not the game changing.
        if ((await _db.ListDownloadsAsync(ct).ConfigureAwait(false)).Any(d => d.IsActive && d.InstallationId == inst.Id)) return;

        // Left over from a damage that was repaired since. It is not what this game did just now.
        if (inst.State == InstallationState.Installed && watch.ModifiedCount > 0) watch.Reset(keepPending: true);

        bool changed;
        if (overflow)
        {
            // Too many events to look at one by one, or the watcher lost some. A full check says the same thing, just slower.
            var c = await _library.CheckAsync(inst.ContentHash, ct).ConfigureAwait(false);
            changed = watch.Record(c.Modified.Concat(c.Missing), c.Added, []);
        }
        else
        {
            var stored = await _db.GetManifestAsync(inst.ContentHash, ct).ConfigureAwait(false);
            if (stored is null) return;
            var (modified, added, forgotten, retry) = await InspectAsync(watch, stored.Manifest, paths, ct).ConfigureAwait(false);
            changed = watch.Record(modified, added, forgotten);
            watch.Requeue(retry);
        }

        if (!changed) return;
        _log.LogInformation("The game at {Path} changed while it was in use: {Modified} game file(s), {Added} new file(s)",
            inst.InstallPath, watch.ModifiedCount, watch.AddedCount);

        // A changed content file means the files no longer match the recorded version.
        bool becameDamaged = false;
        if (watch.ModifiedCount > 0 && inst.State == InstallationState.Installed)
            becameDamaged = await _library.MarkDamagedAsync(inst.ContentHash, ct).ConfigureAwait(false);
        Changed?.Invoke(this, new TrackedChange(inst, becameDamaged));
    }

    private static async Task<(List<string> Modified, List<string> Added, List<string> Forgotten, List<string> Retry)> InspectAsync(
        Watch watch, GameManifest manifest, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var volatileFiles = VolatileMatcher.Create(manifest.VolatilePatterns);
        var files = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        List<string> modified = [], added = [], forgotten = [], retry = [];

        foreach (var rel in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(rel, "gameshare.json", StringComparison.OrdinalIgnoreCase) || volatileFiles.IsMatch(rel)) continue;
            if (watch.IsKnownModified(rel)) continue; // already known, hashing a big file again would tell nothing new

            var info = new FileInfo(Path.Combine(watch.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (files.TryGetValue(rel, out var expected))
            {
                if (!info.Exists) { modified.Add(rel); continue; }
                if (info.Length != expected.Size) { modified.Add(rel); continue; }
                try
                {
                    if (!string.Equals(await ManifestVerifier.HashFileAsync(info.FullName, ct).ConfigureAwait(false), expected.Hash, StringComparison.Ordinal))
                        modified.Add(rel);
                }
                catch (IOException) { retry.Add(rel); } // the game holds it exclusively right now, look again later
            }
            else if (info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) added.Add(rel);
            else forgotten.Add(rel); // a file the game created and removed again
        }
        return (modified, added, forgotten, retry);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The watcher and the bookkeeping for one game folder.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
        private bool _overflow;
        private DateTime _firstPending, _lastEvent;

        public long InstallationId { get; }
        public string Root { get; }
        public object Gate { get; } = new();
        public HashSet<string> Modified { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Added { get; } = new(StringComparer.Ordinal);

        public Watch(long installationId, string root)
        {
            InstallationId = installationId;
            Root = root;
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => Touch(e.FullPath);
            _watcher.Created += (_, e) => Touch(e.FullPath);
            _watcher.Deleted += (_, e) => Touch(e.FullPath);
            _watcher.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
            _watcher.Error += (_, _) => { lock (Gate) { _overflow = true; Stamp(); } };
            _watcher.EnableRaisingEvents = true;
        }

        public int ModifiedCount { get { lock (Gate) return Modified.Count; } }
        public int AddedCount { get { lock (Gate) return Added.Count; } }

        private void Touch(string fullPath)
        {
            var rel = Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
            if (rel.StartsWith("../", StringComparison.Ordinal) || rel == ".") return;
            lock (Gate)
            {
                if (_overflow) return;
                if (_pending.Count >= MaxPendingPaths) { _overflow = true; _pending.Clear(); }
                else _pending.Add(rel);
                Stamp();
            }
        }

        private void Stamp()
        {
            var now = DateTime.UtcNow;
            if (_firstPending == default) _firstPending = now;
            _lastEvent = now;
        }

        public bool IsDue(DateTime now, TimeSpan quiet, TimeSpan maxWait)
        {
            lock (Gate)
                return (_pending.Count > 0 || _overflow) && (now - _lastEvent >= quiet || now - _firstPending >= maxWait);
        }

        public (List<string> Paths, bool Overflow) TakePending()
        {
            lock (Gate)
            {
                var result = (_pending.ToList(), _overflow);
                _pending.Clear();
                _overflow = false;
                _firstPending = default;
                return result;
            }
        }

        public void Requeue(IEnumerable<string> paths)
        {
            lock (Gate)
            {
                foreach (var p in paths) _pending.Add(p);
                if (_pending.Count > 0) { _lastEvent = DateTime.UtcNow; if (_firstPending == default) _firstPending = _lastEvent; }
            }
        }

        public bool IsKnownModified(string rel) { lock (Gate) return Modified.Contains(rel); }

        /// <returns>True when what is known about the folder changed.</returns>
        public bool Record(IEnumerable<string> modified, IEnumerable<string> added, IEnumerable<string> forgotten)
        {
            lock (Gate)
            {
                bool changed = false;
                foreach (var p in modified) { changed |= Modified.Add(p); Added.Remove(p); }
                foreach (var p in added) if (!Modified.Contains(p)) changed |= Added.Add(p);
                foreach (var p in forgotten) changed |= Added.Remove(p);
                return changed;
            }
        }

        /// <param name="keepPending">The files that are waiting to be looked at stay, they are about the game as it is now.</param>
        public void Reset(bool keepPending = false)
        {
            lock (Gate)
            {
                Modified.Clear();
                Added.Clear();
                if (keepPending) return;
                _pending.Clear();
                _overflow = false;
                _firstPending = default;
            }
        }

        public void Dispose() => _watcher.Dispose();
    }
}
