using System.Text.RegularExpressions;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Discovery;
using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// What other PCs may ask this one. Read only. It offers games that are installed and games that were changed by playing them,
/// the latter only with the pieces that still match. It never reveals install paths or anything about downloads in progress.
/// </summary>
public static partial class PeerApi
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    private sealed record Offer(StoredManifest Stored, bool IsComplete, double PercentIntact);

    public static void Map(WebApplication app)
    {
        var peer = app.MapGroup("/peer");

        peer.MapGet("/hello", (AgentIdentity me) => new PeerHelloDto(me.MachineId, me.MachineName, DiscoveryMessage.CurrentVersion, AppVersion.Current));

        peer.MapGet("/games", async (GameShareDb db, SettingsService settings, SeedManager seeds, CancellationToken ct) =>
            (await OfferedAsync(db, settings, seeds, ct)).Select(o => new OfferedGameDto(
                o.Stored.Manifest.ContentHash, o.Stored.Manifest.GameId, o.Stored.Manifest.Name, o.Stored.Manifest.Version, o.Stored.Manifest.TotalSize)
            {
                IsComplete = o.IsComplete,
                PercentIntact = o.PercentIntact,
            }).ToList());

        peer.MapGet("/games/{contentHash}/manifest", async (string contentHash, GameShareDb db, SettingsService settings, SeedManager seeds, CancellationToken ct) =>
        {
            var offer = await FindAsync(contentHash, db, settings, seeds, ct);
            return Results.Text(GameShareJson.Serialize(offer.Stored.Manifest), "application/json");
        });

        peer.MapGet("/games/{contentHash}/torrent", async (string contentHash, GameShareDb db, SettingsService settings, SeedManager seeds, CancellationToken ct) =>
        {
            var offer = await FindAsync(contentHash, db, settings, seeds, ct);
            return Results.File(offer.Stored.TorrentBytes!, "application/x-bittorrent");
        });

        // Which pieces this PC can prove. Lets a PC that wants the game see whether the PCs online have all of it between them.
        peer.MapGet("/games/{contentHash}/pieces", async (string contentHash, GameShareDb db, SettingsService settings, SeedManager seeds, CancellationToken ct) =>
        {
            var offer = await FindAsync(contentHash, db, settings, seeds, ct);
            var have = seeds.GetPiecesHave(offer.Stored);
            if (offer.IsComplete || have is null)
            {
                int count = (int)((offer.Stored.Manifest.TotalSize + offer.Stored.Manifest.PieceLength - 1) / offer.Stored.Manifest.PieceLength);
                return PieceMapCodec.Full(count);
            }
            return PieceMapCodec.Pack(have);
        });
    }

    private static async Task<Offer> FindAsync(string contentHash, GameShareDb db, SettingsService settings, SeedManager seeds, CancellationToken ct)
    {
        if (!Sha256Hex().IsMatch(contentHash)) throw new ArgumentException("The game id must be a 64 character hexadecimal content hash.");
        return (await OfferedAsync(db, settings, seeds, ct)).FirstOrDefault(o => o.Stored.Manifest.ContentHash == contentHash)
            ?? throw new KeyNotFoundException("This PC does not offer that game.");
    }

    /// <summary>
    /// One entry per game version. A verified installation is offered whole. If every copy on this PC is damaged, the game is offered
    /// only when the transfer actually holds some verified pieces, and it says how much.
    /// </summary>
    private static async Task<List<Offer>> OfferedAsync(GameShareDb db, SettingsService settings, SeedManager seeds, CancellationToken ct)
    {
        var offers = new List<Offer>();
        if (!settings.Current.SeedingEnabled) return offers;

        foreach (var group in (await db.ListInstallationsAsync(ct)).Where(i => i.Seeding).GroupBy(i => i.ContentHash))
        {
            var stored = await db.GetManifestAsync(group.Key, ct);
            if (stored?.TorrentBytes is null) continue;

            if (group.Any(i => i.State == InstallationState.Installed))
            {
                offers.Add(new Offer(stored, IsComplete: true, PercentIntact: 100));
                continue;
            }

            var seed = seeds.GetOffer(stored);
            if (seed is { PercentIntact: > 0 }) offers.Add(new Offer(stored, seed.IsComplete, seed.PercentIntact));
        }
        return offers;
    }
}
