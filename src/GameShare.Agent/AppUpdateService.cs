using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent;

/// <summary>
/// Finds newer versions of GameShare itself, downloads them and checks them, so that the user only has to say "update".
///
/// A release is described by <c>update-&lt;build&gt;.json</c>, signed with the release key built into the program (<see cref="ReleaseKey"/>),
/// and only one for this build, newer than what runs, is taken. Its package is downloaded as the zip next to the description, checked
/// against the SHA-256 in the description before it is unpacked, unpacked only into the files the description lists, and every file is
/// checked again. What passes lies in <c>updates\&lt;version&gt;\</c> in the data folder until it is applied.
/// Nothing is ever put in place from here; see AppUpdateFiles for that.
/// </summary>
public sealed class AppUpdateService
{
    public const string FolderName = "updates";

    /// <summary>The signed description, kept next to the package it describes.</summary>
    public const string ReleaseFileName = "release.json";

    /// <summary>A stalled download is given up after this long without a byte, and tried again at the next check.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    private readonly AgentOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<AppUpdateService> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly object _lock = new();

    // All guarded by _lock.
    private AppUpdateState _state;
    private Candidate? _known;
    private StagedRelease? _ready;
    private long _done, _total;
    private string? _error;
    private DateTimeOffset? _lastChecked;
    private Task? _download;
    private CancellationToken _stopping;

    private sealed record Candidate(AppRelease Release, byte[] Document, string Source);

    /// <summary>A newer version that is downloaded and checked. <see cref="PackageDir"/> holds exactly the files of its manifest.</summary>
    public sealed record StagedRelease(AppRelease Release, byte[] Document, string PackageDir);

