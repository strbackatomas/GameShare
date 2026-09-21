using GameShare.Core;
using GameShare.Discovery;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>Turns the agent's internal state into the DTOs the GUI sees. The one place that knows how the pieces fit together.</summary>
public sealed class GameView
{
    private readonly GameLibrary _library;
    private readonly DownloadManager _downloads;
    private readonly PeerCatalog _catalog;
    private readonly DiscoveryService _discovery;
    private readonly GameChangeTracker _changes;
    private readonly TrustService _trust;

    public GameView(GameLibrary library, DownloadManager downloads, PeerCatalog catalog, DiscoveryService discovery, GameChangeTracker changes, TrustService trust)
    {
        _changes = changes;
        _trust = trust;
        _library = library;
        _downloads = downloads;
        _catalog = catalog;
        _discovery = discovery;
    }

    public async Task<IReadOnlyList<GameDto>> ListGamesAsync(CancellationToken ct = default)
    {
        var local = await _library.ListAsync(ct).ConfigureAwait(false);
        var downloads = await _downloads.ListAsync(ct).ConfigureAwait(false);
        var offers = _catalog.Offers.ToLookup(o => o.Game.ContentHash);

        var result = new Dictionary<string, GameDto>(StringComparer.Ordinal);

        // The version of each game that is installed here, so other versions can be offered as an update.
        var installedByGame = local
            .Where(g => g.Installation is { State: InstallationState.Installed })
            .GroupBy(g => g.Stored.Manifest.GameId)
            .ToDictionary(g => g.Key, g => g.First().Stored.Manifest.ContentHash);

        foreach (var g in local)
        {
            var m = g.Stored.Manifest;
            var download = downloads.FirstOrDefault(d => d.ContentHash == m.ContentHash && IsRunning(d.State));
            var peerNames = PeerNames(offers[m.ContentHash]);
            var state = g.Installation?.State switch
            {
                InstallationState.Installed => GameState.Installed,
                InstallationState.Invalid => GameState.Damaged,
                _ => download is not null ? GameState.Downloading
                    : peerNames.Count > 0 ? GameState.AvailableOnLan
                    : GameState.Unavailable,
            };

            var (fully, coverage) = _catalog.Availability(m.ContentHash);
            var seen = state == GameState.Damaged ? _changes.Get(g.Installation!.Id) : ObservedChanges.None;
            var (trust, trustNote) = _trust.Check(m.ContentHash);
            // The list vouches for the files of this version. Files that changed since are not those, so do not claim it. A revocation still stands.
            if (state == GameState.Damaged && trust == TrustVerdict.Verified) trust = TrustVerdict.NotChecked;
            result[m.ContentHash] = new GameDto(
                m.ContentHash, m.GameId, m.Name, m.Version, m.TotalSize, state,
                g.Installation?.InstallPath, peerNames, download?.Id, m.Definition)
            {
                ChangedFileCount = seen.Count,
                SuggestedPatterns = seen.SuggestedPatterns,
                Trust = trust,
                TrustNote = trustNote,
                UpdatesContentHash = g.Installation is null && installedByGame.TryGetValue(m.GameId, out var installed) ? installed : null,
                PartialPeerNames = PartialNames(offers[m.ContentHash]),
                FullyAvailable = fully,
                CoveragePercent = coverage,
            };
        }

        // Versions only other PCs have. Their manifest is fetched when the user installs.
        foreach (var group in offers.Where(g => !result.ContainsKey(g.Key)))
        {
            var o = group.First().Game;
            var (fully, coverage) = _catalog.Availability(o.ContentHash);
            var (trust, trustNote) = _trust.Check(o.ContentHash);
            result[o.ContentHash] = new GameDto(
                o.ContentHash, o.GameId, o.Name, o.Version, o.TotalSize, GameState.AvailableOnLan,
                null, PeerNames(group), null, null)
            {
                UpdatesContentHash = installedByGame.TryGetValue(o.GameId, out var installed) ? installed : null,
                Trust = trust,
                TrustNote = trustNote,
                PartialPeerNames = PartialNames(group),
                FullyAvailable = fully,
                CoveragePercent = coverage,
            };
        }

        return result.Values
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<GameDto?> GetGameAsync(string contentHash, CancellationToken ct = default) =>
        (await ListGamesAsync(ct).ConfigureAwait(false)).FirstOrDefault(g => g.ContentHash == contentHash);

    public PeerDto ToDto(PeerInfo p) =>
        new(p.MachineId, p.MachineName, p.Address.ToString(), p.AgentPort, _catalog.OfferCount(p.MachineId), p.LastSeen);

    public DownloadDto ToDto(DownloadStatus d)
    {
        // Show machine names instead of raw addresses where discovery knows them.
        var names = _discovery.Peers.ToLookup(p => p.Address.ToString(), p => p.MachineName);
        var peers = d.PeerDetails
            .Select(p =>
            {
                var host = StripPort(p.Address);
                return new DownloadPeerDto(names[host].FirstOrDefault() ?? host, host, p.DownloadRate, p.UploadRate);
            })
            .ToList();

        return new DownloadDto(
            d.Id, d.ContentHash, d.GameName, d.State.ToString(), d.BytesDone, d.BytesTotal, d.Percent,
            d.DownloadRate, d.Peers, d.Eta?.TotalSeconds, d.Error, peers) { Kind = d.Kind.ToString() };
    }

    public static SeedDto ToDto(SeedEvent e, string contentHash) => new(contentHash, e.GameName, e.Installation.InstallPath);

    private static IReadOnlyList<string> PartialNames(IEnumerable<RemoteOffer> offers) =>
        offers.Where(o => !o.Game.IsComplete).Select(o => o.Peer.MachineName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    private IReadOnlyList<string> PeerNames(IEnumerable<RemoteOffer> offers) =>
        offers.Select(o => o.Peer.MachineName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsRunning(DownloadState state) => state is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying;

    /// <summary>The engine reports "192.168.1.5:6881". Only the host part identifies the machine.</summary>
    private static string StripPort(string endpoint) =>
        System.Net.IPEndPoint.TryParse(endpoint, out var ep) ? ep.Address.ToString() : endpoint;
}
