using GameShare.Protocol;

namespace GameShare.Storage.Tests;

/// <summary>A definition arrives from another PC. Only a program that is part of the game itself may be started.</summary>
public class LaunchRulesTests
{
    private static string Hash(char c) => new(c, 64);

    private static GameManifest Manifest(GameDefinition? definition, params string[] files)
    {
        var list = files.Select((f, i) => new ManifestFile(f, 10, Hash((char)('a' + i)))).ToList();
        return new GameManifest
        {
            GameId = "g", Name = "G", FolderName = "G", TotalSize = 10L * list.Count, ContentHash = ContentHasher.Compute(list),
            PieceLength = 1 << 20, Files = list, Definition = definition,
        };
    }

    private static GameDefinition Def(string? exe, string? args = null, string workingDirectory = ".") =>
        new() { GameId = "g", Name = "G", Executable = exe, Arguments = args, WorkingDirectory = workingDirectory };

    [Fact]
    public void A_program_that_is_part_of_the_game_is_started_with_its_arguments()
    {
        var plan = LaunchRules.Plan(Manifest(Def("Bin64/Game.exe", "-windowed"), "Bin64/Game.exe", "data.pak"), null, out var problem);

        Assert.Null(problem);
        Assert.Equal(new LaunchPlan("Bin64/Game.exe", "-windowed", "."), plan);
    }

