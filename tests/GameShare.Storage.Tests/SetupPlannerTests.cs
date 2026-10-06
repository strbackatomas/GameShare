using System.Security.Cryptography;
using System.Text;
using GameShare.Protocol;
using GameShare.Tests;

namespace GameShare.Storage.Tests;

/// <summary>.reg files as the old installer shipped them, made ready for the PC the game is installed on.</summary>
public class RegFileTests
{
    private const string Starcraft = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\Starcraft]
        "InstallPath"="C:\\Games\\Starcraft"
        "Program"="C:\\GAMES\\STARCRAFT\\Starcraft.exe"
        "Recent Maps"=hex:00,00,\
          01,02

        [HKEY_CURRENT_USER\Software\Blizzard Entertainment\Starcraft]
        "Name"="Player"
        @="default"

        [-HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\Old]
        """;

    [Fact]
    public void Utf16_utf8_and_ansi_files_all_decode()
    {
        var text = "Windows Registry Editor Version 5.00\r\n\"Jméno\"=\"Hráč\"";
        Assert.Equal(text, RegFile.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]));
        Assert.Equal(text, RegFile.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]));
        Assert.Equal(text, RegFile.Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("é", RegFile.Decode([0xE9])); // not UTF-8, so the single-byte reading
    }

    [Fact]
    public void Paths_are_pointed_at_the_real_folder_whatever_their_case()
    {
        var text = RegFile.ReplacePath(Starcraft, @"C:\Games\Starcraft", @"D:\Hry\Starcraft");

        Assert.Contains(@"""InstallPath""=""D:\\Hry\\Starcraft""", text);
        Assert.Contains(@"""Program""=""D:\\Hry\\Starcraft\\Starcraft.exe""", text); // the old installer missed this one
        Assert.DoesNotContain("Games", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_machines_part_and_the_players_part_are_separate_complete_files()
    {
        var (machine, user) = RegFile.Split(Starcraft);

        Assert.StartsWith("Windows Registry Editor Version 5.00", machine.Text);
        Assert.Contains("hex:00,00,01,02", machine.Text!.Replace("\\\r\n  ", "")); // a continued value stays one value
        Assert.Equal(3, machine.ValueCount);
        Assert.Equal([@"HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\Starcraft", @"-HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\Old"], machine.Keys);
        Assert.DoesNotContain("HKEY_CURRENT_USER", machine.Text);

        Assert.Equal(2, user.ValueCount);
        Assert.Equal([@"HKCU\Software\Blizzard Entertainment\Starcraft"], user.Keys);
        Assert.Contains("    (výchozí) = \"default\"", user.Preview);
        Assert.Contains("smazat HKLM\\SOFTWARE\\WOW6432Node\\Blizzard Entertainment\\Old", machine.Preview);
    }

    [Fact]
    public void A_file_with_only_the_players_keys_has_no_machine_part()
    {
        var (machine, user) = RegFile.Split("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Valve\\Source]\r\n\"Language\"=\"english\"\r\n");

        Assert.Null(machine.Text);
        Assert.NotNull(user.Text);
    }
}

/// <summary>A game's setup section turned into steps, with everything it names checked, because it comes from another PC.</summary>
public class SetupPlannerTests : IDisposable
{
    private readonly string _dir = TestGame.NewTempDir();
    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private sealed class Probe(params string[] installed) : ISetupProbe
    {
        public bool IsInstalled(InstalledCheck check) => installed.Contains(check.File ?? check.Uninstall ?? check.RegistryKey);
        public bool FirewallAllows(string program) => installed.Contains(Path.GetFileName(program));
    }

    private const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\EA Games\Battlefield 2]
        "InstallDir"="C:\\Games\\Battlefield 2"

        [HKEY_CURRENT_USER\Software\EA Games\Battlefield 2]
        "Nick"="player"
        """;

    /// <summary>A game folder with the given files, and its manifest with the given setup.</summary>
    private (GameManifest Manifest, string Root) Game(string name, GameSetup? setup, GameKind kind = GameKind.Game,
        Dictionary<string, RedistPackage>? provides = null, params (string Path, byte[] Content)[] files)
    {
        var root = Path.Combine(_dir, name);
        var list = new List<ManifestFile>();
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            list.Add(new ManifestFile(path, content.Length, Convert.ToHexStringLower(SHA256.HashData(content))));
        }
        var definition = new GameDefinition { GameId = name.ToLowerInvariant(), Name = name, Kind = kind, Setup = setup, Provides = provides ?? [] };
        return (new GameManifest
        {
            GameId = definition.GameId, Name = name, FolderName = name, TotalSize = list.Sum(f => f.Size), ContentHash = ContentHasher.Compute(list),
            PieceLength = 1 << 20, Files = list, Definition = definition,
        }, root);
    }

    private (GameManifest, string) Battlefield(GameSetup setup) => Game("Battlefield 2", setup, files:
    [
        ("BF2.exe", [1, 2, 3]), ("registry-import.reg", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Reg)]),
        ("Battlefield 2-profile/Profiles/Default/Profile.con", [4]), ("_redist/oalinst.exe", [5, 6]),
    ]);

    private InstalledPackage Redist()
    {
        var (m, root) = Game("_Redist", null, GameKind.Redist, new()
        {
            ["directx9"] = new RedistPackage { Name = "DirectX 9", File = "directx/DXSETUP.exe", Args = "/silent", InstalledIf = new InstalledCheck { File = "d3dx9_43" } },
            ["vcredist2005_x86"] = new RedistPackage { File = "vcredist_x86.exe", Args = "/q" },
        }, files: [("directx/DXSETUP.exe", [9]), ("vcredist_x86.exe", [8])]);
        return new InstalledPackage(m, root);
    }

    [Fact]
    public async Task The_old_installers_steps_for_battlefield_become_a_plan_in_the_order_they_run()
    {
        var (game, root) = Battlefield(new GameSetup
        {
            Requires = ["directx9", "vcredist2005_x86"],
            Redist = [new RedistStep { File = "_redist/oalinst.exe", Args = "/s" }],
            Registry = [new RegistryStep { File = "registry-import.reg", OriginalPath = @"C:\Games\Battlefield 2", Cleanup = @"Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\EA Games\Battlefield 2" }],
            Compatibility = [new CompatibilityStep { Executable = "BF2.exe", Layers = "~ WINXPSP3" }],
            Profile = [new ProfileStep { From = "Battlefield 2-profile", To = @"{Documents}\Battlefield 2" }],
        });

        var plan = await SetupPlanner.PlanAsync(game, root, [Redist()], new Probe("d3dx9_43"));

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.MissingRedists);
        Assert.Equal(
            [SetupStepKind.Redist, SetupStepKind.Redist, SetupStepKind.Redist, SetupStepKind.RegistryDelete, SetupStepKind.RegistryImport,
             SetupStepKind.RegistryImport, SetupStepKind.Compatibility, SetupStepKind.Profile],
            plan.Steps.Select(s => s.Kind));

        var directx = plan.Steps[0];
        Assert.True(directx.AlreadyDone);                                           // the probe found it
        Assert.DoesNotContain(plan.FilesToVerify, f => f.Path.EndsWith("DXSETUP.exe")); // so there is nothing to check or run
        Assert.Contains(plan.FilesToVerify, f => f.Path.EndsWith("vcredist_x86.exe"));

        Assert.Equal(@"HKLM\SOFTWARE\WOW6432Node\EA Games\Battlefield 2", plan.Steps[3].Target);
        var machine = plan.Steps[4];
        Assert.True(machine.NeedsAdmin);
        Assert.Contains(root.Replace("\\", "\\\\"), machine.Content);
        Assert.DoesNotContain("HKEY_CURRENT_USER", machine.Content);
        Assert.False(plan.Steps[5].NeedsAdmin); // the player's own keys are imported as the player

        Assert.Equal("WINXPSP3", plan.Steps[6].Arguments);
        Assert.Equal(Path.Combine(root, "Battlefield 2-profile"), plan.Steps[7].File);
        Assert.All(plan.Steps.Where(s => s.Kind is SetupStepKind.Compatibility or SetupStepKind.Profile), s => Assert.False(s.NeedsAdmin));
    }

    [Fact]
    public async Task A_redistributable_no_installed_package_provides_is_reported_missing()
    {
        var (game, root) = Battlefield(new GameSetup { Requires = ["directx9", "physx"] });

        var plan = await SetupPlanner.PlanAsync(game, root, [Redist()], new Probe());

        Assert.Equal(["physx"], plan.MissingRedists);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void After_the_installers_ran_a_redistributable_its_package_can_look_for_and_does_not_find_is_named()
    {
        var (game, _) = Battlefield(new GameSetup { Requires = ["directx9", "vcredist2005_x86", "physx"] });

        Assert.Equal(["DirectX 9"], SetupPlanner.NotInstalled(game, [Redist()], new Probe())); // vcredist has no check, physx no package
        Assert.Empty(SetupPlanner.NotInstalled(game, [Redist()], new Probe("d3dx9_43")));
    }

    [Fact]
    public async Task The_setup_hash_changes_with_the_setup_the_firewalled_programs_and_the_folder_but_not_with_anything_else()
    {
        var setup = new GameSetup { Requires = ["directx9"] };
        var (game, root) = Battlefield(setup);
        var hash = (await SetupPlanner.PlanAsync(game, root, [Redist()], new Probe())).SetupHash;
        var launched = game.Definition! with { Launch = [new LaunchEntry { Executable = "BF2.exe" }] };

        Assert.Equal(hash, SetupPlanner.SetupHash(game.Definition! with { Volatile = ["*.ini"], Version = "1.5" }, root));
        Assert.NotEqual(hash, SetupPlanner.SetupHash(launched, root));                // its program now gets a firewall rule
        Assert.Equal(SetupPlanner.SetupHash(launched, root),
            SetupPlanner.SetupHash(launched with { Launch = [new LaunchEntry { Executable = "BF2.exe", Arguments = "+menu 1", Name = "Hrát" }] }, root));
        Assert.NotEqual(hash, SetupPlanner.SetupHash(game.Definition!, Path.Combine(_dir, "elsewhere")));
        Assert.NotEqual(hash, SetupPlanner.SetupHash(game.Definition! with { Setup = setup with { Requires = ["directx9", "dotnet40"] } }, root));
    }

    [Fact]
    public async Task Every_program_in_launch_is_let_through_the_firewall_once_and_one_already_allowed_is_shown_as_done()
    {
        var (game, root) = Game("Age of Empires 2", null, files: [("empires2.exe", [1]), ("Age2_x1/age2_x1.exe", [2]), ("readme.txt", [3])]);
        game = game with
        {
            Definition = game.Definition! with
            {
                Launch =
                [
                    new LaunchEntry { Executable = "empires2.exe" }, new LaunchEntry { Executable = "Age2_X1/age2_x1.Exe", Arguments = "nosound" },
                    new LaunchEntry { Executable = "Age2_X1/age2_x1.Exe", Arguments = "mfill" }, new LaunchEntry { Executable = "missing.exe" },
                ],
            },
        };

        var plan = await SetupPlanner.PlanAsync(game, root, [], new Probe("empires2.exe"));

        Assert.True(SetupPlanner.HasSetup(game));                     // a game without a setup section is prepared for the firewall
        Assert.Empty(plan.Problems);                                   // a launch entry that is not a file is refused at launch, not here
        Assert.Equal([Path.Combine(root, "empires2.exe"), Path.Combine(root, "Age2_x1", "age2_x1.exe")], plan.Steps.Select(s => s.File));
        Assert.All(plan.Steps, s => { Assert.Equal(SetupStepKind.Firewall, s.Kind); Assert.True(s.NeedsAdmin); });
        Assert.True(plan.Steps[0].AlreadyDone);
        Assert.False(plan.Steps[1].AlreadyDone);
        Assert.Equal("GameShare – Age of Empires 2 – Age2_x1/age2_x1.exe", plan.Steps[1].Target);
        Assert.Empty(plan.FilesToVerify);                              // nothing is run, so nothing is hashed
    }

    [Fact]
    public async Task The_firewall_can_be_left_out_or_given_other_programs_of_the_game()
    {
        var files = new (string, byte[])[] { ("samp.exe", [1]), ("server/samp-server.exe", [2]), ("evil.txt", [3]) };
        GameManifest With(FirewallSetup firewall)
        {
            var (game, _) = Game("GTA", new GameSetup { Firewall = firewall }, files: files);
            return game with { Definition = game.Definition! with { Launch = [new LaunchEntry { Executable = "samp.exe" }] } };
        }
        var root = Path.Combine(_dir, "GTA");

        var off = With(new FirewallSetup { Launch = false });
        Assert.False(SetupPlanner.HasSetup(off));
        Assert.Empty((await SetupPlanner.PlanAsync(off, root, [], new Probe())).Steps);

        var server = await SetupPlanner.PlanAsync(With(new FirewallSetup { Programs = ["server\\samp-server.exe"] }), root, [], new Probe());
        Assert.Equal(["samp.exe", "samp-server.exe"], server.Steps.Select(s => Path.GetFileName(s.File!)));

        var outside = await SetupPlanner.PlanAsync(With(new FirewallSetup { Programs = ["../../Windows/System32/cmd.exe"] }), root, [], new Probe());
        Assert.Contains(outside.Problems, p => p.StartsWith("Firewall:"));
    }

    [Fact]
    public async Task A_default_file_goes_only_over_a_file_the_game_rewrites_and_is_done_where_the_game_has_one()
    {
        var files = new (string, byte[])[] { ("quake3.exe", [1]), ("_gameshare/q3config.cfg", [2]), ("baseq3/pak0.pk3", [3]) };
        (GameManifest, string) Quake(string to, params string[] volatilePatterns)
        {
            var (game, root) = Game("Quake", new GameSetup
            {
                Firewall = new FirewallSetup { Launch = false }, Defaults = [new DefaultFileStep { From = "_gameshare/q3config.cfg", To = to }],
            }, files: files);
            return (game with { Definition = game.Definition! with { Volatile = volatilePatterns } }, root);
        }

        var (quake, root) = Quake("baseq3\\q3config.cfg", "baseq3/q3config.cfg");
        var plan = await SetupPlanner.PlanAsync(quake, root, [], new Probe());
        Assert.Empty(plan.Problems);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(SetupStepKind.DefaultFile, step.Kind);
        Assert.False(step.NeedsAdmin);
        Assert.False(step.AlreadyDone);
        Assert.Equal(Path.Combine(root, "baseq3", "q3config.cfg"), step.Target);
        Assert.Equal(Path.Combine(root, "_gameshare", "q3config.cfg"), step.File);
        Assert.NotNull(step.FileHash);

        File.WriteAllText(Path.Combine(root, "baseq3", "q3config.cfg"), "the player's");
        Assert.True(Assert.Single((await SetupPlanner.PlanAsync(quake, root, [], new Probe())).Steps).AlreadyDone);

        foreach (var (to, pattern) in new[] { ("baseq3/q3config.cfg", ""), ("baseq3/pak0.pk3", "*.pk3"), ("../evil.cfg", "*.cfg") })
        {
            var (bad, badRoot) = Quake(to, pattern.Length > 0 ? [pattern] : []);
            var refused = await SetupPlanner.PlanAsync(bad, badRoot, [], new Probe());
            Assert.Contains(refused.Problems, p => p.StartsWith("Výchozí soubor:"));
            Assert.Empty(refused.Steps);
        }
    }

    [Fact]
    public void A_definition_without_firewall_serialises_as_before_so_signed_definitions_keep_their_hash()
    {
        var setup = new GameSetup { Requires = ["directx9"] };

        Assert.DoesNotContain("firewall", GameShareJson.Serialize(setup));
        Assert.Contains("\"firewall\":{\"launch\":false", GameShareJson.Serialize(setup with { Firewall = new FirewallSetup { Launch = false } }));
        Assert.False((setup with { Requires = [], Firewall = new FirewallSetup { Launch = false } }).IsEmpty); // kept when saved

        var definition = new GameDefinition { GameId = "cod2", Name = "Call of Duty 2", Setup = setup };
        Assert.DoesNotContain("needs", GameShareJson.Serialize(definition));
        Assert.Contains("\"needs\":[\"microphone\"]", GameShareJson.Serialize(definition with { Needs = [GameNeeds.Microphone] }));
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\EA Games")]                  // a whole vendor
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Game")]
    [InlineData(@"HKCR\exefile\shell")]
    [InlineData(@"HKLM\SOFTWARE\EA Games\..\Microsoft\x")]
    public async Task A_cleanup_key_that_is_not_a_games_own_is_refused(string key)
    {
        var (game, root) = Battlefield(new GameSetup { Registry = [new RegistryStep { File = "registry-import.reg", Cleanup = key }] });

        var plan = await SetupPlanner.PlanAsync(game, root, [], new Probe());

        Assert.Contains(plan.Problems, p => p.Contains("not one a game may delete"));
        Assert.DoesNotContain(plan.Steps, s => s.Kind == SetupStepKind.RegistryDelete);
    }

    [Fact]
    public async Task A_reg_file_that_writes_to_windows_own_keys_is_refused_whole()
    {
        var evil = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\EA Games\\Battlefield 2]\r\n\"A\"=\"1\"\r\n\r\n" +
                   "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"Evil\"=\"C:\\\\evil.exe\"\r\n";
        var (game, root) = Game("Evil", new GameSetup { Registry = [new RegistryStep { File = "evil.reg" }] }, files: [("evil.reg", Encoding.UTF8.GetBytes(evil))]);

        var plan = await SetupPlanner.PlanAsync(game, root, [], new Probe());

        Assert.Contains(plan.Problems, p => p.Contains("CurrentVersion\\Run"));
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task A_reg_file_changed_since_the_game_was_verified_is_not_imported()
    {
        var (game, root) = Battlefield(new GameSetup { Registry = [new RegistryStep { File = "registry-import.reg" }] });
        await File.AppendAllTextAsync(Path.Combine(root, "registry-import.reg"), "[HKEY_LOCAL_MACHINE\\SOFTWARE\\X\\Y]");

        var plan = await SetupPlanner.PlanAsync(game, root, [], new Probe());

        Assert.Contains(plan.Problems, p => p.Contains("Repair the game"));
        Assert.Empty(plan.Steps);
    }

    [Theory]
    [InlineData("../outside.reg", null, null, null)]
    [InlineData("registry-import.reg", "..\\..\\Windows", "BF2.exe", "{Documents}\\BF2")]
    [InlineData("registry-import.reg", "Battlefield 2-profile", "BF2.exe", "C:\\Windows\\BF2")]
    [InlineData("registry-import.reg", "Battlefield 2-profile", "BF2.exe", "{Documents}\\..\\..\\BF2")]
    [InlineData("registry-import.reg", "Battlefield 2-profile", "BF2.exe", "{Windows}\\BF2")]
    [InlineData("registry-import.reg", "Battlefield 2-profile", "registry-import.reg", "{Documents}\\BF2")] // compatibility for a non-program
    public async Task Files_folders_and_targets_outside_the_game_or_the_players_profile_are_refused(string reg, string? from, string? exe, string? to)
    {
        var (game, root) = Battlefield(new GameSetup
        {
            Registry = [new RegistryStep { File = reg }],
            Profile = from is null ? [] : [new ProfileStep { From = from, To = to! }],
            Compatibility = exe is null ? [] : [new CompatibilityStep { Executable = exe, Layers = "WINXPSP3" }],
        });

        var plan = await SetupPlanner.PlanAsync(game, root, [], new Probe());

        Assert.NotEmpty(plan.Problems);
    }

    [Theory]
    [InlineData("WINXPSP3", true)]
    [InlineData("~ RUNASADMIN WINXPSP3", true)]
    [InlineData("WINXPSP3\" /evil", false)]
    [InlineData("", false)]
    public async Task Compatibility_layers_are_plain_words(string layers, bool accepted)
    {
        var (game, root) = Battlefield(new GameSetup { Compatibility = [new CompatibilityStep { Executable = "BF2.exe", Layers = layers }] });

        var plan = await SetupPlanner.PlanAsync(game, root, [], new Probe());

        Assert.Equal(accepted, plan.Problems.Count == 0);
    }
}
