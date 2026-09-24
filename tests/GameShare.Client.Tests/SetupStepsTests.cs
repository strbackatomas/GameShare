using System.Security.Cryptography;
using GameShare.Client.Setup;
using GameShare.Protocol;
using Microsoft.Win32;

namespace GameShare.Client.Tests;

/// <summary>
/// The steps the client runs itself, for real, on this PC: the player's registry (under a test key), compatibility mode, a profile
/// folder, and an "installer" (cmd.exe with an exit code). The machine's steps are the same code, run elevated.
/// </summary>
public class SetupStepsTests : IDisposable
{
    private readonly string _id = "GameShareTests-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gameshare-tests", Guid.NewGuid().ToString("N"));
    private string TestKey => @"Software\" + _id;
    private string ProfileTarget => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _id);
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public SetupStepsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestKey, throwOnMissingSubKey: false);
        using (var layers = Registry.CurrentUser.OpenSubKey(LayersKey, writable: true))
            layers?.DeleteValue(Path.Combine(_dir, "Game.exe"), throwOnMissingValue: false);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        try { Directory.Delete(ProfileTarget, true); } catch (DirectoryNotFoundException) { }
    }

    private static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    [Fact]
    public async Task The_players_registry_part_is_imported_and_a_game_key_can_be_deleted()
    {
        var import = new SetupStepDto(SetupStepKind.RegistryImport, "reg", false)
        {
            Content = $"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\{TestKey}\\Game]\r\n\"InstallDir\"=\"D:\\\\Hry\\\\Hráč\"\r\n\"Width\"=dword:00000280\r\n",
        };

        var imported = await SetupSteps.RunAsync([import]);

        Assert.True(Assert.Single(imported).Ok, imported[0].Message);
        using (var key = Registry.CurrentUser.OpenSubKey(TestKey + @"\Game"))
        {
            Assert.Equal(@"D:\Hry\Hráč", key!.GetValue("InstallDir")); // non-ASCII survives, the file is written as UTF-16
            Assert.Equal(640, key.GetValue("Width"));
        }

        var deleted = await SetupSteps.RunAsync([new SetupStepDto(SetupStepKind.RegistryDelete, "del", false) { Target = @"HKCU\" + TestKey + @"\Game" }]);

        Assert.True(Assert.Single(deleted).Ok, deleted[0].Message);
        Assert.Null(Registry.CurrentUser.OpenSubKey(TestKey + @"\Game"));
    }

    [Fact]
    public async Task A_compatibility_mode_is_set_for_the_player_the_way_windows_writes_it()
    {
        var exe = Path.Combine(_dir, "Game.exe");

        var results = await SetupSteps.RunAsync([new SetupStepDto(SetupStepKind.Compatibility, "compat", false) { File = exe, Arguments = "WINXPSP3" }]);

        Assert.True(Assert.Single(results).Ok);
        using var layers = Registry.CurrentUser.OpenSubKey(LayersKey);
        Assert.Equal("~ WINXPSP3", layers!.GetValue(exe));
    }

    [Fact]
    public async Task A_profile_is_copied_once_and_an_existing_one_with_saves_is_left_alone()
    {
        var source = Path.Combine(_dir, "Game-profile");
        Directory.CreateDirectory(Path.Combine(source, "Profiles", "Default"));
        await File.WriteAllTextAsync(Path.Combine(source, "Profiles", "Default", "Profile.con"), "shipped");
        var step = new SetupStepDto(SetupStepKind.Profile, "profile", false) { File = source, Target = "{LocalAppData}\\" + _id };

        Assert.True((await SetupSteps.RunAsync([step]))[0].Ok);
        var copied = Path.Combine(ProfileTarget, "Profiles", "Default", "Profile.con");
        Assert.Equal("shipped", await File.ReadAllTextAsync(copied));

        await File.WriteAllTextAsync(copied, "my saves");
        var again = await SetupSteps.RunAsync([step]);
        Assert.True(again[0].Ok);
        Assert.Contains("existuje", again[0].Message);
        Assert.Equal("my saves", await File.ReadAllTextAsync(copied));
    }

    [Theory]
    [InlineData(@"{LocalAppData}\..\..\Windows")]
    [InlineData(@"C:\Windows\Temp\x")]
    public async Task A_profile_target_outside_the_players_folders_is_refused(string target)
    {
        var results = await SetupSteps.RunAsync([new SetupStepDto(SetupStepKind.Profile, "p", false) { File = _dir, Target = target }]);

        Assert.False(results[0].Ok);
    }

    [Theory]
    [InlineData("/c exit 0", true, null)]
    [InlineData("/c exit 3010", true, "restart")]
    [InlineData("/c exit 5", false, "chybou 5")]
    public async Task An_installer_counts_by_its_exit_code(string args, bool ok, string? note)
    {
        var results = await SetupSteps.RunAsync([new SetupStepDto(SetupStepKind.Redist, "cmd", true) { File = Cmd, FileHash = Sha(Cmd), Arguments = args }]);

        Assert.Equal(ok, results[0].Ok);
        if (note is not null) Assert.Contains(note, results[0].Message);
    }

    [Fact]
    public async Task An_installer_that_is_not_the_verified_file_is_never_started()
    {
        var marker = Path.Combine(_dir, "ran.txt");
        var results = await SetupSteps.RunAsync([new SetupStepDto(SetupStepKind.Redist, "cmd", true)
        {
            File = Cmd, FileHash = new string('0', 64), Arguments = $"/c echo x > \"{marker}\"",
        }]);

        Assert.False(results[0].Ok);
        Assert.Contains("změnil", results[0].Message);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Steps_already_done_are_skipped()
    {
        var results = await SetupSteps.RunAsync([new SetupStepDto(SetupStepKind.Redist, "cmd", true) { File = Cmd, FileHash = "wrong", AlreadyDone = true }]);

        Assert.Empty(results);
    }

    [Fact]
    public void The_elevated_entry_point_ignores_a_normal_start() => Assert.Null(SetupHost.TryRun(["--minimized"]));
}
