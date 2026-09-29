using System.IO.Compression;
using System.Net.Http.Json;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;

namespace GameShare.Agent.Tests;

/// <summary>
/// The agent finding, downloading and checking a newer GameShare from a configured place, here a folder as on a LAN party's share.
/// Signed with a key made for the test, which the agent is told to trust instead of the one built into it.
/// </summary>
public sealed class AppUpdateTests : IAsyncDisposable
{
    private readonly string _source = TestGame.NewTempDir();
    private readonly TrustKeyPair _key = TrustSigning.GenerateKeyPair();
    private readonly List<TestAgent> _agents = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var a in _agents) await a.DisposeAsync();
        TestGame.DeleteQuietly(_source);
    }

    /// <summary>Publishes a release into the source folder the way package-release.ps1 does: package folder, zip, signed description.</summary>
    /// <param name="spoilZip">Changes the zip after it was hashed and signed, before it is published.</param>
    /// <param name="zipExtra">An entry that is put into the zip besides the package, before it is hashed and signed.</param>
    private async Task<AppRelease> PublishAsync(string version = "0.5.0", TrustKeyPair? key = null, bool spoilZip = false, string? zipExtra = null)
    {
        var package = Path.Combine(_source, "stage", "GameShare-Update-agent");
        TestGame.DeleteQuietly(package);
        Directory.CreateDirectory(Path.Combine(package, "Client"));
        File.WriteAllText(Path.Combine(package, "GameShare.Agent.exe"), $"agent {version} " + new string('a', 200_000));
        File.WriteAllText(Path.Combine(package, "Client", "GameShare.exe"), $"client {version} " + new string('c', 300_000));

        var scan = await ContentScanner.ScanAsync(package);
        var torrent = TorrentBuilder.Build(scan);
        var manifest = ManifestBuilder.Build(scan, torrentInfoHash: torrent.InfoHash) with { GameId = "gameshare", Name = "GameShare", Version = version };

        var zip = Path.Combine(_source, "GameShare-Update-agent.zip");
        File.Delete(zip);
        ZipFile.CreateFromDirectory(package, zip);
        if (zipExtra is not null)
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry(zipExtra).Open()))
                writer.Write("not part of the package");

        var release = new AppRelease(version, AppFlavors.Agent, DateTimeOffset.UtcNow, "Novinky.", manifest, torrent.TorrentBytes,
            Path.GetFileName(zip), await ManifestVerifier.HashFileAsync(zip));
        File.WriteAllBytes(Path.Combine(_source, AppRelease.FileName(AppFlavors.Agent)), ReleaseSigning.Serialize(ReleaseSigning.Sign(release, (key ?? _key).PrivateKey)));

        if (spoilZip)
            using (var file = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite)) { file.Position = file.Length / 2; file.WriteByte(0x42); }
        return release;
    }

    private async Task<TestAgent> StartAsync(string running = "0.4.3", string? flavor = AppFlavors.Agent, string? existingDir = null)
    {
        var agent = await TestAgent.StartAsync("updater", TestAgent.DiscoveryPort(), existingDir: existingDir, tweak: o =>
        {
            o.UpdateSource = _source;
            o.UpdatePublicKey = _key.PublicKey;
            o.UpdateFlavor = flavor;
            o.RunningVersion = running;
            o.UpdateCheckInterval = TimeSpan.FromHours(1);
        });
        _agents.Add(agent);
        return agent;
    }

    private static async Task<AppUpdateStatusDto> WaitForAsync(TestAgent agent, Func<AppUpdateStatusDto, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await agent.GetAsync<AppUpdateStatusDto>("/api/app-update");
            if (condition(status)) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The update never got there, it is: {status}");
            await Task.Delay(100);
        }
    }

    private static string VersionDir(TestAgent agent, string version) => Path.Combine(agent.Dir, "data", AppUpdateService.FolderName, version);

    [Fact]
    public async Task A_newer_release_is_found_downloaded_and_checked_and_waits_to_be_applied()
    {
        var release = await PublishAsync("0.5.0");
        var agent = await StartAsync(running: "0.4.3");

        var status = await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);

        Assert.Equal("0.5.0", status.Version);
        Assert.Equal("Novinky.", status.Notes);
        Assert.Equal("0.4.3", status.CurrentVersion);
        Assert.Null(status.Error);
        var package = Path.Combine(VersionDir(agent, "0.5.0"), release.Manifest.FolderName);
        Assert.True((await ManifestVerifier.VerifyAsync(release.Manifest, package, VerifyMode.Full)).IsValid);
        Assert.StartsWith("client 0.5.0", File.ReadAllText(Path.Combine(package, "Client", "GameShare.exe")));
    }

    [Fact]
    public async Task A_version_downloaded_before_a_restart_is_ready_right_after_it()
    {
        await PublishAsync("0.5.0");
        var agent = await StartAsync();
        await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);
        await agent.StopAsync();
        _agents.Remove(agent);
        File.Delete(Path.Combine(_source, AppRelease.FileName(AppFlavors.Agent))); // nothing to find any more, it must come from disk

        var again = await StartAsync(existingDir: agent.Dir);

        var status = await WaitForAsync(again, s => s.State == AppUpdateState.Ready);
        Assert.Equal("0.5.0", status.Version);
    }

    [Fact]
    public async Task A_zip_changed_after_signing_is_never_unpacked()
    {
        await PublishAsync("0.5.0", spoilZip: true);
        var agent = await StartAsync();

        var status = await WaitForAsync(agent, s => s.Error is not null);

        Assert.Equal(AppUpdateState.Available, status.State);
        Assert.Contains("not the file that was signed", status.Error);
        Assert.False(Directory.Exists(VersionDir(agent, "0.5.0")));
    }

    [Theory]
    [InlineData("../../escaped.txt")]
    [InlineData("Client/extra.dll")]
    public async Task A_signed_zip_with_a_file_that_is_not_in_the_package_is_refused_as_a_whole(string extra)
    {
        await PublishAsync("0.5.0", zipExtra: extra);
        var agent = await StartAsync();

        var status = await WaitForAsync(agent, s => s.Error is not null);

        Assert.Contains("not part of the signed package", status.Error);
        Assert.False(Directory.Exists(VersionDir(agent, "0.5.0")));
        Assert.False(File.Exists(Path.Combine(agent.Dir, "data", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(agent.Dir, "escaped.txt")));
    }

    [Fact]
    public async Task A_release_signed_with_another_key_is_not_taken()
    {
        await PublishAsync("0.5.0", key: TrustSigning.GenerateKeyPair());
        var agent = await StartAsync();

        var status = await WaitForAsync(agent, s => s.LastChecked is not null);

        Assert.Equal(AppUpdateState.UpToDate, status.State);
        Assert.Contains("signed with key", status.Error);
        Assert.Null(status.Version);
    }

    [Theory]
    [InlineData("0.5.0")]
    [InlineData("0.6.1")]
    public async Task A_release_that_is_not_newer_than_what_runs_is_not_downloaded(string running)
    {
        await PublishAsync("0.5.0");
        var agent = await StartAsync(running: running);

        var status = await WaitForAsync(agent, s => s.LastChecked is not null);

        Assert.Equal(AppUpdateState.UpToDate, status.State);
        Assert.Null(status.Error);
        Assert.False(Directory.Exists(VersionDir(agent, "0.5.0")));
    }

    [Fact]
    public async Task A_newer_release_published_later_is_found_when_asked_to_check()
    {
        var agent = await StartAsync();
        var nothing = await WaitForAsync(agent, s => s.LastChecked is not null);
        Assert.Equal(AppUpdateState.UpToDate, nothing.State);
        Assert.NotNull(nothing.Error); // nothing published yet: the folder has no description

        await PublishAsync("0.5.0");
        var response = await agent.SendAsync(HttpMethod.Post, "/api/app-update/check");
        response.EnsureSuccessStatusCode();

        var status = await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);
        Assert.Equal("0.5.0", status.Version);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task A_build_without_a_flavor_does_not_update_itself_and_says_why()
    {
        await PublishAsync("0.5.0");
        var agent = await StartAsync(flavor: null);

        var status = await agent.GetAsync<AppUpdateStatusDto>("/api/app-update");

        Assert.Equal(AppUpdateState.Disabled, status.State);
        Assert.Contains("publish.ps1", status.DisabledReason);
        Assert.False(Directory.Exists(Path.Combine(agent.Dir, "data", AppUpdateService.FolderName)));
    }
}
