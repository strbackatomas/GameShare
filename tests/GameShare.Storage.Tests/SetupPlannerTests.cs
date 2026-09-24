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
    public async Task The_setup_hash_changes_with_the_setup_and_the_folder_but_not_with_anything_else()
    {
        var setup = new GameSetup { Requires = ["directx9"] };
        var (game, root) = Battlefield(setup);
        var hash = (await SetupPlanner.PlanAsync(game, root, [Redist()], new Probe())).SetupHash;

        Assert.Equal(hash, SetupPlanner.SetupHash(game.Definition! with { Launch = [new LaunchEntry { Executable = "BF2.exe" }] }, root));
        Assert.NotEqual(hash, SetupPlanner.SetupHash(game.Definition!, Path.Combine(_dir, "elsewhere")));
        Assert.NotEqual(hash, SetupPlanner.SetupHash(game.Definition! with { Setup = setup with { Requires = ["directx9", "dotnet40"] } }, root));
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