    [Theory]
    [InlineData(@"Bin64\Game.exe")]   // the way it is written on Windows
    [InlineData("bin64/game.EXE")]    // Windows does not care about case, and neither does this
    [InlineData(" Bin64/Game.exe ")]
    public void The_spelling_does_not_matter_and_the_manifests_spelling_is_what_is_started(string written)
    {
        var plan = LaunchRules.Plan(Manifest(Def(written), "Bin64/Game.exe"), null, out var problem);

        Assert.Null(problem);
        Assert.Equal("Bin64/Game.exe", plan!.Executable);
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\evil.exe")]
    [InlineData("/etc/evil.exe")]
    [InlineData("Bin64/../../evil.exe")]
    [InlineData("Game.exe:stream")]
    [InlineData("CON.exe ")]
    public void A_path_that_leaves_the_game_folder_is_never_started(string executable)
    {
        var plan = LaunchRules.Plan(Manifest(Def(executable), "Game.exe"), null, out var problem);

        Assert.Null(plan);
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_program_that_is_not_one_of_the_games_files_is_never_started_even_if_it_exists_on_disk()
    {
        // A file dropped into the game folder later is not part of what was verified.
        var plan = LaunchRules.Plan(Manifest(Def("dropped.exe"), "Game.exe"), null, out var problem);

        Assert.Null(plan);
        Assert.Contains("not one of the files", problem);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("setup.bat")]
    [InlineData("game.dll")]
    [InlineData("Game")]
    public void Only_programs_are_started_not_other_files_of_the_game(string file)
    {
        var plan = LaunchRules.Plan(Manifest(Def(file), file), null, out var problem);

        Assert.Null(plan);
        Assert.Contains("not a program", problem);
    }

    [Fact]
    public void A_player_choice_replaces_the_definition_entirely()
    {
        var manifest = Manifest(Def("Game.exe", "-fromdefinition"), "Game.exe", "Launcher.exe");

        var plan = LaunchRules.Plan(manifest, new LauncherChoice("Launcher.exe", null), out var problem);

        Assert.Null(problem);
        Assert.Equal(new LaunchPlan("Launcher.exe", null, "."), plan);
    }

    [Fact]
    public void A_choice_that_is_not_valid_is_refused_with_the_reason_and_does_not_fall_back_to_the_definition()
    {
        var manifest = Manifest(Def("Game.exe"), "Game.exe");

        var plan = LaunchRules.Plan(manifest, new LauncherChoice("evil.exe", null), out var problem);

        Assert.Null(plan);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Nothing_configured_is_not_a_problem_it_just_means_there_is_nothing_to_start()
    {
        var plan = LaunchRules.Plan(Manifest(null, "Game.exe"), null, out var problem);

        Assert.Null(plan);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("-w\n-x")]
    [InlineData("-a \0")]
    public void Arguments_with_control_characters_are_refused(string arguments)
    {
        var plan = LaunchRules.Plan(Manifest(Def("Game.exe", arguments), "Game.exe"), null, out var problem);

        Assert.Null(plan);
        Assert.Contains("control characters", problem);
    }

    [Fact]
    public void Absurdly_long_arguments_are_refused()
    {
        var plan = LaunchRules.Plan(Manifest(Def("Game.exe", new string('a', LaunchRules.MaxArgumentsLength + 1)), "Game.exe"), null, out var problem);

        Assert.Null(plan);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("Bin64", "Bin64")]
    [InlineData(@"Bin64\Tools", "Bin64/Tools")]
    [InlineData("", ".")]
    public void The_working_directory_is_a_folder_inside_the_game(string written, string expected)
    {
        var plan = LaunchRules.Plan(Manifest(Def("Game.exe", workingDirectory: written), "Game.exe"), null, out _);

        Assert.Equal(expected, plan!.WorkingDirectory);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\Other")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    public void A_working_directory_outside_the_game_is_refused(string directory)
    {
        var plan = LaunchRules.Plan(Manifest(Def("Game.exe", workingDirectory: directory), "Game.exe"), null, out var problem);

        Assert.Null(plan);
        Assert.Contains("working directory", problem);
    }

    private static GameDefinition DefWith(params LaunchEntry[] entries) => new() { GameId = "g", Name = "G", Launch = entries };

    [Fact]
    public void A_definition_can_list_several_programs_and_each_is_planned_with_its_own_settings()
    {
        var manifest = Manifest(DefWith(
                new LaunchEntry { Executable = "System/UT2004.exe" },
                new LaunchEntry { Name = "Editor", Executable = @"System\UnrealEd.exe", WorkingDirectory = "System" },
                new LaunchEntry { Name = "Server", Executable = "Server.exe", Arguments = "-lan", RunAsAdmin = true }),
            "System/UT2004.exe", "System/UnrealEd.exe", "Server.exe");

        Assert.Equal(new LaunchPlan("System/UT2004.exe", null, "."), LaunchRules.Plan(manifest, null, 0, out _));
        Assert.Equal(new LaunchPlan("System/UnrealEd.exe", null, "System") { Name = "Editor" }, LaunchRules.Plan(manifest, null, 1, out _));
        Assert.Equal(new LaunchPlan("Server.exe", "-lan", ".") { Name = "Server", RunAsAdmin = true }, LaunchRules.Plan(manifest, null, 2, out _));
        Assert.Null(LaunchRules.Plan(manifest, null, 3, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Every_entry_is_checked_and_one_that_is_not_a_game_file_is_left_out()
    {
        var manifest = Manifest(DefWith(
                new LaunchEntry { Executable = "Game.exe" },
                new LaunchEntry { Name = "Evil", Executable = @"C:\Windows\System32\cmd.exe" },
                new LaunchEntry { Name = "Editor", Executable = "Editor.exe" }),
            "Game.exe", "Editor.exe");

        Assert.Null(LaunchRules.Plan(manifest, null, 1, out var problem));
        Assert.NotNull(problem);
        Assert.Equal([0, 2], LaunchRules.Entries(manifest, null).Select(e => e.Index));
    }

    [Fact]
    public void The_players_choice_replaces_only_the_game_itself_and_never_asks_for_admin_rights()
    {
        var manifest = Manifest(DefWith(
                new LaunchEntry { Executable = "Game.exe", RunAsAdmin = true },
                new LaunchEntry { Name = "Editor", Executable = "Editor.exe" }),
            "Game.exe", "Editor.exe", "Other.exe");
        var choice = new LauncherChoice("Other.exe", null);

        Assert.Equal(new LaunchPlan("Other.exe", null, "."), LaunchRules.Plan(manifest, choice, 0, out _));
        Assert.Equal("Editor.exe", LaunchRules.Plan(manifest, choice, 1, out _)!.Executable);
    }

    [Fact]
    public void The_old_single_executable_fields_still_work_and_launch_wins_when_both_are_there()
    {
        var old = Manifest(Def("Game.exe", "-x"), "Game.exe", "New.exe");
        var both = Manifest(Def("Game.exe") with { Launch = [new LaunchEntry { Executable = "New.exe" }] }, "Game.exe", "New.exe");

        Assert.Equal(new LaunchPlan("Game.exe", "-x", "."), Assert.Single(LaunchRules.Entries(old, null)).Plan);
        Assert.Equal("New.exe", Assert.Single(LaunchRules.Entries(both, null)).Plan.Executable);
    }

    [Theory]
    [InlineData("registry-import.reg", "registry-import.reg")]
    [InlineData(@"_redist\Setup.EXE", "_redist/setup.exe")]
    public void A_file_named_by_a_definition_is_found_as_the_manifest_spells_it(string written, string listed)
    {
        var file = LaunchRules.GameFile(Manifest(null, listed, "Game.exe"), written, out var problem);

        Assert.Null(problem);
        Assert.Equal(listed, file!.Path);
    }

    [Theory]
    [InlineData(@"..\evil.reg")]
    [InlineData(@"C:\evil.reg")]
    [InlineData("missing.reg")]
    [InlineData("")]
    public void A_file_outside_the_game_or_not_in_it_is_refused(string written)
    {
        Assert.Null(LaunchRules.GameFile(Manifest(null, "registry-import.reg"), written, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void The_candidates_are_the_programs_of_the_game_nearest_the_root_first()
    {
        var manifest = Manifest(null, "Bin64/Game.exe", "readme.txt", "Launcher.exe", "Bin64/Tools/editor.EXE", "data.pak", "Alpha.exe");

        Assert.Equal(["Alpha.exe", "Launcher.exe", "Bin64/Game.exe", "Bin64/Tools/editor.EXE"], LaunchRules.Candidates(manifest));
    }
}
