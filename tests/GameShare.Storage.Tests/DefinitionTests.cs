using GameShare.Protocol;
using GameShare.Tests;

namespace GameShare.Storage.Tests;

/// <summary>gameshare.json: read, written back, and fingerprinted so it can be signed.</summary>
public class DefinitionTests
{
    internal const string Battlefield = """
        {
          "gameId": "battlefield-2", "name": "Battlefield 2", "version": "lan-v2",
          "launch": [ { "name": "Hrát", "executable": "BF2.exe", "arguments": "+menu 1 +fullscreen 1" } ],
          "setup": {
            "requires": [ "directx9", "vcredist2005_x86" ],
            "registry": [ { "file": "registry-import.reg", "originalPath": "C:\\Games\\Battlefield 2",
                            "cleanup": "HKLM\\SOFTWARE\\WOW6432Node\\Electronic Arts\\EA Games\\Battlefield 2" } ],
            "compatibility": [ { "executable": "BF2.exe", "layers": "WINXPSP3" } ],
            "profile": [ { "from": "Battlefield 2-profile", "to": "{Documents}\\Battlefield 2" } ]
          }
        }
        """;

    private static async Task<GameDefinition> LoadAsync(string json)
    {
        var dir = TestGame.NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "gameshare.json"), json);
            return (await GameDefinitionFile.TryLoadAsync(dir))!;
        }
        finally { TestGame.DeleteQuietly(dir); }
    }

    [Fact]
    public async Task A_definition_made_from_the_old_installer_is_read_with_its_programs_and_setup()
    {
        var def = await LoadAsync(Battlefield);

        var play = Assert.Single(def.LaunchEntries());
        Assert.Equal(("Hrát", "BF2.exe", "+menu 1 +fullscreen 1"), (play.Name, play.Executable, play.Arguments));
        Assert.Equal(["directx9", "vcredist2005_x86"], def.Setup!.Requires);
        Assert.Equal(@"C:\Games\Battlefield 2", Assert.Single(def.Setup.Registry).OriginalPath);
        Assert.Equal("WINXPSP3", Assert.Single(def.Setup.Compatibility).Layers);
        Assert.Equal(@"{Documents}\Battlefield 2", Assert.Single(def.Setup.Profile).To);
        Assert.Equal(GameKind.Game, def.Kind);
    }

    [Fact]
    public async Task A_redist_package_lists_what_it_provides_and_how_to_tell_it_is_installed()
    {
        var def = await LoadAsync("""
            { "gameId": "redist", "name": "Redist", "kind": "redist",
              "provides": { "directx9": { "file": "directx/DXSETUP.exe", "args": "/silent", "installedIf": { "file": "{SysWOW64}\\d3dx9_43.dll" } } } }
            """);

        Assert.Equal(GameKind.Redist, def.Kind);
        Assert.Equal(@"{SysWOW64}\d3dx9_43.dll", def.Provides["directx9"].InstalledIf!.File);
    }

    [Fact]
    public async Task Written_back_the_definition_reads_the_same_and_keeps_its_fingerprint()
    {
        var def = await LoadAsync(Battlefield);
        var dir = TestGame.NewTempDir();
        try
        {
            await GameDefinitionFile.WriteAsync(dir, def);
            var text = await File.ReadAllTextAsync(Path.Combine(dir, "gameshare.json"));
            var again = await GameDefinitionFile.TryLoadAsync(dir);

            Assert.Contains("Hrát", text); // readable when opened by hand
            Assert.DoesNotContain("null", text);
            Assert.Equal(DefinitionHasher.Compute(def), DefinitionHasher.Compute(again!));
        }
        finally { TestGame.DeleteQuietly(dir); }
    }

    [Fact]
    public async Task Any_change_to_how_a_game_starts_or_is_prepared_changes_the_fingerprint()
    {
        var def = await LoadAsync(Battlefield);
        var hash = DefinitionHasher.Compute(def);

        Assert.NotEqual(hash, DefinitionHasher.Compute(def with { Launch = [def.Launch[0] with { RunAsAdmin = true }] }));
        Assert.NotEqual(hash, DefinitionHasher.Compute(def with { Setup = def.Setup! with { Requires = ["directx9"] } }));
        Assert.NotEqual(hash, DefinitionHasher.Compute(def with { Setup = def.Setup! with { Registry = [def.Setup.Registry[0] with { File = "other.reg" }] } }));
    }

    [Fact]
    public void The_order_of_provided_redistributables_does_not_matter()
    {
        var a = new RedistPackage { File = "a.exe" };
        var b = new RedistPackage { File = "b.exe" };
        var one = new GameDefinition { GameId = "r", Name = "R", Provides = new Dictionary<string, RedistPackage> { ["a"] = a, ["b"] = b } };
        var two = one with { Provides = new Dictionary<string, RedistPackage> { ["b"] = b, ["a"] = a } };

        Assert.True(DefinitionHasher.Same(one, two));
    }
}
