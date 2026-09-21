using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Storage.Tests;

public class PathPatternTests
{
    private static bool Matches(string pattern, string path)
    {
        Assert.True(PathPattern.TryCreate(pattern, out var p, out var error), error);
        return p.IsMatch(path);
    }

    [Theory]
    // A pattern without a slash matches the file name at any depth.
    [InlineData("*.ini", "settings.ini", true)]
    [InlineData("*.ini", "Config/user.ini", true)]
    [InlineData("*.ini", "a/b/c/d.ini", true)]
    [InlineData("*.ini", "settings.ini.bak", false)]
    [InlineData("*.ini", "inisettings", false)]
    [InlineData("Thumbs.db", "content/textures/Thumbs.db", true)]
    // Matching ignores case, Windows does too.
    [InlineData("*.INI", "settings.ini", true)]
    [InlineData("Saves/**", "saves/slot1.sav", true)]
    // A folder pattern covers everything below it, at any depth, but only from the game root.
    [InlineData("saves/**", "saves/slot1.sav", true)]
    [InlineData("saves/**", "saves/deep/er/slot.sav", true)]
    [InlineData("saves/**", "content/saves/slot1.sav", false)]
    [InlineData("saves/**", "savesgame.dat", false)]
    [InlineData("saves/", "saves/slot1.sav", true)]
    [InlineData("shadercache/", "shadercache/a/b.bin", true)]
    // ** at the start crosses folders, so the folder can be anywhere.
    [InlineData("**/cache/**", "cache/x.bin", true)]
    [InlineData("**/cache/**", "a/b/cache/x.bin", true)]
    [InlineData("**/cache/**", "a/b/notcache/x.bin", false)]
    // A path pattern with a slash is anchored at the root, and * does not cross folders.
    [InlineData("Config/user.cfg", "Config/user.cfg", true)]
    [InlineData("Config/user.cfg", "other/Config/user.cfg", false)]
    [InlineData("Config/*.cfg", "Config/user.cfg", true)]
    [InlineData("Config/*.cfg", "Config/sub/user.cfg", false)]
    [InlineData("data??.bin", "data01.bin", true)]
    [InlineData("data??.bin", "data1.bin", false)]
    // Characters that mean something in a regular expression are just characters here.
    [InlineData("a+b(1).sav", "a+b(1).sav", true)]
    [InlineData("a+b(1).sav", "aab(1).sav", false)]
    public void Patterns_match_as_documented(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Matches(pattern, path));

    [Theory]
    [InlineData("./saves/**", "saves/x.sav")]        // leading ./ is dropped
    [InlineData("/saves/**", "saves/x.sav")]         // leading slash is dropped
    [InlineData("saves\\**", "saves/x.sav")]          // backslashes are slashes
    [InlineData("  *.ini  ", "a.ini")]                // surrounding whitespace is ignored
    public void Patterns_are_normalised(string pattern, string path) => Assert.True(Matches(pattern, path));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("*", "every file")]
    [InlineData("**", "every file")]
    [InlineData("**/**", "every file")]
    [InlineData("../outside/**", "..")]
    [InlineData("a/../../b", "..")]
    [InlineData("C:/Games/**", "drive letters")]
    [InlineData("saves/**\u0007", "control characters")]
    public void Dangerous_or_meaningless_patterns_are_refused_with_a_reason(string pattern, string reasonContains)
    {
        Assert.False(PathPattern.TryCreate(pattern, out _, out var error));
        Assert.Contains(reasonContains, error);
    }

    [Fact]
    public void A_very_long_pattern_is_refused() =>
        Assert.False(PathPattern.TryCreate(new string('a', 300), out _, out _));

    [Fact]
    public void Matching_is_not_slow_on_pathological_input()
    {
        // Several ** in a row must not make the regular expression backtrack for ages.
        Assert.True(PathPattern.TryCreate("**/a/**/a/**/a/**/b", out var p, out _));
        var path = string.Join('/', Enumerable.Repeat("a", 60)) + "/c";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(p.IsMatch(path));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"matching took {sw.Elapsed.TotalSeconds:F1} s");
    }
}

