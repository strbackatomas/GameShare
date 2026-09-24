using System.Text;
using GameShare.AdminGui.ViewModels;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.AdminGui.Tests;

/// <summary>Editing a game's gameshare.json in the admin tool, on a real folder, and signing it together with the game.</summary>
public class DefinitionEditorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TestGame.NewTempDir();
    private readonly FakePickers _pickers = new();

    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private const string Reg = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\EA Games\\Battlefield 2]\r\n\"InstallDir\"=\"C:\\\\Games\\\\Battlefield 2\"\r\n";

    /// <summary>A game folder the way the old installer had them, next to a redistributables package.</summary>
    private string Battlefield()
    {
        var game = Path.Combine(_dir, "Hry", "Battlefield 2");
        Directory.CreateDirectory(Path.Combine(game, "Battlefield 2-profile"));
        Directory.CreateDirectory(Path.Combine(game, "System"));
        File.WriteAllBytes(Path.Combine(game, "BF2.exe"), [1]);
        File.WriteAllBytes(Path.Combine(game, "System", "Editor.exe"), [2]);
        File.WriteAllText(Path.Combine(game, "Battlefield 2-profile", "Profile.con"), "x");
        File.WriteAllBytes(Path.Combine(game, "registry-import.reg"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Reg)]);

        var redist = Path.Combine(_dir, "Hry", "_Redist");
        Directory.CreateDirectory(redist);
        File.WriteAllText(Path.Combine(redist, "gameshare.json"), """
            { "gameId": "redist", "name": "Redist", "kind": "redist",
              "provides": { "directx9": { "name": "DirectX 9", "file": "dx.exe" }, "dotnet40": { "file": "net.exe" } } }
            """);
        return game;
    }

    [Fact]
    public async Task A_folder_without_a_definition_starts_from_its_name_and_offers_only_what_is_in_it()
    {
        var editor = await DefinitionEditorViewModel.OpenAsync(Battlefield());

        Assert.Equal(("battlefield-2", "Battlefield 2"), (editor.GameId, editor.Name));
        Assert.Equal(["BF2.exe", "System/Editor.exe"], editor.Programs);
        Assert.Equal(["registry-import.reg"], editor.RegFiles);
        Assert.Contains("Battlefield 2-profile", editor.Folders);
        Assert.Equal(["directx9", "dotnet40"], editor.Requires.Select(r => r.Id)); // from the package next to the game
        Assert.Equal("BF2.exe", Assert.Single(editor.Launch).Executable);
    }

    [Fact]
    public async Task What_the_form_says_is_saved_as_gameshare_json_the_way_agents_read_it()
    {
        var folder = Battlefield();
        var editor = await DefinitionEditorViewModel.OpenAsync(folder);
        editor.Launch[0].Arguments = "+menu 1 +fullscreen 1";
        editor.AddLaunchCommand.Execute(null);
        editor.Launch[1].Executable = "System/Editor.exe";
        editor.Launch[1].Name = "Editor";
        editor.Requires.Single(r => r.Id == "directx9").IsChecked = true;
        editor.AddRegistryCommand.Execute(null);
        editor.Registry[0].Cleanup = @"HKLM\SOFTWARE\WOW6432Node\EA Games\Battlefield 2";
        editor.AddCompatibilityCommand.Execute(null);
        editor.AddProfileCommand.Execute(null);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(editor.Problems);
        var saved = (await GameDefinitionFile.TryLoadAsync(folder))!;
        Assert.Equal(["BF2.exe", "System/Editor.exe"], saved.Launch.Select(l => l.Executable));
        Assert.Equal("System", saved.Launch[1].WorkingDirectory); // a program starts in its own folder
        Assert.Equal(["directx9"], saved.Setup!.Requires);
        Assert.Equal(@"C:\Games\Battlefield 2", saved.Setup.Registry[0].OriginalPath);
        Assert.Equal("WINXPSP3", saved.Setup.Compatibility[0].Layers);
        Assert.Equal(("Battlefield 2-profile", @"{Documents}\Battlefield 2"), (saved.Setup.Profile[0].From, saved.Setup.Profile[0].To));
    }

    [Fact]
    public async Task A_definition_an_agent_would_refuse_is_not_saved_and_the_form_says_why()
    {
        var folder = Battlefield();
        var editor = await DefinitionEditorViewModel.OpenAsync(folder);
        editor.AddRegistryCommand.Execute(null);
        editor.Registry[0].Cleanup = @"HKLM\SOFTWARE\Microsoft";
        editor.AddProfileCommand.Execute(null);
        editor.Profile[0].To = @"C:\Windows\x";
        editor.GameId = "Bad Id";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(3, editor.Problems.Count);
        Assert.False(File.Exists(Path.Combine(folder, "gameshare.json")));
    }

    [Fact]
    public async Task What_the_form_does_not_show_survives_a_save()
    {
        var folder = Battlefield();
        await File.WriteAllTextAsync(Path.Combine(folder, "gameshare.json"),
            """{ "gameId": "bf2", "name": "Battlefield 2", "volatile": ["mods/**/Settings/*.con"], "icon": "BF2.exe", "executable": "BF2.exe", "arguments": "-x" }""");
        var editor = await DefinitionEditorViewModel.OpenAsync(folder);
        Assert.Equal("-x", editor.Launch[0].Arguments); // the old single-program fields become the first entry

        await editor.SaveCommand.ExecuteAsync(null);

        var saved = (await GameDefinitionFile.TryLoadAsync(folder))!;
        Assert.Equal(["mods/**/Settings/*.con"], saved.Volatile);
        Assert.Equal("BF2.exe", saved.Icon);
        Assert.Null(saved.Executable);
        Assert.Equal("-x", Assert.Single(saved.Launch).Arguments);
    }

    [Fact]
    public async Task Edited_scanned_and_added_the_definition_is_signed_with_the_game()
    {
        var folder = Battlefield();
        var vm = new MainViewModel(_pickers, _pickers, () => Now, Path.Combine(_dir, "settings.json"));
        _pickers.NextFolder = Path.Combine(_dir, "keys");
        await vm.GenerateKeysCommand.ExecuteAsync(null);
        vm.ListPath = Path.Combine(_dir, "trust.json");
        await vm.LoadListCommand.ExecuteAsync(null);

        vm.GameFolder = folder;
        await vm.EditDefinitionCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        vm.Editor!.Launch[0].RunAsAdmin = true;
        await vm.Editor.SaveCommand.ExecuteAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Contains("podepíše", vm.ScannedDefinition);
        await vm.AddScannedGameCommand.ExecuteAsync(null);

        var signed = Assert.Single(TrustWorkflow.LoadOrStartList(vm.ListPath, vm.PublicKey!, Now).Games);
        Assert.Equal(DefinitionHasher.Compute((await GameDefinitionFile.TryLoadAsync(folder))!), signed.DefinitionHash);
        Assert.Contains("Definice podepsána", vm.VerifiedGames.Single().DefinitionText);
        Assert.False(vm.IsEditing);
    }
}
