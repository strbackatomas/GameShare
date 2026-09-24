using System.Collections.Concurrent;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent;

/// <summary>
/// Preparing a PC for a game, what the old LAN party installer did after copying: redistributables, registry, compatibility mode,
/// profile. The agent plans and checks, and runs nothing: some steps need the player (their registry, their Documents) and some need
/// administrator rights the player grants once. The client shows the plan, the player confirms, the client runs it.
/// </summary>
public sealed class SetupService
{
    private const string KeyPrefix = "setup.";
    /// <summary>The gameId a shared redistributables package is published under, so a PC that lacks it can find it on the LAN.</summary>
    public const string RedistGameId = "redist";

    private readonly GameShareDb _db;
    private readonly GameLibrary _library;
    private readonly TrustService _trust;
    private readonly PeerCatalog _catalog;
    private readonly ISetupProbe _probe;
    private readonly ILogger<SetupService> _log;
    private readonly ConcurrentDictionary<string, string?> _done = new(StringComparer.Ordinal);

    public SetupService(GameShareDb db, GameLibrary library, TrustService trust, PeerCatalog catalog, ISetupProbe probe, ILogger<SetupService> log)
    {
        _db = db;
        _library = library;
        _trust = trust;
        _catalog = catalog;
        _probe = probe;
        _log = log;
    }

    /// <summary>Whether the game has setup steps that were not run here for its current setup and folder.</summary>
    public async Task<bool> NeedsSetupAsync(GameManifest manifest, Installation? installation, CancellationToken ct = default)
    {
        if (installation is null || !SetupPlanner.HasSetup(manifest)) return false;
        var hash = SetupPlanner.SetupHash(manifest.Definition!, installation.InstallPath);
        return await DoneHashAsync(manifest.GameId, ct).ConfigureAwait(false) != hash;
    }

    /// <summary>The checked plan for this PC, including why it must not run when it must not.</summary>
    public async Task<SetupPlanDto> PlanAsync(string contentHash, CancellationToken ct = default)
    {
        var (installation, manifest) = await InstalledAsync(contentHash, ct).ConfigureAwait(false);
        if (!SetupPlanner.HasSetup(manifest))
            return new SetupPlanDto(contentHash, manifest.Name, "", [], _trust.CheckDefinition(contentHash, manifest.Definition));

        var verdict = _trust.CheckDefinition(contentHash, manifest.Definition);
        if (verdict == DefinitionVerdict.Different)
        {
            // An administrator changed the definition and signed it after this PC installed the game: fetch the signed one from the LAN.
            if (await RefreshDefinitionAsync(contentHash, ct).ConfigureAwait(false) is { } refreshed)
            {
                manifest = refreshed;
                verdict = _trust.CheckDefinition(contentHash, manifest.Definition);
                if (!SetupPlanner.HasSetup(manifest)) return new SetupPlanDto(contentHash, manifest.Name, "", [], verdict);
            }
        }

        var packages = await PackagesAsync(ct).ConfigureAwait(false);
        var plan = await SetupPlanner.PlanAsync(manifest, installation.InstallPath, packages.Select(p => p.Package).ToList(), _probe, ct).ConfigureAwait(false);

        string? blocked = null, warning = null, missingHash = null;
        var (gameVerdict, note) = _trust.Check(contentHash);
        if (gameVerdict == TrustVerdict.Revoked) blocked = $"Správce tuto verzi hry stáhl: {note}";
        else if (plan.Problems.Count > 0) blocked = "Definice hry obsahuje kroky, které GameShare neprovede: " + string.Join(" ", plan.Problems);
        else if (_trust.Mode == TrustMode.Require && verdict != DefinitionVerdict.Verified)
            blocked = verdict == DefinitionVerdict.Different
                ? "Tahle definice hry není ta, kterou podepsal správce, a tento PC pouští jen ověřené. Počkej, až bude v síti PC s podepsanou verzí."
                : "Správce definici této hry nepodepsal a tento PC pouští přípravu jen u ověřených her.";
        else if (plan.MissingRedists.Count > 0)
        {
            missingHash = _catalog.Offers.FirstOrDefault(o => o.Game.GameId == RedistGameId)?.Game.ContentHash;
            blocked = missingHash is null
                ? $"Hra potřebuje knihovny ({string.Join(", ", plan.MissingRedists)}) ze sdíleného balíčku, který na tomto PC není a v síti ho teď nikdo nenabízí."
                : $"Hra potřebuje knihovny ({string.Join(", ", plan.MissingRedists)}) ze sdíleného balíčku. Nejdřív ho nainstaluj.";
        }
        else if (_trust.Mode == TrustMode.Require && packages.Where(p => !p.DefinitionVerified).Select(p => p.Package).FirstOrDefault() is { } unverified)
            blocked = $"Balíček {unverified.Manifest.Name} nemá podepsanou definici a tento PC pouští přípravu jen u ověřených.";

        if (blocked is null && verdict is DefinitionVerdict.NotSigned or DefinitionVerdict.Different && _trust.Mode != TrustMode.Off)
            warning = verdict == DefinitionVerdict.Different
                ? "Pozor: tahle definice hry není ta, kterou podepsal správce. Kroky níže přišly z jiného PC."
                : "Správce definici této hry nepodepsal. Kroky níže přišly z PC, od kterého je hra.";

        if (blocked is null)
        {
            foreach (var (path, hash) in plan.FilesToVerify)
            {
                if (!File.Exists(path) || await ManifestVerifier.HashFileAsync(path, ct).ConfigureAwait(false) != hash)
                {
                    blocked = $"Soubor {path} chybí nebo se změnil. Oprav hru (nebo balíček knihoven) a zkus to znovu.";
                    break;
                }
            }
        }

        return new SetupPlanDto(contentHash, manifest.Name, plan.SetupHash, plan.Steps, verdict, blocked, warning, missingHash);
    }

