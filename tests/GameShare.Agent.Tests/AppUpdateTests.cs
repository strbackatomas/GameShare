using System.IO.Compression;
using System.Net.Http.Json;
using GameShare.Protocol;
using GameShare.Storage;
using GameShare.Torrent;
using Microsoft.Extensions.DependencyInjection;

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

    private async Task<TestAgent> StartAsync(
        string running = "0.4.3", string? flavor = AppFlavors.Agent, string? existingDir = null, int? discoveryPort = null,
        bool withSource = true, TimeSpan? internetDelay = null, string? installDir = null, IAppUpdateApplier? applier = null)
    {
        var agent = await TestAgent.StartAsync("updater", discoveryPort ?? TestAgent.DiscoveryPort(), existingDir: existingDir,
            services: applier is null ? null : s => s.AddSingleton(applier), tweak: o =>
        {
            o.UpdateSource = withSource ? _source : "";
            o.UpdatePublicKey = _key.PublicKey;
            o.UpdateFlavor = flavor;
            o.RunningVersion = running;
            o.UpdateCheckInterval = TimeSpan.FromHours(1);
            o.UpdateLanPollInterval = TimeSpan.FromMilliseconds(300);
            o.UpdateInternetDelay = internetDelay ?? TimeSpan.Zero;
            o.UpdateLanStall = TimeSpan.FromSeconds(20);
            o.UpdateInstallDir = installDir ?? Path.Combine(_source, "no-install"); // not the test's own folder
        });
        _agents.Add(agent);
        return agent;
    }

    private static async Task<AppUpdateStatusDto> WaitForAsync(TestAgent agent, Func<AppUpdateStatusDto, bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
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

    // ---- applying ----

    /// <summary>Records what it was asked to do instead of replacing anything.</summary>
    private sealed class FakeApplier : IAppUpdateApplier
    {
        public string? Refuse { get; set; }
        public Exception? Fail { get; set; }
        public List<AppUpdateApplyContext> Applied { get; } = [];

        public string? CannotApplyReason(AppUpdateService.StagedRelease staged) => Refuse;

        public Task ApplyAsync(AppUpdateApplyContext context, CancellationToken ct)
        {
            if (Fail is not null) throw Fail;
            Applied.Add(context);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task An_agent_that_is_not_the_installed_service_says_it_cannot_apply_and_why()
    {
        await PublishAsync("0.5.0");
        var agent = await StartAsync();

        var status = await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);

        Assert.False(status.CanApply);
        Assert.Contains("služba Windows", status.CannotApplyReason);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Post, "/api/app-update/apply")).StatusCode);
    }

    [Fact]
    public async Task Applying_hands_the_checked_package_to_the_builds_applier()
    {
        await PublishAsync("0.5.0");
        var applier = new FakeApplier();
        var agent = await StartAsync(applier: applier);
        var ready = await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);
        Assert.True(ready.CanApply);

        var response = await agent.SendAsync(HttpMethod.Post, "/api/app-update/apply");

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var context = Assert.Single(applier.Applied);
        Assert.Equal("0.5.0", context.Staged.Release.Version);
        Assert.Equal(Path.Combine(VersionDir(agent, "0.5.0"), "GameShare-Update-agent"), context.Staged.PackageDir);
        Assert.Null(context.PreviousFiles); // installed by hand, its package is not here
        Assert.Equal(AppUpdateState.Applying, (await agent.GetAsync<AppUpdateStatusDto>("/api/app-update")).State);
    }

    [Fact]
    public async Task A_package_changed_on_disk_after_it_was_checked_is_never_applied_and_is_fetched_again()
    {
        await PublishAsync("0.5.0");
        var applier = new FakeApplier();
        var agent = await StartAsync(applier: applier);
        await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);
        File.AppendAllText(Path.Combine(VersionDir(agent, "0.5.0"), "GameShare-Update-agent", "GameShare.Agent.exe"), "planted");

        var response = await agent.SendAsync(HttpMethod.Post, "/api/app-update/apply");

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(applier.Applied);
        Assert.False(Directory.Exists(VersionDir(agent, "0.5.0")));
        var status = await agent.GetAsync<AppUpdateStatusDto>("/api/app-update");
        Assert.NotEqual(AppUpdateState.Ready, status.State);
        Assert.Contains("změnily", status.Error);
    }

    [Fact]
    public async Task A_failure_of_the_applier_leaves_the_version_ready_to_try_again_with_the_reason()
    {
        await PublishAsync("0.5.0");
        var applier = new FakeApplier { Fail = new IOException("disk full") };
        var agent = await StartAsync(applier: applier);
        await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);

        await agent.SendAsync(HttpMethod.Post, "/api/app-update/apply");

        var status = await agent.GetAsync<AppUpdateStatusDto>("/api/app-update");
        Assert.Equal(AppUpdateState.Ready, status.State);
        Assert.True(status.CanApply);
        Assert.Equal("disk full", status.Error);
    }

    [Fact]
    public async Task The_outcome_of_the_last_update_is_shown_by_the_agent_that_starts_next()
    {
        var dir = TestGame.NewTempDir();
        var updates = Path.Combine(dir, "data", AppUpdateService.FolderName);
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, AppUpdateResult.FileName),
            GameShareJson.Serialize(new AppUpdateResult("0.5.0", false, "Verze 0.5.0 se nerozběhla, vrátila se předchozí.", DateTimeOffset.UtcNow)));

        var agent = await StartAsync(existingDir: dir);

        var status = await WaitForAsync(agent, s => s.LastChecked is not null);
        Assert.Contains("nerozběhla", status.Error);
        Assert.False(File.Exists(Path.Combine(updates, AppUpdateResult.FileName)));
    }

    [Fact]
    public async Task After_an_update_the_old_files_are_named_when_the_next_one_is_applied()
    {
        await PublishAsync("0.5.0");
        var first = await StartAsync(running: "0.4.3");
        await WaitForAsync(first, s => s.State == AppUpdateState.Ready);
        await first.StopAsync();
        _agents.Remove(first);
        await PublishAsync("0.6.0");
        var applier = new FakeApplier();

        var after = await StartAsync(running: "0.5.0", existingDir: first.Dir, applier: applier); // 0.5.0 was applied, 0.6.0 is out
        await WaitForAsync(after, s => s.State == AppUpdateState.Ready && s.Version == "0.6.0");
        await after.SendAsync(HttpMethod.Post, "/api/app-update/apply");

        var context = Assert.Single(applier.Applied);
        Assert.Equal(["Client/GameShare.exe", "GameShare.Agent.exe"], context.PreviousFiles);
    }

    // ---- the LAN ----

    [Fact]
    public async Task A_PC_without_internet_takes_the_new_version_from_a_PC_on_the_LAN_that_has_it()
    {
        var release = await PublishAsync("0.5.0");
        int port = TestAgent.DiscoveryPort();
        var online = await StartAsync(discoveryPort: port);
        await WaitForAsync(online, s => s.State == AppUpdateState.Ready);

        var offline = await StartAsync(discoveryPort: port, withSource: false);

        var status = await WaitForAsync(offline, s => s.State == AppUpdateState.Ready, seconds: 60);
        Assert.Equal("0.5.0", status.Version);
        Assert.Equal(AppUpdateService.LanSource, status.Source);
        var package = Path.Combine(VersionDir(offline, "0.5.0"), release.Manifest.FolderName);
        Assert.True((await ManifestVerifier.VerifyAsync(release.Manifest, package, VerifyMode.Full)).IsValid);
    }

    [Fact]
    public async Task A_PC_that_found_it_on_the_internet_waits_and_takes_it_from_the_LAN_instead()
    {
        await PublishAsync("0.5.0");
        int port = TestAgent.DiscoveryPort();
        var first = await StartAsync(discoveryPort: port);
        await WaitForAsync(first, s => s.State == AppUpdateState.Ready);
        // From now on the internet would fail: only the description is there, not the zip.
        File.Delete(Path.Combine(_source, "GameShare-Update-agent.zip"));

        var second = await StartAsync(discoveryPort: port, internetDelay: TimeSpan.FromMinutes(5));

        var status = await WaitForAsync(second, s => s.State == AppUpdateState.Ready, seconds: 60);
        Assert.Equal(AppUpdateService.LanSource, status.Source);
    }

    [Fact]
    public async Task Files_the_running_program_already_has_are_copied_and_not_downloaded()
    {
        await PublishAsync("0.5.0");
        File.Delete(Path.Combine(_source, "GameShare-Update-agent.zip")); // nothing to download from, and nobody on the LAN
        var installed = Path.Combine(_source, "stage", "GameShare-Update-agent"); // the same files as the package

        var agent = await StartAsync(installDir: installed);

        var status = await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);
        Assert.Equal("0.5.0", status.Version);
    }

    [Fact]
    public async Task The_peer_API_offers_what_is_ready_with_its_signed_description_and_nothing_while_sharing_is_off()
    {
        await PublishAsync("0.5.0");
        var agent = await StartAsync();
        await WaitForAsync(agent, s => s.State == AppUpdateState.Ready);

        var offers = await agent.PeerApi.GetFromJsonAsync<List<AppUpdateOfferDto>>("/peer/app-update", TestAgent.Json);
        Assert.Equal([new AppUpdateOfferDto(AppFlavors.Agent, "0.5.0")], offers);
        var document = await agent.PeerApi.GetByteArrayAsync("/peer/app-update/agent/0.5.0");
        Assert.Equal("0.5.0", ReleaseSigning.Open(document, _key.PublicKey).Version);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await agent.PeerApi.GetAsync("/peer/app-update/agent/0.6.0")).StatusCode);

        var settings = await agent.GetAsync<SettingsDto>("/api/settings");
        (await agent.SendAsync(HttpMethod.Put, "/api/settings", settings with { SeedingEnabled = false })).EnsureSuccessStatusCode();

        Assert.Empty((await agent.PeerApi.GetFromJsonAsync<List<AppUpdateOfferDto>>("/peer/app-update", TestAgent.Json))!);
    }

    [Fact]
    public async Task After_the_update_the_package_of_the_version_that_runs_is_still_offered_to_the_others()
    {
        await PublishAsync("0.5.0");
        var before = await StartAsync(running: "0.4.3");
        await WaitForAsync(before, s => s.State == AppUpdateState.Ready);
        await before.StopAsync();
        _agents.Remove(before);

        var after = await StartAsync(running: "0.5.0", existingDir: before.Dir); // as if it had been applied

        var status = await WaitForAsync(after, s => s.LastChecked is not null);
        Assert.Equal(AppUpdateState.UpToDate, status.State);
        var offers = await after.PeerApi.GetFromJsonAsync<List<AppUpdateOfferDto>>("/peer/app-update", TestAgent.Json);
        Assert.Equal([new AppUpdateOfferDto(AppFlavors.Agent, "0.5.0")], offers);
    }
}
