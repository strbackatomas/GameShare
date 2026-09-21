using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TorrentSharp.Wrap;
using TorrentSharp.Wrap.Configurations;
using TorrentSharp.Wrap.Configurations.Settings;
using TorrentSharp.Wrap.Enums;
using TorrentSharp.Wrap.Notifications;

namespace GameShare.Torrent;

public sealed record TorrentEngineOptions
{
    /// <summary>TCP/uTP port. 0 lets the OS pick, which the tests use so several engines can share a machine.</summary>
    public int ListenPort { get; init; } = 6881;

    /// <summary>
    /// Only talk to private, loopback and link-local IPv4 addresses. On by default: game data must never leave the LAN.
    /// Also disables DHT, UPnP and NAT-PMP, so nothing is announced to the internet and no port is forwarded.
    /// </summary>
    public bool LanOnly { get; init; } = true;

    /// <summary>Bytes per second. Null means unlimited.</summary>
    public int? MaxUploadBytesPerSecond { get; init; }
    public int? MaxDownloadBytesPerSecond { get; init; }

    /// <summary>Normally off. Needed only when several peers share one IP address, as in single-machine tests.</summary>
    public bool AllowMultipleConnectionsPerIp { get; init; }

    /// <summary>
    /// How many files the library keeps open at once. On Windows an open file is memory-mapped, and a program that replaces such a file
    /// by truncating it, which is how most games save their settings, is refused. A game must never notice that its files are shared,
    /// so only a few files are held open, and see <see cref="IdleReleaseAfter"/> for the rest.
    /// Measured with 3000 small files: the transfer was not slower with 2, 4 or 16 than with the default.
    /// </summary>
    public int OpenFileLimit { get; init; } = 8;

    /// <summary>
    /// A seed that has not uploaded anything for this long lets go of its files, so a game can rewrite them.
    /// Null turns it off. Pausing and resuming a seed does not re-check it and it announces itself again straight away.
    /// </summary>
    public TimeSpan? IdleReleaseAfter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Address ranges allowed when <see cref="LanOnly"/> is on. Null means the private ranges. Mainly for tests.</summary>
    public IReadOnlyList<IpRange>? AllowedRanges { get; init; }
}

/// <summary>Inclusive IPv4 range.</summary>
public sealed record IpRange(string First, string Last);

/// <summary>
/// Owns one libtorrent session. Peers are found through Local Service Discovery on the LAN,
/// so no tracker and no DHT are needed.
/// </summary>
public sealed class TorrentEngine : IDisposable
{
    /// <summary>Private, link-local and loopback IPv4 ranges. A LAN-only engine talks to nothing else.</summary>
    public static readonly IReadOnlyList<IpRange> PrivateRanges =
    [
        new("10.0.0.0", "10.255.255.255"),
        new("172.16.0.0", "172.31.255.255"),
        new("192.168.0.0", "192.168.255.255"),
        new("169.254.0.0", "169.254.255.255"),
        new("127.0.0.0", "127.255.255.255"),
    ];

    private readonly TorrentClient _client;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, TorrentTransfer> _transfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _removals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyRemoved = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopBalancer = new();
    private readonly TimeSpan? _idleReleaseAfter;
    private volatile int _maxUpload;    // bytes per second, 0 means unlimited
    private volatile int _maxDownload;

    public TorrentEngine(TorrentEngineOptions? options = null, ILogger<TorrentEngine>? logger = null)
    {
        options ??= new TorrentEngineOptions();
        _log = logger ?? NullLogger<TorrentEngine>.Instance;

        _client = new TorrentClient(new TorrentClientConfig
        {
            NotificationCategories = NotificationCategories.Status | NotificationCategories.Peer | NotificationCategories.Error,
        });
        _client.NotificationRaised += OnNotification;

        var pack = new SettingsPack()
            .Set(new ListenInterfaces($"0.0.0.0:{options.ListenPort}")) // IPv4 only, keeps the LAN filter simple
            .Set(new FilePoolSize(Math.Max(1, options.OpenFileLimit)))
            .Set(new EnableLsd(true))
            // Each torrent announces itself on the LAN once per interval, five minutes by default. A single lost multicast
            // datagram, easy on Wi-Fi, would leave a new download without peers for minutes. Announcements are tiny.
            .Set(new LocalServiceAnnounceInterval(30))
            .Set(new EnableDht(false))
            .Set(new EnableUpnp(false))
            .Set(new EnableNatpmp(false))
            .Set(new AllowMultipleConnectionsPerIp(options.AllowMultipleConnectionsPerIp))
            .Set(new UploadRateLimit(options.MaxUploadBytesPerSecond ?? 0))
            .Set(new DownloadRateLimit(options.MaxDownloadBytesPerSecond ?? 0));
        _client.UpdateSettings(pack);

        if (options.LanOnly)
        {
            _client.AddIpFilterRule("0.0.0.0", "255.255.255.255", blocked: true);
            // Later rules override earlier ones, so "block everything" first and then punch holes.
            foreach (var range in options.AllowedRanges ?? PrivateRanges)
                _client.AddIpFilterRule(range.First, range.Last, blocked: false);
        }

        _idleReleaseAfter = options.IdleReleaseAfter;
        _maxUpload = options.MaxUploadBytesPerSecond ?? 0;
        _maxDownload = options.MaxDownloadBytesPerSecond ?? 0;
        _ = Task.Run(() => BalanceLoopAsync(_stopBalancer.Token));

        _log.LogInformation("Torrent engine started. Port={Port} LanOnly={LanOnly}", options.ListenPort, options.LanOnly);
    }

