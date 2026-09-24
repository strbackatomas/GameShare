using System.Diagnostics;
using System.Text.RegularExpressions;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Discovery;
using GameShare.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;

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
            return new StatusDto(me.MachineId, me.MachineName, AppVersion.Current,
                discovery.Peers.Count, games.Count(g => g.Installation is { State: InstallationState.Installed }), active);
        });

        // Restarts the agent so a setting that is only read at startup (e.g. TorrentDebugLogging) takes effect.
        // Waits for the normal graceful shutdown (hosted services get to stop cleanly, e.g. resume data is saved)
        // before relaunching. A Windows service is left to its own configured failure/restart action (see
        // scripts/install-agent.ps1) rather than self-launched, so it stays under the Service Control Manager.
        api.MapPost("/agent/restart", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.ApplicationStopped.Register(() =>
            {
                if (!WindowsServiceHelpers.IsWindowsService())
                {
                    var exe = Environment.ProcessPath;
                    if (exe is not null)
                    {
                        try { Process.Start(exe, Environment.GetCommandLineArgs().Skip(1)); }
                        catch { /* best effort; this process is exiting anyway */ }
                    }
                }
                Environment.Exit(0);
            });
            lifetime.StopApplication();
            return Results.Accepted();
        });

        api.MapGet("/settings", (SettingsService settings) => settings.Current);
        api.MapPut("/settings", async (SettingsDto request, SettingsService settings, CancellationToken ct) =>
            await settings.UpdateAsync(request, ct));

        // The configured game folders with free space, for a GUI that lets the player pick where to install.
        api.MapGet("/settings/roots", (SettingsService settings) =>
            settings.Current.GameRoots.Select(r => new GameRootDto(r, DownloadManager.GetFreeSpace(r))).ToList());

        // Network adapters discovery could use, and which of them it currently skips because they look virtual
        // (VirtualBox, Hyper-V, …) and were not re-enabled in settings.
        api.MapGet("/settings/adapters", (SettingsService settings) =>
        {
            var unignored = settings.Current.AllowedVirtualAdapterIds ?? [];
            return UdpDatagramTransport.ListAdapters()
                .Select(a => new NetworkAdapterDto(a.Id, a.Name, a.Description, a.IsLikelyVirtual, a.IsLikelyVirtual && !unignored.Contains(a.Id)))
                .ToList();
        });

        // The tail of today's log file, so the GUI can show what the agent is doing without a trip to the data folder.
        api.MapGet("/logs", async (int? lines, AgentOptions options, GameShareDb db, CancellationToken ct) =>
            await TailLogAsync(options.ResolveDataDir(), Math.Clamp(lines ?? 200, 1, 2000), db, ct).ConfigureAwait(false));

        // Hides everything logged before now from the GUI. Nothing is deleted from disk, so it is still there for support.
        api.MapPost("/logs/clear", async (GameShareDb db, CancellationToken ct) =>
        {
            await db.SetSettingAsync(LogClearedAtKey, DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // The administrator's list of verified games, and whether it loaded. Refreshing asks the source again now.
        api.MapGet("/trust", (TrustService trust) => trust.Status());
        api.MapPost("/trust/refresh", async (TrustService trust, CancellationToken ct) => await trust.RefreshAsync(ct));

        api.MapGet("/peers", (DiscoveryService discovery, GameView view) => discovery.Peers.Select(view.ToDto).ToList());

        // Who is pulling data from this PC right now, and how fast. Only games with at least one active peer.
        api.MapGet("/uploads", async (GameView view, CancellationToken ct) => await view.ListUploadsAsync(ct));

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
            var (manifest, torrent) = await ResolveAsync(contentHash, db, catalog, trust, ct);
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
        // ?entry=N picks one of the programs the definition lists (an editor, a server), 0 or none is the game itself.
        api.MapPost("/games/{contentHash}/launch", async (string contentHash, int? entry, LaunchService launch, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            return await launch.PrepareAsync(contentHash, entry ?? 0, ct);
        });

        // Preparing this PC for the game: the checked steps for the player to confirm, and the note that they ran.
        api.MapGet("/games/{contentHash}/setup", async (string contentHash, SetupService setup, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            return await setup.PlanAsync(contentHash, ct);
        });

        api.MapPost("/games/{contentHash}/setup/done", async (string contentHash, SetupDoneRequest request, SetupService setup, GameView view, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            await setup.MarkDoneAsync(contentHash, request?.SetupHash ?? "", ct);
            return await view.GetGameAsync(contentHash, ct);
        });

        // Play asks for the preparation again, for another player on this PC or after a step failed.
        api.MapDelete("/games/{contentHash}/setup", async (string contentHash, SetupService setup, GameView view, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            await setup.ResetAsync(contentHash, ct);
            return await view.GetGameAsync(contentHash, ct);
        });

        // The picture shown next to the game's name, read from this PC's copy of the game.
        api.MapGet("/games/{contentHash}/icon", async (string contentHash, GameShareDb db, IconService icons, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            var stored = await db.GetManifestAsync(contentHash, ct);
            var installation = (await db.ListInstallationsAsync(ct)).FirstOrDefault(i => i.ContentHash == contentHash);
            return stored is not null && await icons.GetAsync(stored.Manifest, installation, ct) is { } icon
                ? Results.Bytes(icon.Bytes, icon.ContentType)
                : Results.NotFound();
        });

        // The programs of the game the player can choose from, for a game that does not say which one starts it.
        api.MapGet("/games/{contentHash}/executables", async (string contentHash, LaunchService launch, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            return await launch.CandidatesAsync(contentHash, ct);
        });

        api.MapPut("/games/{contentHash}/launcher", async (string contentHash, LauncherChoiceRequest request, LaunchService launch, IconService icons, GameView view, CancellationToken ct) =>
        {
            RequireHash(contentHash);
            await launch.ChooseAsync(contentHash, request?.Executable ?? "", request?.Arguments, ct);
            icons.Forget(contentHash); // the picture follows the program the play button starts
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
            var (manifest, torrent) = await ResolveAsync(contentHash, db, catalog, trust, ct);
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
    /// <summary>The manifest and torrent of a version, from this PC or else from a peer, preferring a peer whose definition is the signed one.</summary>
    private static async Task<(GameManifest Manifest, byte[] Torrent)> ResolveAsync(
        string contentHash, GameShareDb db, PeerCatalog catalog, TrustService trust, CancellationToken ct)
    {
        var stored = await db.GetManifestAsync(contentHash, ct);
        return stored?.TorrentBytes is not null ? (stored.Manifest, stored.TorrentBytes)
            : await catalog.FetchAsync(contentHash, ct, m => trust.CheckDefinition(contentHash, m.Definition) != DefinitionVerdict.Different);
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

    private const string LogClearedAtKey = "log.clearedAt";

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of the newest "agent-*.log" file (see AgentHost's file sink), opened for
    /// shared read so a log still being written does not block this. Empty when nothing has logged yet.
    /// Lines from before the last "/logs/clear" are left out: the file itself is never truncated while Serilog still has it
    /// open for writing, doing that from a second handle would race Serilog's own write position and corrupt the file.
    /// </summary>
    private static async Task<List<string>> TailLogAsync(string dataDir, int maxLines, GameShareDb db, CancellationToken ct)
    {
        var dir = Path.Combine(dataDir, "logs");
        var newest = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "agent-*.log").OrderByDescending(f => f).FirstOrDefault() : null;
        if (newest is null) return [];

        using var stream = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        for (string? line; (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null;) lines.Add(line);

        var clearedAtRaw = await db.GetSettingAsync(LogClearedAtKey, ct).ConfigureAwait(false);
        if (clearedAtRaw is not null && DateTimeOffset.TryParse(clearedAtRaw, out var clearedAt))
            lines = FilterClearedLines(lines, clearedAt);

        return lines.Count <= maxLines ? lines : lines.GetRange(lines.Count - maxLines, maxLines);
    }

    /// <summary>
    /// Drops every line logged before <paramref name="clearedAt"/>. A continuation line (a stack trace) has no
    /// timestamp of its own: it belongs to whichever entry logged just before it, so it is kept or dropped along with
    /// that entry rather than always being kept. Public so this can be tested without a running agent.
    /// </summary>
    public static List<string> FilterClearedLines(IReadOnlyList<string> lines, DateTimeOffset clearedAt)
    {
        var kept = new List<string>();
        bool showing = true;
        foreach (var l in lines)
        {
            if (TryParseLineTime(l, out var t)) showing = t >= clearedAt;
            if (showing) kept.Add(l);
        }
        return kept;
    }

    /// <summary>AgentHost's Serilog template starts every line with "yyyy-MM-dd HH:mm:ss.fff" in local time. A continuation
    /// line of a stack trace has none, and is kept regardless: it belongs to whichever entry logged just before it.</summary>
    private static bool TryParseLineTime(string line, out DateTimeOffset time)
    {
        time = default;
        if (line.Length < 23) return false;
        if (!DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", null, System.Globalization.DateTimeStyles.AssumeLocal, out var t))
            return false;
        time = new DateTimeOffset(t.ToUniversalTime());
        return true;
    }
}
