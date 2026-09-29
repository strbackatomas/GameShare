using System.IO.Compression;
using GameShare.Admin;
using GameShare.Protocol;
using GameShare.Torrent;

namespace GameShare.Storage.Tests;

/// <summary>The release commands of the admin tool, run the way scripts\package-release.ps1 runs them.</summary>
public class ReleaseCommandsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TestGame.NewTempDir();

    public ReleaseCommandsTests()
    {
        Write("GameShare.Agent.exe", new string('a', 50_000));
        Write("appsettings.json", "{ \"Agent\": {} }");
        Write("Client/GameShare.exe", new string('c', 70_000));
        Write("Client/Avalonia.dll", new string('d', 3_000));
    }

    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private string P(string name) => Path.Combine(_dir, name);
    private string Package => P("GameShare-Update-agent");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Package, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AdminCli.RunAsync(args, output, error, () => Now);
        return (code, output.ToString(), error.ToString());
    }

    private async Task<string> KeysAsync()
    {
        var result = await RunAsync("release-keygen", "--out", P("keys"));
        Assert.Equal(0, result.Code);
        return P("keys/release-private.key");
    }

    private string Zip(string name = "GameShare-Update-agent.zip")
    {
        var zip = P(name);
        ZipFile.CreateFromDirectory(Package, zip);
        return zip;
    }

    [Fact]
    public async Task A_signed_package_opens_with_the_public_key_and_describes_the_folder_and_the_zip()
    {
        var key = await KeysAsync();
        var zip = Zip();
        File.WriteAllText(P("notes.md"), "### Fixed\n- Things.\n");

        var signed = await RunAsync("release-sign", "--folder", Package, "--version", "0.5.0", "--flavor", "agent",
            "--zip", zip, "--notes-file", P("notes.md"), "--key", key, "--out", P("out/update-agent.json"));

        Assert.True(signed.Code == 0, signed.Err);
        var release = ReleaseSigning.Open(File.ReadAllBytes(P("out/update-agent.json")), File.ReadAllText(P("keys/release-public.key")).Trim());
        Assert.Equal("0.5.0", release.Version);
        Assert.Equal(AppFlavors.Agent, release.Flavor);
        Assert.Equal(["Client/Avalonia.dll", "Client/GameShare.exe", "GameShare.Agent.exe", "appsettings.json"], release.Manifest.Files.Select(f => f.Path));
        Assert.Equal("GameShare-Update-agent.zip", release.ZipName);
        Assert.Equal(await ManifestVerifier.HashFileAsync(zip), release.ZipSha256);
        Assert.Equal("### Fixed\n- Things.", release.Notes);

        var shown = await RunAsync("release-show", "--file", P("out/update-agent.json"), "--pub", P("keys/release-public.key"));
        Assert.Equal(0, shown.Code);
        Assert.Contains("GameShare 0.5.0 for the agent build", shown.Out);
    }

    [Fact]
    public async Task The_signed_torrent_is_the_one_a_PC_builds_from_the_same_files()
    {
        var key = await KeysAsync();
        await RunAsync("release-sign", "--folder", Package, "--version", "0.5.0", "--flavor", "agent", "--key", key, "--out", P("update-agent.json"));
        var release = ReleaseSigning.Open(File.ReadAllBytes(P("update-agent.json")), File.ReadAllText(P("keys/release-public.key")).Trim());

        var rebuilt = await TorrentBuilder.BuildAsync(Package);

        Assert.Equal(rebuilt.InfoHash, release.Manifest.TorrentInfoHash);
        Assert.Equal(rebuilt.TorrentBytes, release.Torrent);
        Assert.Null(release.ZipName);
    }

    [Fact]
    public async Task The_key_can_come_from_the_environment_the_way_the_build_server_hands_it_over()
    {
        var key = File.ReadAllText(await KeysAsync()).Trim();
        Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, key);
        try
        {
            var signed = await RunAsync("release-sign", "--folder", Package, "--version", "0.5.0", "--flavor", "lanparty", "--out", P("update-lanparty.json"));
            Assert.True(signed.Code == 0, signed.Err);
        }
        finally { Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, null); }

        var missing = await RunAsync("release-sign", "--folder", Package, "--version", "0.5.0", "--flavor", "lanparty", "--out", P("again.json"));
        Assert.Equal(1, missing.Code);
        Assert.Contains(ReleaseCommands.KeyVariable, missing.Err);
    }

    [Fact]
    public async Task A_zip_that_would_not_unpack_into_the_package_is_refused()
    {
        var key = await KeysAsync();
        var zip = P("nested.zip");
        ZipFile.CreateFromDirectory(Package, zip, CompressionLevel.Fastest, includeBaseDirectory: true);

        var signed = await RunAsync("release-sign", "--folder", Package, "--version", "0.5.0", "--flavor", "agent", "--zip", zip, "--key", key, "--out", P("u.json"));

        Assert.Equal(1, signed.Code);
        Assert.Contains("does not hold the same files", signed.Err);
        Assert.False(File.Exists(P("u.json")));
    }

    [Theory]
    [InlineData("0.5", "agent", "--version")]
    [InlineData("0.5.0", "portable", "--flavor")]
    public async Task Bad_arguments_are_refused_with_a_message(string version, string flavor, string expected)
    {
        var key = await KeysAsync();

        var signed = await RunAsync("release-sign", "--folder", Package, "--version", version, "--flavor", flavor, "--key", key, "--out", P("u.json"));

        Assert.Equal(1, signed.Code);
        Assert.Contains(expected, signed.Err);
    }

    [Fact]
    public async Task A_release_key_is_never_overwritten()
    {
        await KeysAsync();

        var again = await RunAsync("release-keygen", "--out", P("keys"));

        Assert.Equal(1, again.Code);
        Assert.Contains("already exists", again.Err);
    }

    [Fact]
    public async Task A_release_signed_with_another_key_is_shown_as_invalid()
    {
        var key = await KeysAsync();
        await RunAsync("release-sign", "--folder", Package, "--version", "0.5.0", "--flavor", "agent", "--key", key, "--out", P("u.json"));
        await RunAsync("release-keygen", "--out", P("other"));

        var shown = await RunAsync("release-show", "--file", P("u.json"), "--pub", P("other/release-public.key"));

        Assert.Equal(1, shown.Code);
        Assert.Contains("signed with key", shown.Err);
    }
}