    public IReadOnlyCollection<TorrentTransfer> Transfers => _transfers.Values.ToList();

    /// <summary>
    /// Registers a torrent. Data is written directly below <paramref name="savePath"/>, into a folder named after the torrent.
    /// Nothing happens on the network until <see cref="TorrentTransfer.Start"/>.
    /// </summary>
    /// <param name="resumeData">Value previously returned by <see cref="TorrentTransfer.SaveResumeDataAsync"/>.</param>
    /// <param name="uploadOnly">Seed without ever writing to the files. See <see cref="TorrentTransfer.UploadOnly"/>.</param>
    public TorrentTransfer Add(byte[] torrentBytes, string savePath, byte[]? resumeData = null, bool uploadOnly = false)
    {
        Directory.CreateDirectory(savePath);
        var info = new TorrentInfo(torrentBytes);
        var meta = info.Metadata ?? throw new InvalidDataException("Torrent metadata could not be read.");
        var infoHash = meta.InfoHash!.ToLowerInvariant();

        if (_transfers.ContainsKey(infoHash))
            throw new InvalidOperationException($"Torrent {meta.Name} ({infoHash}) is already added to this engine.");

        var manager = _client.AttachTorrent(info, savePath, resumeData);
        var transfer = new TorrentTransfer(manager, infoHash, meta.Name!, meta.TotalSize) { UploadOnly = uploadOnly };
        _transfers[infoHash] = transfer;

        _log.LogInformation("Torrent added: {Name} {InfoHash} into {SavePath} (resume data: {HasResume})",
            meta.Name, infoHash, savePath, resumeData is { Length: > 0 });
        Rebalance();
        return transfer;
    }

