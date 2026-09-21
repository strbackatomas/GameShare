using System.Net;
using System.Net.Http.Json;
using GameShare.Protocol;
using GameShare.Storage;
using Microsoft.Data.Sqlite;

namespace GameShare.Agent.Tests;

/// <summary>The administrator's signed list of verified games, through real agents and a real list file.</summary>
public class TrustTests : IDisposable
{
    private const long SmallGame = 2_000_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _dir = TestGame.NewTempDir();
    private readonly TrustKeyPair _keys = TrustSigning.GenerateKeyPair();

    private string ListPath => Path.Combine(_dir, "trust.json");
    private static string Fake(char c) => new(c, 64);

    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private void WriteList(long sequence, string[] verified, (string Hash, string Reason)[]? revoked = null, TrustKeyPair? signer = null, TimeSpan? validFor = null)
    {
        var payload = new TrustPayload(
            sequence, Now, validFor is null ? null : Now + validFor,
            verified.Select(h => new TrustedGame(h, "testgame", "TestGame", "1")).ToList(),
            (revoked ?? []).Select(r => new RevokedGame(r.Hash, r.Reason)).ToList());
        File.WriteAllBytes(ListPath, TrustSigning.Serialize(TrustSigning.Sign(payload, (signer ?? _keys).PrivateKey)));
    }

    private Action<AgentOptions> Trust(TrustMode mode) => o =>
    {
        o.TrustMode = mode;
        o.TrustListSource = ListPath;
        o.TrustPublicKey = _keys.PublicKey;
        o.TrustRefreshInterval = TimeSpan.FromHours(1); // tests refresh by hand, so they know when it happened
    };

