using System.Text.RegularExpressions;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Discovery;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// What other PCs may ask this one. Read only, and only about games that are installed, verified and being offered.
/// Never reveals install paths or anything about downloads in progress.
/// </summary>
public static partial class PeerApi
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    public static void Map(WebApplication app)
    {
        var peer = app.MapGroup("/peer");

        peer.MapGet("/hello", (AgentIdentity me) => new PeerHelloDto(me.MachineId, me.MachineName, DiscoveryMessage.CurrentVersion));

        peer.MapGet("/games", async (GameShareDb db, SettingsService settings, CancellationToken ct) =>
            (await OfferedAsync(db, settings, ct)).Select(o => new OfferedGameDto(
                o.Manifest.ContentHash, o.Manifest.GameId, o.Manifest.Name, o.Manifest.Version, o.Manifest.TotalSize)).ToList());

        peer.MapGet("/games/{contentHash}/manifest", async (string contentHash, GameShareDb db, SettingsService settings, CancellationToken ct) =>
        {
            var offer = await FindAsync(contentHash, db, settings, ct);
            return Results.Text(GameShareJson.Serialize(offer.Manifest), "application/json");
        });

        peer.MapGet("/games/{contentHash}/torrent", async (string contentHash, GameShareDb db, SettingsService settings, CancellationToken ct) =>
        {
            var offer = await FindAsync(contentHash, db, settings, ct);
            return Results.File(offer.TorrentBytes!, "application/x-bittorrent");
        });
    }

    private static async Task<StoredManifest> FindAsync(string contentHash, GameShareDb db, SettingsService settings, CancellationToken ct)
    {
        if (!Sha256Hex().IsMatch(contentHash)) throw new ArgumentException("The game id must be a 64 character hexadecimal content hash.");
        return (await OfferedAsync(db, settings, ct)).FirstOrDefault(o => o.Manifest.ContentHash == contentHash)
            ?? throw new KeyNotFoundException("This PC does not offer that game.");
    }

    /// <summary>Verified installations with seeding on, one entry per game version even if it is installed twice.</summary>
    private static async Task<List<StoredManifest>> OfferedAsync(GameShareDb db, SettingsService settings, CancellationToken ct)
    {
        var offered = new List<StoredManifest>();
        if (!settings.Current.SeedingEnabled) return offered;

        var hashes = (await db.ListInstallationsAsync(ct))
            .Where(i => i.State == InstallationState.Installed && i.Seeding)
            .Select(i => i.ContentHash).Distinct();
        foreach (var hash in hashes)
        {
            var stored = await db.GetManifestAsync(hash, ct);
            if (stored?.TorrentBytes is not null) offered.Add(stored);
        }
        return offered;
    }
}