    /// <summary>Remembers the preparation as done, for this game's setup in this folder.</summary>
    /// <exception cref="InvalidOperationException">The setup changed since the plan was made. Plan again.</exception>
    public async Task MarkDoneAsync(string contentHash, string setupHash, CancellationToken ct = default)
    {
        var (installation, manifest) = await InstalledAsync(contentHash, ct).ConfigureAwait(false);
        if (!SetupPlanner.HasSetup(manifest)) return;
        if (SetupPlanner.SetupHash(manifest.Definition!, installation.InstallPath) != setupHash)
            throw new InvalidOperationException($"The setup of {manifest.Name} changed while it was being prepared. Prepare it again.");
        await _db.SetSettingAsync(KeyPrefix + manifest.GameId, setupHash, ct).ConfigureAwait(false);
        _done[manifest.GameId] = setupHash;
        _log.LogInformation("Setup of {Name} done", manifest.Name);
    }

    /// <summary>Forgets that the game was prepared, so Play asks again. For another player on the same PC, or after a failed step.</summary>
    public async Task ResetAsync(string contentHash, CancellationToken ct = default)
    {
        var (_, manifest) = await InstalledAsync(contentHash, ct).ConfigureAwait(false);
        await _db.SetSettingAsync(KeyPrefix + manifest.GameId, "", ct).ConfigureAwait(false);
        _done[manifest.GameId] = "";
    }

    private async Task<string?> DoneHashAsync(string gameId, CancellationToken ct)
    {
        if (_done.TryGetValue(gameId, out var known)) return known;
        return _done[gameId] = await _db.GetSettingAsync(KeyPrefix + gameId, ct).ConfigureAwait(false);
    }

    private async Task<List<(InstalledPackage Package, bool DefinitionVerified)>> PackagesAsync(CancellationToken ct)
    {
        var result = new List<(InstalledPackage, bool)>();
        foreach (var installation in await _db.ListInstallationsAsync(ct).ConfigureAwait(false))
        {
            if (installation.State != InstallationState.Installed) continue;
            var stored = await _db.GetManifestAsync(installation.ContentHash, ct).ConfigureAwait(false);
            if (stored?.Manifest.Definition is not { Kind: GameKind.Redist }) continue;
            if (_trust.Check(installation.ContentHash).Verdict == TrustVerdict.Revoked) continue;
            var verified = _trust.CheckDefinition(installation.ContentHash, stored.Manifest.Definition) == DefinitionVerdict.Verified;
            result.Add((new InstalledPackage(stored.Manifest, installation.InstallPath), verified));
        }
        return result;
    }

    private async Task<GameManifest?> RefreshDefinitionAsync(string contentHash, CancellationToken ct)
    {
        try
        {
            var (fetched, _) = await _catalog.FetchAsync(contentHash, ct, m => _trust.CheckDefinition(contentHash, m.Definition) == DefinitionVerdict.Verified).ConfigureAwait(false);
            if (_trust.CheckDefinition(contentHash, fetched.Definition) != DefinitionVerdict.Verified || fetched.Definition is null) return null;
            return await _library.ReplaceDefinitionAsync(contentHash, fetched.Definition, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or HttpRequestException)
        {
            _log.LogInformation("No PC on the LAN has the signed definition of {Hash}: {Reason}", contentHash[..12], ex.Message);
            return null;
        }
    }

    private async Task<(Installation Installation, GameManifest Manifest)> InstalledAsync(string contentHash, CancellationToken ct)
    {
        var installation = (await _db.ListInstallationsAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.ContentHash == contentHash)
            ?? throw new KeyNotFoundException($"Game {contentHash} is not installed on this PC.");
        var stored = await _db.GetManifestAsync(contentHash, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No manifest is stored for {contentHash}.");
        return (installation, stored.Manifest);
    }
}