    private static async Task<TrustStatusDto> RefreshAsync(TestAgent a)
    {
        var response = await a.SendAsync(HttpMethod.Post, "/api/trust/refresh");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TrustStatusDto>(TestAgent.Json))!;
    }

    private static Task<TrustStatusDto> StatusAsync(TestAgent a) => a.GetAsync<TrustStatusDto>("/api/trust");

    private static async Task<GameDto> InstalledAsync(TestAgent a) =>
        await a.WaitForGameAsync(g => g.State == GameState.Installed, $"{a.Name} to list the game as installed");

    /// <summary>A PC that has the game and offers it, without any trust settings.</summary>
    private static async Task<(TestAgent Source, GameDto Game)> SourceAsync(int discovery)
    {
        var source = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true, bigFileBytes: SmallGame);
        return (source, await InstalledAsync(source));
    }

    private static async Task<HttpResponseMessage> TryInstallAsync(TestAgent a, string hash) =>
        await a.SendAsync(HttpMethod.Post, $"/api/games/{hash}/install");

    [Fact]
    public async Task Warn_marks_games_by_what_the_list_says_and_follows_the_list_when_it_changes()
    {
        WriteList(1, [Fake('a')]);
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame, tweak: Trust(TrustMode.Warn));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Trust == TrustVerdict.Unknown, "the game to be marked as not in the list");

        WriteList(2, [game.ContentHash]);
        var status = await RefreshAsync(pc);
        Assert.True(status.HasList);
        Assert.Equal((2L, 1, 0), (status.Sequence, status.VerifiedCount, status.RevokedCount));
        Assert.Equal(TrustSigning.KeyId(_keys.PublicKey), status.KeyId);
        await pc.WaitForGameAsync(g => g.Trust == TrustVerdict.Verified, "the game to be verified");

        WriteList(3, [], [(game.ContentHash, "contains a modified executable")]);
        await RefreshAsync(pc);
        var revoked = await pc.WaitForGameAsync(g => g.Trust == TrustVerdict.Revoked, "the game to be revoked");
        Assert.Equal("contains a modified executable", revoked.TrustNote);
    }

    [Fact]
    public async Task A_list_that_is_tampered_with_signed_by_someone_else_or_older_is_ignored_and_the_good_one_stays()
    {
        WriteList(1, [Fake('a')]);
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame, tweak: Trust(TrustMode.Warn));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Trust == TrustVerdict.Unknown, "a list to be loaded");
        WriteList(5, [game.ContentHash]);
        await RefreshAsync(pc);
        await pc.WaitForGameAsync(g => g.Trust == TrustVerdict.Verified, "the game to be verified");

        File.WriteAllText(ListPath, "this is not a signed list");
        var garbage = await RefreshAsync(pc);
        Assert.NotNull(garbage.LastError);
        Assert.Equal(5, garbage.Sequence);

        WriteList(9, [], signer: TrustSigning.GenerateKeyPair()); // someone else's key, and it would remove the game
        Assert.Contains("signed with key", (await RefreshAsync(pc)).LastError);

        WriteList(2, []); // validly signed, but older than what is held: a replay that could hide a revocation
        var older = await RefreshAsync(pc);
        Assert.Contains("older list is never used", older.LastError);
        Assert.Equal(5, older.Sequence);

        Assert.Equal(TrustVerdict.Verified, (await pc.GamesAsync()).Single().Trust); // through all of it

        WriteList(6, [game.ContentHash]); // and a good newer list works again
        var recovered = await RefreshAsync(pc);
        Assert.Null(recovered.LastError);
        Assert.Equal(6, recovered.Sequence);
    }

    [Fact]
    public async Task Require_refuses_a_game_that_is_not_in_the_list_and_installs_it_once_it_is()
    {
        int discovery = TestAgent.DiscoveryPort();
        var (source, game) = await SourceAsync(discovery);
        await using var _ = source;
        WriteList(1, [Fake('a')]);
        await using var pc = await TestAgent.StartAsync("PC-02", discovery, tweak: Trust(TrustMode.Require));
        await pc.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.State == GameState.AvailableOnLan && g.Trust == TrustVerdict.Unknown, "the offered game to be marked");

        var refused = await TryInstallAsync(pc, game.ContentHash);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not in the administrator's list", await refused.Content.ReadAsStringAsync());
        Assert.Empty(await pc.GetAsync<List<DownloadDto>>("/api/downloads")); // nothing was started

        WriteList(2, [game.ContentHash]);
        await RefreshAsync(pc);
        var download = await pc.InstallAsync(game.ContentHash);
        await pc.WaitForDownloadAsync(download.Id, "Completed");
        Assert.Equal(TestGame.HashTree(source.InstalledPath), TestGame.HashTree(pc.InstalledPath));
    }

    [Fact]
    public async Task Require_without_a_usable_list_installs_nothing_because_it_cannot_check()
    {
        int discovery = TestAgent.DiscoveryPort();
        var (source, game) = await SourceAsync(discovery);
        await using var _ = source;
        // No list file at all.
        await using var pc = await TestAgent.StartAsync("PC-02", discovery, tweak: Trust(TrustMode.Require));
        await pc.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.State == GameState.AvailableOnLan, "the game to be offered");
        await Poll.UntilAsync(async () => (await StatusAsync(pc)).LastError is not null, "the failed load to be recorded");

        var refused = await TryInstallAsync(pc, game.ContentHash);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = await refused.Content.ReadAsStringAsync();
        Assert.Contains("no trust list is available", message);
        Assert.Contains("does not exist", message); // why, so the administrator knows what to fix
        Assert.False((await StatusAsync(pc)).HasList);
    }

    [Fact]
    public async Task A_revoked_version_is_refused_even_in_warn_mode_but_an_unlisted_one_is_allowed()
    {
        int discovery = TestAgent.DiscoveryPort();
        var (source, game) = await SourceAsync(discovery);
        await using var _ = source;
        WriteList(1, [], [(game.ContentHash, "modified executable")]);
        await using var pc = await TestAgent.StartAsync("PC-02", discovery, tweak: Trust(TrustMode.Warn));
        await pc.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.Trust == TrustVerdict.Revoked, "the game to be marked as revoked");

        var refused = await TryInstallAsync(pc, game.ContentHash);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("withdrawn by the administrator: modified executable", await refused.Content.ReadAsStringAsync());

        WriteList(2, [Fake('a')]); // no longer revoked, and not verified either. Warn only warns.
        await RefreshAsync(pc);
        await pc.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.Trust == TrustVerdict.Unknown, "the game to be unlisted");
        var download = await pc.InstallAsync(game.ContentHash);
        await pc.WaitForDownloadAsync(download.Id, "Completed");
    }

    [Fact]
    public async Task A_verified_game_whose_files_changed_is_no_longer_shown_as_verified()
    {
        WriteList(1, [Fake('a')]);
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame, tweak: Trust(TrustMode.Warn));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Trust == TrustVerdict.Unknown, "a list to be loaded");
        WriteList(2, [game.ContentHash]);
        await RefreshAsync(pc);
        await pc.WaitForGameAsync(g => g.Trust == TrustVerdict.Verified, "the game to be verified");

        TestGame.CorruptOneByte(Path.Combine(pc.InstalledPath, "content", "big.pak"));
        var check = await pc.SendAsync(HttpMethod.Post, $"/api/games/{game.ContentHash}/check");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);

        var damaged = await pc.WaitForGameAsync(g => g.State == GameState.Damaged, "the game to show as changed");
        Assert.Equal(TrustVerdict.NotChecked, damaged.Trust); // what is on disk is no longer what the administrator vouched for
    }

    [Fact]
    public async Task An_expired_list_is_not_used_and_the_game_is_not_marked()
    {
        WriteList(1, [Fake('a')], validFor: TimeSpan.FromDays(-1));
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame, tweak: Trust(TrustMode.Warn));
        var game = await InstalledAsync(pc);
        await Poll.UntilAsync(async () => (await StatusAsync(pc)).LastError is not null, "the expired list to be reported");

        Assert.Contains("expired", (await StatusAsync(pc)).LastError);
        Assert.Equal(TrustVerdict.NotChecked, game.Trust);
        Assert.False((await StatusAsync(pc)).HasList);
    }

    [Fact]
    public async Task The_last_good_list_survives_a_restart_while_the_source_is_unreachable()
    {
        int discovery = TestAgent.DiscoveryPort();
        var dir = TestGame.NewTempDir();
        WriteList(1, [Fake('a')]);
        var pc = await TestAgent.StartAsync("PC-01", discovery, preloadGame: true, bigFileBytes: SmallGame, existingDir: dir, tweak: Trust(TrustMode.Warn));
        var game = await pc.WaitForGameAsync(g => g.State == GameState.Installed && g.Trust == TrustVerdict.Unknown, "a list to be loaded");
        WriteList(2, [game.ContentHash]);
        await RefreshAsync(pc);
        await pc.WaitForGameAsync(g => g.Trust == TrustVerdict.Verified, "the game to be verified");
        await pc.StopAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(ListPath); // the server is gone

        await using var again = await TestAgent.StartAsync("PC-01", discovery, existingDir: dir, tweak: Trust(TrustMode.Warn));

        await again.WaitForGameAsync(g => g.ContentHash == game.ContentHash && g.Trust == TrustVerdict.Verified, "the stored list to be used");
        await Poll.UntilAsync(async () => (await StatusAsync(again)).LastError is not null, "the unreachable source to be reported");
        Assert.True((await StatusAsync(again)).HasList);
    }

    [Fact]
    public async Task With_checking_off_nothing_is_marked_and_the_trust_endpoints_say_so()
    {
        await using var pc = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), preloadGame: true, bigFileBytes: SmallGame);
        var game = await InstalledAsync(pc);

        var status = await RefreshAsync(pc);

        Assert.Equal(TrustVerdict.NotChecked, game.Trust);
        Assert.Equal(TrustMode.Off, status.Mode);
        Assert.False(status.HasList);
        Assert.Null(status.Source);
    }

    [Theory]
    [InlineData(TrustMode.Warn, null, "key", "TrustListSource")]
    [InlineData(TrustMode.Require, "list.json", null, "TrustPublicKey")]
    [InlineData(TrustMode.Require, "list.json", "not a key", "not usable")]
    public void A_configuration_that_cannot_work_is_refused_at_startup_and_names_the_setting(TrustMode mode, string? source, string? key, string expected)
    {
        var options = new AgentOptions { TrustMode = mode, TrustListSource = source, TrustPublicKey = key };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(expected, ex.Message);
    }
}
