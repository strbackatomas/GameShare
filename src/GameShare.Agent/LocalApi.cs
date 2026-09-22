using System.Text.RegularExpressions;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Discovery;
using GameShare.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace GameShare.Agent;

/// <summary>Machine identity announced to other PCs.</summary>
public sealed record AgentIdentity(string MachineId, string MachineName);

/// <summary>The control API the GUI uses. Only reachable from this machine, see <see cref="AccessGuard"/>.</summary>
public static partial class LocalApi
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", async (AgentIdentity me, DiscoveryService discovery, GameLibrary library, DownloadManager downloads, CancellationToken ct) =>
        {
            var games = await library.ListAsync(ct);
            var active = (await downloads.ListAsync(ct)).Count(d => d.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying);
            return new StatusDto(me.MachineId, me.MachineName, typeof(LocalApi).Assembly.GetName().Version?.ToString() ?? "0",
                discovery.Peers.Count, games.Count(g => g.Installation is { State: InstallationState.Installed }), active);
        });

        api.MapGet("/settings", (SettingsService settings) => settings.Current);
        api.MapPut("/settings", async (SettingsDto request, SettingsService settings, CancellationToken ct) =>
            await settings.UpdateAsync(request, ct));

        // The configured game folders with free space, for a GUI that lets the player pick where to install.
        api.MapGet("/settings/roots", (SettingsService settings) =>
            settings.Current.GameRoots.Select(r => new GameRootDto(r, DownloadManager.GetFreeSpace(r))).ToList());

        // The tail of today's log file, so the GUI can show what the agent is doing without a trip to the data folder.
        api.MapGet("/logs", (int? lines, AgentOptions options) => TailLog(options.ResolveDataDir(), Math.Clamp(lines ?? 200, 1, 2000)));

        // The administrator's list of verified games, and whether it loaded. Refreshing asks the source again now.
        api.MapGet("/trust", (TrustService trust) => trust.Status());
        api.MapPost("/trust/refresh", async (TrustService trust, CancellationToken ct) => await trust.RefreshAsync(ct));

        api.MapGet("/peers", (DiscoveryService discovery, GameView view) => discovery.Peers.Select(view.ToDto).ToList());

        // What one PC on the LAN offers, for the network view's expanded row. Empty for a PC that is unknown or offers nothing.
        api.MapGet("/peers/{machineId}/games", (string machineId, PeerCatalog catalog) =>
            catalog.Offers.Where(o => o.Peer.MachineId == machineId).Select(o => o.Game).ToList());

        api.MapGet("/games", async (GameView view, CancellationToken ct) => await view.ListGamesAsync(ct));

        api.MapGet("/games/{contentHash}", async (string contentHash, GameView view, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            return await view.GetGameAsync(contentHash, ct) ?? throw new KeyNotFoundException($"No game with content hash {contentHash} is known.");
        });

        api.MapPost("/games/scan", async (ScanService scan, CancellationToken ct) => await scan.ScanAsync(ct));

        api.MapPost("/games/{contentHash}/install", async (
            string contentHash, [FromBody] InstallRequest? request,
            SettingsService settings, GameShareDb db, PeerCatalog catalog, DownloadManager downloads, GameView view, TrustService trust, CancellationToken ct) =>
        {
            RequireHash(contentHash);

            var root = request?.TargetRoot ?? settings.Current.GameRoots.FirstOrDefault()
                ?? throw new ArgumentException("No game folder is configured. Add one in the settings first.");
            // The GUI may only choose among the configured folders, never an arbitrary place on disk.
            if (!settings.IsConfiguredRoot(root))
                throw new ArgumentException($"'{root}' is not one of the configured game folders: {string.Join(", ", settings.Current.GameRoots)}.");

            RequireFullyAvailable(catalog, contentHash);
            var (manifest, torrent) = await ResolveAsync(contentHash, db, catalog, ct);
            trust.RequireAllowed(contentHash, manifest.Name);

            var status = await downloads.StartInstallAsync(manifest, torrent, root, ct);
            return Results.Accepted($"/api/downloads/{status.Id}", view.ToDto(status));
        });

        // ---- looking after a game that is already installed ----

        // Full verification. Says what changed and brings the recorded state in line: a damaged game is still offered, but only its intact pieces.
        api.MapPost("/games/{contentHash}/check", async (string contentHash, GameLibrary library, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            var c = await library.CheckAsync(contentHash, ct);
            return new GameChangesDto(c.IsIntact, c.Modified, c.Missing, c.Added, c.SuggestedPatterns);
        });

        // What the client starts, after the agent has checked it: a program of the game, still as verified, and not a withdrawn version.
        api.MapPost("/games/{contentHash}/launch", async (string contentHash, LaunchService launch, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            return await launch.PrepareAsync(contentHash, ct);
        });

        // The programs of the game the player can choose from, for a game that does not say which one starts it.
        api.MapGet("/games/{contentHash}/executables", async (string contentHash, LaunchService launch, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            return await launch.CandidatesAsync(contentHash, ct);
        });

        api.MapPut("/games/{contentHash}/launcher", async (string contentHash, LauncherChoiceRequest request, LaunchService launch, GameView view, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            await launch.ChooseAsync(contentHash, request?.Executable ?? "", request?.Arguments, ct);
            return await view.GetGameAsync(contentHash, ct);
        });

        // Accept the files as they are now as the game's new version. After deliberately patching a game.
        api.MapPost("/games/{contentHash}/register", async (string contentHash, GameShareDb db, GameLibrary library, GameView view, RunningGames running, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            var inst = await RequireInstallationAsync(contentHash, db, ct);
            await RequireNotRunningAsync(running, inst, ct);
            var game = await library.RescanAsync(inst.InstallPath, ct: ct);
            return await view.GetGameAsync(game.Stored.Manifest.ContentHash, ct);
        });

        // Tell GameShare which files this game rewrites while it is played. They stop counting as game content.
        api.MapPost("/games/{contentHash}/volatile", async (
            string contentHash, AddVolatileRequest request, GameShareDb db, GameLibrary library, GameView view, RunningGames running, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            if (request?.Patterns is not { Count: > 0 }) throw new ArgumentException("Send at least one pattern, for example \"saves/**\".");
            // A bad pattern is a mistake in the request, not in the stored data, so answer 400 and name it.
            try { Storage.VolatileMatcher.Create(request.Patterns); }
            catch (InvalidDataException ex) { throw new ArgumentException(ex.Message, ex); }
            var inst = await RequireInstallationAsync(contentHash, db, ct);
            await RequireNotRunningAsync(running, inst, ct);
            var game = await library.RescanAsync(inst.InstallPath, request.Patterns, ct);
            return await view.GetGameAsync(game.Stored.Manifest.ContentHash, ct);
        });

        // Restore the installed files to what the manifest says, from other PCs. The only thing that overwrites changed files.
        api.MapPost("/games/{contentHash}/repair", async (string contentHash, GameShareDb db, DownloadManager downloads, GameView view, RunningGames running, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            var inst = await RequireInstallationAsync(contentHash, db, ct);
            await RequireNotRunningAsync(running, inst, ct);
            var status = await downloads.StartRepairAsync(inst.Id, ct);
            return Results.Accepted($"/api/downloads/{status.Id}", view.ToDto(status));
        });

        // Bring the installed version of a game to this version, fetching only what differs.
        api.MapPost("/games/{contentHash}/update", async (
            string contentHash, GameShareDb db, PeerCatalog catalog, DownloadManager downloads, GameView view, TrustService trust, RunningGames running, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            RequireFullyAvailable(catalog, contentHash);
            var (manifest, torrent) = await ResolveAsync(contentHash, db, catalog, ct);
            trust.RequireAllowed(contentHash, manifest.Name);

            var installs = new List<Installation>();
            foreach (var i in await db.ListInstallationsAsync(ct))
                if ((await db.GetManifestAsync(i.ContentHash, ct))?.Manifest.GameId == manifest.GameId) installs.Add(i);
            if (installs.Count == 0) throw new InvalidOperationException($"{manifest.Name} is not installed on this PC, so there is nothing to update. Install it instead.");
            if (installs.Count > 1) throw new InvalidOperationException($"{manifest.Name} is installed in {installs.Count} places, GameShare cannot tell which one to update.");
            await RequireNotRunningAsync(running, installs[0], ct);

            var status = await downloads.StartUpdateAsync(installs[0].Id, manifest, torrent, ct);
            return Results.Accepted($"/api/downloads/{status.Id}", view.ToDto(status));
        });

        // Take the game out of service: stop offering it, delete its files and forget it was installed.
        api.MapDelete("/games/{contentHash}", async (string contentHash, GameShareDb db, DownloadManager downloads, RunningGames running, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            var inst = await RequireInstallationAsync(contentHash, db, ct);
            await RequireNotRunningAsync(running, inst, ct);
            await downloads.UninstallAsync(inst.Id, deleteFiles: true, ct);
            return Results.NoContent();
        });

        api.MapGet("/downloads", async (DownloadManager downloads, GameView view, CancellationToken ct) =>
            (await downloads.ListAsync(ct)).Select(view.ToDto).ToList());

        api.MapGet("/downloads/{id:long}", async (long id, DownloadManager downloads, GameView view, CancellationToken ct) =>
            view.ToDto(await downloads.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Download {id} does not exist.")));

        api.MapPost("/downloads/{id:long}/pause", async (long id, DownloadManager downloads, GameView view, CancellationToken ct) =>
        {
            await downloads.PauseAsync(id, ct);
            return view.ToDto((await downloads.GetAsync(id, ct))!);
        });

        api.MapPost("/downloads/{id:long}/resume", async (long id, DownloadManager downloads, GameView view, CancellationToken ct) =>
        {
            await downloads.ResumeAsync(id, ct);
            return view.ToDto((await downloads.GetAsync(id, ct))!);
        });

        api.MapDelete("/downloads/{id:long}", async (long id, [FromQuery] bool? deleteFiles, DownloadManager downloads, CancellationToken ct) =>
        {
            await downloads.CancelAsync(id, deleteFiles ?? false, ct);
            return Results.NoContent();
        });
    }

    /// <summary>The manifest and torrent of a version: from this PC if it has them, otherwise from a PC that offers it.</summary>
    private static async Task<(GameManifest Manifest, byte[] Torrent)> ResolveAsync(string contentHash, GameShareDb db, PeerCatalog catalog, CancellationToken ct)
    {
        var stored = await db.GetManifestAsync(contentHash, ct);
        return stored?.TorrentBytes is not null ? (stored.Manifest, stored.TorrentBytes) : await catalog.FetchAsync(contentHash, ct);
    }

    private static async Task<Installation> RequireInstallationAsync(string contentHash, GameShareDb db, CancellationToken ct) =>
        (await db.ListInstallationsAsync(ct)).FirstOrDefault(i => i.ContentHash == contentHash)
        ?? throw new KeyNotFoundException($"Game {contentHash} is not installed on this PC.");

    /// <summary>
    /// Installing needs every piece from somewhere. If the PCs that are online have only parts of the game and those parts do not add up,
    /// the download would stall, so say so now and let the user wait for the PC that has the rest.
    /// </summary>
    private static void RequireFullyAvailable(PeerCatalog catalog, string contentHash)
    {
        var (fully, coverage) = catalog.Availability(contentHash);
        if (!fully)
            throw new InvalidOperationException(
                $"Only {coverage:0.#} % of this game is available on the LAN right now, so it cannot be installed yet. " +
                "The PCs that have it were used to play it and have only parts of it. It works as soon as a PC with the missing parts is online.");
    }

    /// <summary>A repair, an update or registering rewrites or reads every file. Not under a game that is running and holding them.</summary>
    private static async Task RequireNotRunningAsync(RunningGames running, Installation installation, CancellationToken ct)
    {
        if (await running.IsRunningNowAsync(installation, ct))
            throw new InvalidOperationException("The game is running. Close it first, then try again.");
    }

    private static void RequireHash(string contentHash)
    {
        if (!Sha256Hex().IsMatch(contentHash))
            throw new ArgumentException("The game id must be 64 lowercase hexadecimal characters, the content hash from the game list.");
    }

    /// <summary>The last <paramref name="maxLines"/> lines of the newest "agent-*.log" file (see AgentHost's file sink),
    /// opened for shared read so a log still being written does not block this. Empty when nothing has logged yet.</summary>
    private static List<string> TailLog(string dataDir, int maxLines)
    {
        var dir = Path.Combine(dataDir, "logs");
        var newest = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "agent-*.log").OrderByDescending(f => f).FirstOrDefault() : null;
        if (newest is null) return [];

        using var stream = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        for (string? line; (line = reader.ReadLine()) is not null;) lines.Add(line);
        return lines.Count <= maxLines ? lines : lines.GetRange(lines.Count - maxLines, maxLines);
    }
}