    /// <summary>
    /// Like <see cref="Add"/>, but tolerates the library still finishing the removal of the same torrent, which happens
    /// when a download is paused and resumed, or cancelled and started again, within moments. Any other failure is immediate.
    /// </summary>
    public async Task<TorrentTransfer> AddAsync(
        byte[] torrentBytes, string savePath, byte[]? resumeData = null, bool uploadOnly = false, CancellationToken cancellationToken = default)
    {
        var infoHash = new TorrentInfo(torrentBytes).Metadata?.InfoHash?.ToLowerInvariant();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try { return Add(torrentBytes, savePath, resumeData, uploadOnly); }
            catch (InvalidOperationException ex) when (
                infoHash is not null
                && ex.Message.Contains("already attached", StringComparison.OrdinalIgnoreCase)
                && _recentlyRemoved.TryGetValue(infoHash, out var removedAt)
                && DateTimeOffset.UtcNow - removedAt < TimeSpan.FromSeconds(30)
                && DateTimeOffset.UtcNow < deadline)
            {
                _log.LogDebug("Torrent {InfoHash} is still being removed, retrying to add it", infoHash);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Stops and forgets the torrent, and returns only once the library has really let go of it.
    /// The library removes torrents asynchronously, so adding the same torrent again straight after a plain
    /// detach fails with "already attached". Files on disk are never deleted.
    /// </summary>
    public async Task RemoveAsync(TorrentTransfer transfer, CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryRemove(transfer.InfoHash, out _)) return;

        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _removals[transfer.InfoHash] = removed;
        _client.DetachTorrent(GetManagerFor(transfer));

        _recentlyRemoved[transfer.InfoHash] = DateTimeOffset.UtcNow;

        // The notification is the fast path. If it is late, AddAsync retries, so this wait only smooths the common case.
        try { await removed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _log.LogDebug("No removal notification for {Name} {InfoHash} within 2 seconds", transfer.Name, transfer.InfoHash);
        }
        finally { _removals.TryRemove(transfer.InfoHash, out _); }

        _log.LogInformation("Torrent removed: {Name} {InfoHash}", transfer.Name, transfer.InfoHash);
        Rebalance();
    }

    /// <summary>
    /// Sets the total speed limits in bytes per second, null for unlimited. Takes effect at once and on running transfers.
    /// </summary>
    /// <remarks>
    /// The library exempts peers on the local network from its global limits, and every peer here is on the local network.
    /// So the total is enforced per torrent instead: it is divided between the transfers that are actually moving data
    /// and re-divided every second, so one busy game gets the whole allowance while the idle ones keep nothing back.
    /// </remarks>
    public void SetLimits(int? maxUploadBytesPerSecond, int? maxDownloadBytesPerSecond)
    {
        _maxUpload = maxUploadBytesPerSecond ?? 0;
        _maxDownload = maxDownloadBytesPerSecond ?? 0;
        _client.UpdateSettings(new SettingsPack()
            .Set(new UploadRateLimit(_maxUpload))
            .Set(new DownloadRateLimit(_maxDownload)));
        Rebalance();
    }

    /// <summary>An even share of <paramref name="total"/> (null or 0 = unlimited) for one of <paramref name="activeTransfers"/>.</summary>
    public static int? EvenShare(int? total, int activeTransfers)
    {
        if (total is null or <= 0) return null;
        return Math.Max(16 * 1024, total.Value / Math.Max(1, activeTransfers));
    }

    private async Task BalanceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Rebalance();
        }
        catch (OperationCanceledException) { /* engine disposed */ }
    }

    private void Rebalance()
    {
        int up = _maxUpload, down = _maxDownload;
        var transfers = Transfers;
        var states = new List<(TorrentTransfer Transfer, TransferStatus? Status)>(transfers.Count);
        foreach (var t in transfers)
        {
            try { states.Add((t, t.GetStatus())); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { states.Add((t, null)); } // being removed
        }

        // Upload is shared by whoever is sending, download by whatever is not finished yet.
        int uploading = states.Count(x => x.Status is { UploadRate: > 0 });
        int downloading = states.Count(x => x.Status is { IsComplete: false });
        int? upShare = EvenShare(up, uploading);
        int? downShare = EvenShare(down, downloading);

        var now = DateTime.UtcNow;
        foreach (var (t, status) in states)
        {
            try
            {
                t.SetRateLimits(upShare, downShare);
                if (status is not null) ReleaseIdleFiles(t, status, now);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                _log.LogDebug("Could not set limits on {Name}, it is being removed: {Reason}", t.Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// A seed that stopped uploading keeps a few files open, and an open file cannot be replaced by a game that saves by truncating it.
    /// Once it has been idle for a while it is paused and, a tick later, resumed, which closes them. Once per idle period, not over and over.
    /// The pause takes a moment to close the files: resuming at once cancels it.
    /// </summary>
    private void ReleaseIdleFiles(TorrentTransfer t, TransferStatus status, DateTime now)
    {
        if (t.Releasing)
        {
            t.Releasing = false;
            t.Start();
            return;
        }
        if (_idleReleaseAfter is not { } idleAfter || !t.UploadOnly || t.IsStopped) return;
        if (status.State is TransferState.Checking or TransferState.Error) return;

        // The upload rate is a moving average that stays above zero for a long time after the last byte, so count the bytes.
        if (status.SessionUploaded != t.LastUploaded)
        {
            t.LastUploaded = status.SessionUploaded;
            t.LastActivity = now;
            t.FilesReleased = false;
            return;
        }
        if (t.FilesReleased || now - t.LastActivity < idleAfter) return;

        t.FilesReleased = true;
        t.Releasing = true;
        t.Stop(); // resumed on the next tick
        _log.LogDebug("Seed {Name} was idle, its files are being released", t.Name);
    }

    private TorrentManager GetManagerFor(TorrentTransfer transfer) =>
        _client.ActiveTorrents.First(m => string.Equals(m.InfoHash, transfer.InfoHash, StringComparison.OrdinalIgnoreCase));

    private void OnNotification(object? sender, SessionNotification n)
    {
        switch (n)
        {
            case TorrentRemovedNotification r:
                if (_removals.TryGetValue(r.TorrentManager.InfoHash, out var pending)) pending.TrySetResult();
                break;
            case TorrentStatusNotification s:
                _log.LogInformation("Torrent {InfoHash}: {Old} -> {New}", s.TorrentManager.InfoHash, LibtorrentStateName(s.OldState), LibtorrentStateName(s.NewState));
                break;
            case PeerNotification p:
                _log.LogInformation("Peer {Event}: {Address} on torrent {InfoHash}", p.NotificationType, p.Address, p.TorrentManager.InfoHash);
                break;
            default:
                _log.LogDebug("libtorrent: {Message}", n.Message);
                break;
        }
    }

    /// <summary>
    /// The binding puts libtorrent's state numbers into its own enum in a different order, so its names are wrong for state change
    /// notifications, for example checking_resume_data shows up as "Errored". The numbers are libtorrent's, so name them from those.
    /// </summary>
    internal static string LibtorrentStateName(TorrentState state) => (int)state switch
    {
        0 => "unknown",
        1 => "checking_files",
        2 => "downloading_metadata",
        3 => "downloading",
        4 => "finished",
        5 => "seeding",
        6 => "allocating",
        7 => "checking_resume_data",
        var n => $"state {n}",
    };

    public void Dispose()
    {
        _stopBalancer.Cancel();
        _client.NotificationRaised -= OnNotification;
        _client.Dispose();
    }
}
