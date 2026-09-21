using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Storage.Tests;

public class ManifestTests
{
    [Fact]
    public async Task Manifest_lists_every_file_and_its_hash_is_reproducible()
    {
        using var a = new TestGame();
        using var b = new TestGame(); // same content, different parent directory

        var (ma, _) = await ManifestBuilder.ScanAndBuildAsync(a.GameDir);
        var (mb, _) = await ManifestBuilder.ScanAndBuildAsync(b.GameDir);

        Assert.Equal(TestGame.FileCount, ma.Files.Count);
        Assert.Equal(ma.Files.Sum(f => f.Size), ma.TotalSize);
        Assert.Equal(ma.ContentHash, mb.ContentHash);
        Assert.Equal("testgame", ma.GameId);
        Assert.Equal("TestGame", ma.FolderName);
        Assert.Empty(ManifestValidator.Validate(ma));
    }

    [Fact]
    public async Task One_changed_byte_changes_the_content_hash()
    {
        using var a = new TestGame();
        using var b = new TestGame();
        TestGame.CorruptOneByte(Path.Combine(b.GameDir, "content", "big.pak"));

        var (ma, _) = await ManifestBuilder.ScanAndBuildAsync(a.GameDir);
        var (mb, _) = await ManifestBuilder.ScanAndBuildAsync(b.GameDir);

        Assert.NotEqual(ma.ContentHash, mb.ContentHash);
    }

    [Fact]
    public async Task Definition_file_is_loaded_but_does_not_affect_content_identity()
    {
        using var plain = new TestGame();
        using var withDef = new TestGame();
        await File.WriteAllTextAsync(Path.Combine(withDef.GameDir, "gameshare.json"),
            """{ "gameId": "test-game", "name": "Test Game", "version": "0.38", "executable": "Game.exe" }""");

        var (mp, _) = await ManifestBuilder.ScanAndBuildAsync(plain.GameDir);
        var (md, _) = await ManifestBuilder.ScanAndBuildAsync(withDef.GameDir);

        Assert.Equal(mp.ContentHash, md.ContentHash);
        Assert.Equal(TestGame.FileCount, md.Files.Count); // gameshare.json is not content
        Assert.Equal("test-game", md.GameId);
        Assert.Equal("0.38", md.Version);
        Assert.Equal("Game.exe", md.Definition?.Executable);
        Assert.Equal(".", md.Definition?.WorkingDirectory);
    }

    [Fact]
    public async Task Malformed_definition_file_error_names_the_file()
    {
        using var g = new TestGame();
        var path = Path.Combine(g.GameDir, "gameshare.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => GameDefinitionFile.TryLoadAsync(g.GameDir));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public async Task Definition_with_bad_game_id_is_rejected()
    {
        using var g = new TestGame();
        await File.WriteAllTextAsync(Path.Combine(g.GameDir, "gameshare.json"), """{ "gameId": "Bad Id!", "name": "X" }""");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => GameDefinitionFile.TryLoadAsync(g.GameDir));
        Assert.Contains("Bad Id!", ex.Message);
    }

    [Fact]
    public async Task Manifest_survives_a_json_round_trip()
    {
        using var g = new TestGame();
        var (m, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);

        var back = GameShareJson.Deserialize<GameManifest>(GameShareJson.Serialize(m));

        Assert.Equal(m.ContentHash, back.ContentHash);
        Assert.Equal(m.Files, back.Files);
        Assert.Empty(ManifestValidator.Validate(back));
    }

    [Theory]
    [InlineData("Game.exe")]
    [InlineData("a/b/c.dat")]
    [InlineData("file with spaces.txt")]
    public void Safe_paths_are_accepted(string path) => Assert.True(ManifestValidator.IsSafeRelativePath(path));

    [Theory]
    [InlineData("")]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("a\\b.txt")]
    [InlineData("a//b.txt")]
    [InlineData("./a.txt")]
    [InlineData("trailingdot.")]
    [InlineData("a/b ")]
    public void Unsafe_paths_are_rejected(string path) => Assert.False(ManifestValidator.IsSafeRelativePath(path));

    [Fact]
    public async Task Validator_reports_tampered_manifests()
    {
        using var g = new TestGame();
        var (good, _) = await ManifestBuilder.ScanAndBuildAsync(g.GameDir);

        var traversal = good with { Files = [.. good.Files, new ManifestFile("../evil.dll", 1, new string('a', 64))], TotalSize = good.TotalSize + 1 };
        var wrongTotal = good with { TotalSize = good.TotalSize + 5 };
        var forgedHash = good with { ContentHash = new string('0', 64) };
        var dupe = good with { Files = [.. good.Files, good.Files[0] with { Path = good.Files[0].Path.ToUpperInvariant() }] };
        var badFolder = good with { FolderName = "..\\x" };

        Assert.Contains(ManifestValidator.Validate(traversal), e => e.Contains("Unsafe file path"));
        Assert.Contains(ManifestValidator.Validate(wrongTotal), e => e.Contains("TotalSize"));
        Assert.Contains(ManifestValidator.Validate(forgedHash), e => e.Contains("ContentHash does not match"));
        Assert.Contains(ManifestValidator.Validate(dupe), e => e.Contains("Duplicate"));
        Assert.Contains(ManifestValidator.Validate(badFolder), e => e.Contains("FolderName"));
    }
}