public class VolatileMatcherTests
{
    [Fact]
    public void Matcher_normalises_deduplicates_and_sorts_its_patterns()
    {
        var m = VolatileMatcher.Create(["saves/**", "*.INI", "./saves/**", "*.ini", "a\\b/**"]);

        Assert.Equal(["*.INI", "a/b/**", "saves/**"], m.Patterns.Select(p => p == "*.ini" ? "*.INI" : p)); // case duplicates collapse
        Assert.Equal(3, m.Patterns.Count);
        Assert.True(m.IsMatch("x/y/z.ini"));
        Assert.False(m.IsMatch("game.exe"));
    }

    [Fact]
    public void An_invalid_pattern_fails_with_a_message_that_names_it()
    {
        var ex = Assert.Throws<InvalidDataException>(() => VolatileMatcher.Create(["saves/**", "../evil"]));
        Assert.Contains("../evil", ex.Message);
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void Too_many_patterns_are_refused()
    {
        var many = Enumerable.Range(0, VolatileRules.MaxPatterns + 1).Select(i => $"folder{i}/**");
        Assert.Throws<InvalidDataException>(() => VolatileMatcher.Create(many));
    }

    [Fact]
    public void Resolve_combines_defaults_definition_and_what_the_manifest_already_excluded()
    {
        var definition = new GameDefinition { GameId = "g", Name = "G", Volatile = ["saves/**"] };

        var m = VolatileRules.Resolve(definition, alreadyRecorded: ["shadercache/**"], additional: ["*.cfg"]);

        Assert.All(new[] { "saves/**", "shadercache/**", "*.cfg", "*.log", "*.tmp", "*.dmp", "Thumbs.db" }, p => Assert.Contains(p, m.Patterns));
    }

    [Fact]
    public void Same_set_ignores_order_and_case_but_not_content()
    {
        Assert.True(VolatileRules.SameSet(["a/**", "*.ini"], ["*.INI", "a/**"]));
        Assert.False(VolatileRules.SameSet(["a/**"], ["a/**", "b/**"]));
        Assert.False(VolatileRules.SameSet(["a/**"], ["b/**"]));
    }

    [Fact]
    public void Suggestions_group_files_in_folders_and_keep_root_files_by_name()
    {
        var s = VolatileRules.Suggest(["saves/slot1.sav", "saves/slot2.sav", "profile/a/b.dat", "settings.ini"]);

        Assert.Equal(["profile/**", "saves/**", "settings.ini"], s);
    }
}

public class VolatileScanTests
{
    private static async Task WriteAsync(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    [Fact]
    public async Task Default_patterns_keep_logs_and_temp_files_out_of_the_game()
    {
        using var g = new TestGame();
        await WriteAsync(g.GameDir, "game.log", "log");
        await WriteAsync(g.GameDir, "content/cache.tmp", "tmp");
        await WriteAsync(g.GameDir, "crash.dmp", "dump");

        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);

        Assert.Equal(TestGame.FileCount, m.Files.Count);
        Assert.DoesNotContain(m.Files, f => f.Path.EndsWith(".log") || f.Path.EndsWith(".tmp") || f.Path.EndsWith(".dmp"));
        Assert.Contains("*.log", m.VolatilePatterns);
    }

    [Fact]
    public async Task Changing_a_volatile_file_never_changes_the_game_identity_but_changing_content_does()
    {
        using var a = new TestGame();
        using var b = new TestGame();
        await WriteAsync(a.GameDir, "saves/slot1.sav", "progress: level 3");
        await WriteAsync(b.GameDir, "saves/slot1.sav", "progress: level 99, plus a new hat");
        await WriteAsync(b.GameDir, "saves/slot2.sav", "another save");
        string[] patterns = ["saves/**"];

        var (ma, _) = await ManifestBuilder.ScanAndBuildAsync(a.GameDir, additional: patterns);
        var (mb, _) = await ManifestBuilder.ScanAndBuildAsync(b.GameDir, additional: patterns);

        Assert.Equal(ma.ContentHash, mb.ContentHash);
        Assert.DoesNotContain(mb.Files, f => f.Path.StartsWith("saves/"));

        TestGame.CorruptOneByte(Path.Combine(b.GameDir, "Game.exe"));
        var (mc, _) = await ManifestBuilder.ScanAndBuildAsync(b.GameDir, additional: patterns);
        Assert.NotEqual(ma.ContentHash, mc.ContentHash);
    }

