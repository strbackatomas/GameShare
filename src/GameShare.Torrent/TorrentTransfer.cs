using TorrentSharp.Wrap;
using TorrentSharp.Wrap.Enums;

namespace GameShare.Torrent;

public enum TransferState
{
    /// <summary>Hashing existing files or validating resume data.</summary>
    Checking,
    Downloading,
    /// <summary>All data present and verified. The transfer is (or may be) uploading to others.</summary>
    Complete,
    Error,
}

/// <param name="Progress">0..1 of the wanted data that is present and hash-verified.</param>
/// <param name="DownloadRate">Bytes per second.</param>
/// <param name="SessionDownloaded">Payload bytes received since this engine started. Excludes data restored from resume state.</param>
public sealed record TransferStatus(
    TransferState State,
    double Progress,
    long BytesDone,
    long BytesTotal,
    long DownloadRate,
    long UploadRate,
    int PeerCount,
    int SeedCount,
    long SessionDownloaded,
    long SessionUploaded)
{
    public bool IsComplete => State == TransferState.Complete;

    public TimeSpan? Eta =>
        State == TransferState.Downloading && DownloadRate > 0
            ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / (double)DownloadRate)
            : null;

    public override string ToString() =>
        $"{State} {Progress:P1} peers={PeerCount} down={DownloadRate / 1_000_000.0:F1}MB/s up={UploadRate / 1_000_000.0:F1}MB/s";
}

public sealed record PeerSnapshot(string Address, long DownloadRate, long UploadRate, bool IsSeed);

/// <summary>One torrent inside a <see cref="TorrentEngine"/>. Hides the binding's types from the rest of the app.</summary>
public sealed class TorrentTransfer
{
    private readonly TorrentManager _manager;

    internal TorrentTransfer(TorrentManager manager, string infoHash, string name, long totalSize)
    {
        _manager = manager;
        InfoHash = infoHash;
        Name = name;
        TotalSize = totalSize;
    }

    public string InfoHash { get; }
    public string Name { get; }
    public long TotalSize { get; }

    /// <summary>
    /// When true the transfer only uploads. It never requests data and never writes to the files, so a file the user or
    /// a running game has changed is left alone instead of being "repaired" from other PCs. Pieces that no longer match
    /// are simply not offered. Set it for every seed, leave it off while installing.
    /// </summary>
    public bool UploadOnly
    {
        get => _manager.UploadMode;
        set => _manager.UploadMode = value;
    }

    /// <summary>
    /// The library's global speed limits skip peers on the local network, which is all of our traffic, so limits are set per torrent.
    /// Null means unlimited. The engine keeps these up to date, see <see cref="TorrentEngine.SetLimits"/>.
    /// </summary>
    internal void SetRateLimits(int? uploadBytesPerSecond, int? downloadBytesPerSecond)
    {
        _manager.UploadLimit = uploadBytesPerSecond ?? -1;
        _manager.DownloadLimit = downloadBytesPerSecond ?? -1;
    }

    /// <summary>True after <see cref="Stop"/> until <see cref="Start"/> is called again.</summary>
    public bool IsStopped { get; private set; } = true;

    public void Start() { _manager.Start(); IsStopped = false; }
    public void Stop() { _manager.Stop(); IsStopped = true; }

    /// <summary>Re-hashes every file on disk and re-downloads pieces that no longer match. Used for repair.</summary>
    public void ForceRecheck() => _manager.ForceRecheck();

    public TransferStatus GetStatus()
    {
        var s = _manager.GetCurrentStatus();
        var state = s.State switch
        {
            TorrentState.Seeding or TorrentState.Finished => TransferState.Complete,
            TorrentState.Errored => TransferState.Error,
            TorrentState.Downloading or TorrentState.DownloadingMetadata => TransferState.Downloading,
            _ => TransferState.Checking,
        };
        return new TransferStatus(
            state, s.Progress, s.TotalWantedDone, s.TotalWanted, s.DownloadRate, s.UploadRate,
            s.PeerCount, s.SeedCount, s.BytesDownloaded, s.BytesUploaded);
    }

    public IReadOnlyList<PeerSnapshot> GetPeers() =>
        _manager.GetPeers().Select(p => new PeerSnapshot(p.Address, p.DownloadRate, p.UploadRate, p.IsSeed)).ToList();

    /// <summary>
    /// Opaque state that lets a later run continue without re-downloading verified data.
    /// Persist it whenever it is produced, and pass it back to <see cref="TorrentEngine.Add"/>.
    /// The library only answers when the torrent is in a state that can be saved, so this never waits longer than
    /// <paramref name="timeout"/> (5 seconds by default) and then throws <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<byte[]> SaveResumeDataAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        try
        {
            return await _manager.SaveResumeDataAsync(cancellationToken, limit).WaitAsync(limit + TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Resume data for '{Name}' was not available within {limit.TotalSeconds:F0} seconds. The torrent is probably still checking its files.", ex);
        }
    }

    public Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(s => s.IsComplete, "complete", timeout, cancellationToken);

    /// <summary>Polls until <paramref name="predicate"/> holds. Throws with the last status if it does not in time.</summary>
    public async Task WaitUntilAsync(
        Func<TransferStatus, bool> predicate, string description, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var last = GetStatus();
        try
        {
            while (true)
            {
                last = GetStatus();
                if (predicate(last)) return;
                if (last.State == TransferState.Error)
                    throw new InvalidOperationException($"Torrent '{Name}' entered an error state: {last}");
                await Task.Delay(100, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Torrent '{Name}' was not {description} after {timeout.TotalSeconds:F0}s. Last status: {last}");
        }
    }
}