    public AppUpdateService(AgentOptions options, HttpClient http, ILogger<AppUpdateService> log, TimeProvider? time = null)
    {
        _options = options;
        _http = http;
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
            return new AppUpdateStatusDto(
                _options.RunningVersion, _options.UpdateFlavor, _state, release?.Version, release?.Notes, release?.ReleasedAt,
                _done, _total, _ready is null ? _known?.Source : null, _error, _lastChecked,
                CanApply: false, CannotApplyReason: null, DisabledReason);
        }
    }

    /// <summary>Picks up a version downloaded before a restart, then looks for newer ones now and on every interval, until cancelled.</summary>
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
        await LoadStagedAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.UpdateCheckInterval);
        try
        {
            do
            {
                try { await CheckAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Looking for a newer GameShare failed"); }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            Task? download;
            lock (_lock) download = _download;
            if (download is not null) await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
        }
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
            try
            {
                ReleaseSigning.CheckApplies(release, _options.UpdateFlavor!, _options.RunningVersion, heldVersion: _ready?.Release.Version);
            }
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

    private async Task DownloadAsync(Candidate candidate, CancellationToken ct)
    {
        var release = candidate.Release;
        var versionDir = Path.Combine(UpdatesDir, release.Version);
        var partial = versionDir + ".partial";
        var zip = partial + ".zip";
        try
        {
            if (release.ZipName is null) throw new InvalidDataException($"Version {release.Version} has no zip to download.");
            DeleteQuietly(partial);
            Directory.CreateDirectory(partial);

            await FetchZipAsync(SourceFetch.Combine(candidate.Source, release.ZipName), release, zip, ct).ConfigureAwait(false);
            var packageDir = Path.Combine(partial, release.Manifest.FolderName);
            Unpack(zip, release.Manifest, packageDir);
            File.Delete(zip);

            var check = await ManifestVerifier.VerifyAsync(release.Manifest, packageDir, VerifyMode.Full, cancellationToken: ct).ConfigureAwait(false);
            if (!check.IsValid) throw new InvalidDataException($"The downloaded files of version {release.Version} are not what was signed: {check}.");
            await File.WriteAllBytesAsync(Path.Combine(partial, ReleaseFileName), candidate.Document, ct).ConfigureAwait(false);

            DeleteQuietly(versionDir);
            Directory.Move(partial, versionDir);
            var staged = new StagedRelease(release, candidate.Document, Path.Combine(versionDir, release.Manifest.FolderName));
            lock (_lock)
            {
                _ready = staged;
                _state = AppUpdateState.Ready;
                _done = _total;
                _error = null;
                _download = null;
            }
            RemoveOtherVersions(release.Version);
            _log.LogInformation("GameShare {Version} is downloaded and checked, it is applied when the user asks", release.Version);
        }
        catch (Exception ex)
        {
            DeleteQuietly(partial);
            TryDelete(zip);
            lock (_lock)
            {
                _download = null;
                _state = _ready is null ? AppUpdateState.Available : AppUpdateState.Ready;
                _error = ct.IsCancellationRequested ? null : $"Stažení verze {release.Version} se nepovedlo: {(ex is TaskCanceledException ? "zdroj přestal odpovídat." : ex.Message)}";
            }
            if (!ct.IsCancellationRequested) _log.LogWarning("Downloading GameShare {Version} failed, it is tried again at the next check: {Reason}", release.Version, ex.Message);
        }
        RaiseChanged();
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
                    stall.CancelAfter(StallTimeout);
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
            throw new TimeoutException($"{location} sent nothing for {StallTimeout.TotalMinutes:N0} minutes.");
        }
        finally { response?.Dispose(); }
    }

    /// <summary>
    /// Unpacks only the files the signed manifest lists, each with the size it lists. Any other entry, a path outside the folder among
    /// them, fails the whole package instead of being skipped: a zip that does not match its description is not used at all.
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

            // Safe: the path is one of the manifest's, and those were checked to stay inside the folder when the release was opened.
            var target = Path.Combine(targetDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        if (seen.Count != expected.Count)
            throw new InvalidDataException($"The zip lacks {expected.Count - seen.Count} file(s) of the signed package.");
    }

    /// <summary>A version downloaded before a restart is still offered, after a quick look that its files are all there.</summary>
    private async Task LoadStagedAsync(CancellationToken ct)
    {
        foreach (var leftover in Directory.EnumerateDirectories(UpdatesDir, "*.partial")) DeleteQuietly(leftover);
        foreach (var leftover in Directory.EnumerateFiles(UpdatesDir, "*.partial.zip")) TryDelete(leftover);

        StagedRelease? best = null;
        foreach (var dir in Directory.EnumerateDirectories(UpdatesDir))
        {
            try
            {
                var document = await File.ReadAllBytesAsync(Path.Combine(dir, ReleaseFileName), ct).ConfigureAwait(false);
                var release = ReleaseSigning.Open(document, _options.UpdatePublicKey!);
                ReleaseSigning.CheckApplies(release, _options.UpdateFlavor!, _options.RunningVersion);
                var packageDir = Path.Combine(dir, release.Manifest.FolderName);
                var check = await ManifestVerifier.VerifyAsync(release.Manifest, packageDir, VerifyMode.Quick, cancellationToken: ct).ConfigureAwait(false);
                if (!check.IsValid) throw new InvalidDataException(check.ToString());
                if (best is null || SemVer.IsNewer(release.Version, best.Release.Version)) best = new StagedRelease(release, document, packageDir);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Applied already, for another build, or damaged: of no use, and a newer one is downloaded again if there is one.
                _log.LogInformation("Removing {Dir}, it holds no usable update: {Reason}", dir, ex.Message);
                DeleteQuietly(dir);
            }
        }

        if (best is null) return;
        lock (_lock)
        {
            _ready = best;
            _state = AppUpdateState.Ready;
            _done = _total = best.Release.Manifest.TotalSize;
        }
        RemoveOtherVersions(best.Release.Version);
        _log.LogInformation("GameShare {Version} was downloaded earlier and is ready to be applied", best.Release.Version);
        RaiseChanged();
    }

    private void RemoveOtherVersions(string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(UpdatesDir))
            if (!string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase) && !dir.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
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