    [Fact]
    public async Task Two_pcs_with_different_pattern_lists_still_agree_when_the_resulting_files_are_the_same()
    {
        using var a = new TestGame();
        using var b = new TestGame();
        await WriteAsync(a.GameDir, "saves/x.sav", "1");

        var (ma, sa) = await ManifestBuilder.ScanAndBuildAsync(a.GameDir, additional: ["saves/**"]);
        var (mb, sb) = await ManifestBuilder.ScanAndBuildAsync(b.GameDir); // never had saves, no patterns beyond the defaults

        Assert.Equal(ma.ContentHash, mb.ContentHash);
        Assert.NotEqual(ma.VolatilePatterns, mb.VolatilePatterns); // recorded per PC, but not part of the identity
        Assert.Equal(
            Torrent_InfoHashOf(sa), Torrent_InfoHashOf(sb)); // and the transfer metadata agrees too, so both PCs are one swarm
    }

    // The torrent lives in another project. What matters here is that piece hashes and layout are equal.
    private static string Torrent_InfoHashOf(ScanResult scan) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            scan.PieceHashes.Concat(System.Text.Encoding.UTF8.GetBytes(scan.FolderName + scan.PieceLength + string.Join('|', scan.Files.Select(f => f.Path + f.Size)))).ToArray()));

    [Fact]
    public async Task Definition_file_patterns_are_applied_and_recorded_in_the_manifest()
    {
        using var g = new TestGame();
        await WriteAsync(g.GameDir, "saves/slot1.sav", "x");
        await WriteAsync(g.GameDir, "options.cfg", "y");
        await File.WriteAllTextAsync(Path.Combine(g.GameDir, "gameshare.json"),
            """{ "gameId": "test-game", "name": "Test Game", "volatile": ["saves/**", "options.cfg"] }""");

        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);

        Assert.Equal(TestGame.FileCount, m.Files.Count);
        Assert.Contains("saves/**", m.VolatilePatterns);
        Assert.Contains("options.cfg", m.VolatilePatterns);
        Assert.Equal(["saves/**", "options.cfg"], m.Definition!.Volatile);
    }

    [Fact]
    public async Task A_bad_pattern_in_the_definition_file_fails_naming_the_file_and_the_pattern()
    {
        using var g = new TestGame();
        var file = Path.Combine(g.GameDir, "gameshare.json");
        await File.WriteAllTextAsync(file, """{ "gameId": "test-game", "name": "T", "volatile": ["saves/**", "../escape"] }""");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => GameDefinitionFile.TryLoadAsync(g.GameDir));

        Assert.Contains(file, ex.Message);
        Assert.Contains("../escape", ex.Message);
    }

    [Fact]
    public async Task Verification_ignores_volatile_files_and_does_not_list_them_as_extra()
    {
        using var g = new TestGame();
        await WriteAsync(g.GameDir, "saves/slot1.sav", "before");
        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir, additional: ["saves/**"]);

        await WriteAsync(g.GameDir, "saves/slot1.sav", "after a long play session");
        await WriteAsync(g.GameDir, "saves/slot2.sav", "new");
        await WriteAsync(g.GameDir, "game.log", "log");
        await WriteAsync(g.GameDir, "notes.txt", "a real extra file");

        var r = await ManifestVerifier.VerifyAsync(m, g.GameDir, VerifyMode.Full);

        Assert.True(r.IsValid, r.ToString());
        Assert.Equal(["notes.txt"], r.Extra); // only what is neither content nor volatile
    }

    [Fact]
    public async Task Manifest_from_another_pc_with_a_hostile_pattern_is_rejected()
    {
        using var g = new TestGame();
        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);

        var evil = m with { VolatilePatterns = [.. m.VolatilePatterns, "../../Windows/**"] };
        var everything = m with { VolatilePatterns = ["**"] };

        Assert.Contains(ManifestValidator.Validate(evil), e => e.Contains("Invalid volatile pattern"));
        Assert.Contains(ManifestValidator.Validate(everything), e => e.Contains("every file"));
        Assert.Empty(ManifestValidator.Validate(m));
    }

    [Fact]
    public async Task Older_manifests_without_the_field_still_load()
    {
        using var g = new TestGame();
        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);
        var json = System.Text.Json.Nodes.JsonNode.Parse(GameShareJson.Serialize(m))!.AsObject();
        json.Remove("volatilePatterns");

        var back = GameShareJson.Deserialize<GameManifest>(json.ToJsonString());

        Assert.Empty(back.VolatilePatterns);
        Assert.Empty(ManifestValidator.Validate(back));
    }
}
