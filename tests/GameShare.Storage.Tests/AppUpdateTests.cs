using System.Text;
using System.Text.Json;
using GameShare.Protocol;

namespace GameShare.Storage.Tests;

/// <summary>Versions, signed release descriptions and the swap of installed files, the parts of the updater that need no agent.</summary>
public class SemVerTests
{
    [Theory]
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.5.0", "0.5.0-beta.2")]
    [InlineData("0.5.0-beta.10", "0.5.0-beta.2")]
    [InlineData("0.5.0-beta", "0.5.0-1")]
    [InlineData("0.5.0-beta.1", "0.5.0-beta")]
    [InlineData("0.4.4", "0.4.3")]
    public void Newer_versions_compare_as_semver_org_says(string newer, string older)
    {
        Assert.True(SemVer.IsNewer(newer, older));
        Assert.False(SemVer.IsNewer(older, newer));
        Assert.True(SemVer.Parse(newer).CompareTo(SemVer.Parse(older)) > 0);
    }

    [Fact]
    public void The_same_version_is_not_newer() => Assert.False(SemVer.IsNewer("0.4.3", "0.4.3"));

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3+build")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_version_is_refused_and_never_newer(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
        Assert.False(SemVer.IsNewer(text, "0.0.1"));
    }
}

public class ReleaseSigningTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] FakeTorrent = Encoding.ASCII.GetBytes("d4:infod4:name3:abcee");

    private static async Task<AppRelease> ReleaseAsync(string version = "0.5.0", string flavor = AppFlavors.Agent)
    {
        var dir = TestGame.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "GameShare.Agent.exe"), $"agent {version}");
            Directory.CreateDirectory(Path.Combine(dir, "Client"));
            File.WriteAllText(Path.Combine(dir, "Client", "GameShare.exe"), $"client {version}");
            var manifest = ManifestBuilder.Build(await ContentScanner.ScanAsync(dir)) with { Version = version, TorrentInfoHash = new string('1', 40) };
            return new AppRelease(version, flavor, Now, "Fixed things.", manifest, FakeTorrent, $"GameShare-Update-{flavor}.zip", new string('2', 64));
        }
        finally { TestGame.DeleteQuietly(dir); }
    }

    [Fact]
    public async Task A_signed_release_opens_with_the_public_key_and_says_what_was_signed()
    {
        var keys = TrustSigning.GenerateKeyPair();
        var release = await ReleaseAsync();

        var opened = ReleaseSigning.Open(ReleaseSigning.Serialize(ReleaseSigning.Sign(release, keys.PrivateKey)), keys.PublicKey);

        Assert.Equal("0.5.0", opened.Version);
        Assert.Equal(AppFlavors.Agent, opened.Flavor);
        Assert.Equal(release.Manifest.ContentHash, opened.Manifest.ContentHash);
        Assert.Equal(FakeTorrent, opened.Torrent);
        Assert.Equal("Fixed things.", opened.Notes);
    }

    [Fact]
    public async Task A_release_signed_with_another_key_is_refused()
    {
        var release = await ReleaseAsync();
        var envelope = ReleaseSigning.Serialize(ReleaseSigning.Sign(release, TrustSigning.GenerateKeyPair().PrivateKey));

        var ex = Assert.Throws<InvalidDataException>(() => ReleaseSigning.Open(envelope, TrustSigning.GenerateKeyPair().PublicKey));

        Assert.Contains("release description is signed with key", ex.Message);
    }

    [Fact]
    public async Task A_release_changed_after_signing_is_refused()
    {
        var keys = TrustSigning.GenerateKeyPair();
        var doc = ReleaseSigning.Sign(await ReleaseAsync(), keys.PrivateKey);
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(doc.Payload)).Replace("Fixed things.", "Run me as SYSTEM.");
        var forged = doc with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) };

        var ex = Assert.Throws<InvalidDataException>(() => ReleaseSigning.Open(ReleaseSigning.Serialize(forged), keys.PublicKey));

        Assert.Contains("signature", ex.Message);
        Assert.Contains("release key", ex.Message);
    }

    [Fact]
    public async Task A_release_whose_files_do_not_match_its_content_hash_is_refused_even_when_signed()
    {
        var release = await ReleaseAsync();
        var files = release.Manifest.Files.Select((f, i) => i == 0 ? f with { Hash = new string('f', 64) } : f).ToList();
        var bad = release with { Manifest = release.Manifest with { Files = files } };

        var ex = Assert.Throws<InvalidDataException>(() => ReleaseSigning.Validate(bad));

        Assert.Contains("ContentHash", ex.Message);
    }

    public static TheoryData<string, Func<AppRelease, AppRelease>> Broken => new()
    {
        { "not a version", r => r with { Version = "latest" } },
        { "build this version knows", r => r with { Flavor = "portable" } },
        { "package says version", r => r with { Version = "0.5.1" } },
        { "plain zip file name", r => r with { ZipName = "../GameShare.zip" } },
        { "both a name and a hash", r => r with { ZipSha256 = null } },
        { "not a SHA-256", r => r with { ZipSha256 = "abc" } },
        { "torrent", r => r with { Torrent = [] } },
        { "no torrent info hash", r => r with { Manifest = r.Manifest with { TorrentInfoHash = null } } },
        { "may change", r => r with { Manifest = r.Manifest with { VolatilePatterns = ["*.log"] } } },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public async Task An_inconsistent_release_is_never_signed(string expected, Func<AppRelease, AppRelease> breakIt)
    {
        var release = breakIt(await ReleaseAsync());

        var ex = Assert.Throws<InvalidDataException>(() => ReleaseSigning.Sign(release, TrustSigning.GenerateKeyPair().PrivateKey));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_trust_list_is_not_a_release_even_when_the_keys_are_mixed_up()
    {
        var keys = TrustSigning.GenerateKeyPair();
        var list = TrustSigning.Serialize(TrustSigning.Sign(TrustPayload.Empty(Now), keys.PrivateKey));

        Assert.Throws<InvalidDataException>(() => ReleaseSigning.Open(list, keys.PublicKey));
    }

    [Theory]
    [InlineData(AppFlavors.LanParty, "0.4.3", null, "lanparty build")]
    [InlineData(AppFlavors.Agent, "0.5.0", null, "not newer")]
    [InlineData(AppFlavors.Agent, "0.6.0", null, "not newer")]
    [InlineData(AppFlavors.Agent, "0.4.3", "0.5.1", "older than 0.5.1")]
    public async Task A_release_that_is_not_for_this_PC_does_not_apply(string runningFlavor, string running, string? held, string expected)
    {
        var release = await ReleaseAsync("0.5.0", AppFlavors.Agent);

        var ex = Assert.Throws<InvalidDataException>(() => ReleaseSigning.CheckApplies(release, runningFlavor, running, held));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_newer_release_for_this_build_applies()
    {
        var release = await ReleaseAsync("0.5.0");

        ReleaseSigning.CheckApplies(release, AppFlavors.Agent, "0.4.3");
        ReleaseSigning.CheckApplies(release, AppFlavors.Agent, "0.4.3", heldVersion: "0.5.0");
    }
}

public sealed class AppUpdateFilesTests : IDisposable
{
    private readonly string _root = TestGame.NewTempDir();
    private string Staging => Path.Combine(_root, "staging");
    private string Install => Path.Combine(_root, "install");

    public AppUpdateFilesTests()
    {
        Write(Install, "GameShare.Agent.exe", "agent 1");
        Write(Install, "same.dll", "unchanged");
        Write(Install, "Client/GameShare.exe", "client 1");
        Write(Install, "appsettings.json", "{ \"Agent\": { \"TorrentPort\": 7000 } }");
        Write(Install, "old.dll", "only in version 1");
        Write(Install, "notes.txt", "not ours");

        Write(Staging, "GameShare.Agent.exe", "agent 2");
        Write(Staging, "same.dll", "unchanged");
        Write(Staging, "Client/GameShare.exe", "client 2");
        Write(Staging, "Client/new.dll", "new in version 2");
        Write(Staging, "appsettings.json", "{ \"Agent\": {} }");
    }

    public void Dispose() => TestGame.DeleteQuietly(_root);

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar)));

    private bool Exists(string relative) => File.Exists(Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar)));

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Install, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(Install, p).Replace('\\', '/'), File.ReadAllText);

    private async Task<GameManifest> ManifestAsync() =>
        ManifestBuilder.Build(await ContentScanner.ScanAsync(Staging)) with { Version = "0.5.0" };

    [Fact]
    public async Task A_swap_puts_the_package_in_place_and_keeps_what_the_PC_set_up_for_itself()
    {
        await AppUpdateFiles.SwapAsync(Staging, Install, await ManifestAsync(), previousFiles: ["GameShare.Agent.exe", "same.dll", "old.dll"]);

        Assert.Equal("agent 2", Read("GameShare.Agent.exe"));
        Assert.Equal("client 2", Read("Client/GameShare.exe"));
        Assert.Equal("new in version 2", Read("Client/new.dll"));
        Assert.Contains("7000", Read("appsettings.json"));
        Assert.False(Exists("old.dll"));
        Assert.Equal("not ours", Read("notes.txt"));
        Assert.Equal("agent 1", Read("GameShare.Agent.exe.gsold"));
        Assert.True(AppUpdateFiles.HasPendingSwap(Install));

        AppUpdateFiles.Commit(Install);

        Assert.False(AppUpdateFiles.HasPendingSwap(Install));
        Assert.DoesNotContain(Snapshot().Keys, k => k.EndsWith(AppUpdateFiles.AsideSuffix));
        Assert.False(Exists(AppUpdateFiles.LeftoversFileName));
    }

    [Fact]
    public async Task Without_the_old_file_list_nothing_else_in_the_folder_is_touched()
    {
        await AppUpdateFiles.SwapAsync(Staging, Install, await ManifestAsync());
        AppUpdateFiles.Commit(Install);

        Assert.Equal("only in version 1", Read("old.dll"));
        Assert.Equal("not ours", Read("notes.txt"));
    }

    [Fact]
    public async Task A_rollback_restores_the_installation_exactly()
    {
        var before = Snapshot();
        await AppUpdateFiles.SwapAsync(Staging, Install, await ManifestAsync(), previousFiles: ["old.dll"]);

        Assert.True(AppUpdateFiles.Rollback(Install));

        Assert.Equal(before, Snapshot());
        Assert.False(AppUpdateFiles.Rollback(Install));
    }

    [Fact]
    public async Task A_rollback_after_a_crash_halfway_leaves_alone_what_was_never_done()
    {
        var before = Snapshot();
        await AppUpdateFiles.SwapAsync(Staging, Install, await ManifestAsync(), previousFiles: ["old.dll"]);
        // As if the process died before it moved the client aside: the old client is still in its place, the journal names it.
        var client = Path.Combine(Install, "Client", "GameShare.exe");
        File.Move(client + AppUpdateFiles.AsideSuffix, client, overwrite: true);

        AppUpdateFiles.Rollback(Install);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task A_package_that_is_not_what_was_signed_is_refused_before_anything_is_touched()
    {
        var before = Snapshot();
        var manifest = await ManifestAsync();
        Write(Staging, "Client/GameShare.exe", "client 2, changed afterwards");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateFiles.SwapAsync(Staging, Install, manifest));

        Assert.Contains("not what was signed", ex.Message);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task A_file_that_cannot_be_moved_aside_rolls_everything_back()
    {
        var before = Snapshot();
        var manifest = await ManifestAsync();
        // Held open without FILE_SHARE_DELETE, so it cannot be renamed. It is the last file in the manifest, so the others were swapped by then.
        using (new FileStream(Path.Combine(Install, "same.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => AppUpdateFiles.SwapAsync(Staging, Install, manifest));
        }

        Assert.Equal(before, Snapshot());
        Assert.False(AppUpdateFiles.HasPendingSwap(Install));
    }

    [Fact]
    public async Task A_running_program_is_moved_aside_and_deleted_later_once_it_has_quit()
    {
        var manifest = await ManifestAsync();
        var client = Path.Combine(Install, "Client", "GameShare.exe");
        // A running program's image is open with FILE_SHARE_DELETE: it can be renamed, but not deleted while it runs.
        using (new FileStream(client, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            await AppUpdateFiles.SwapAsync(Staging, Install, manifest);
        }
        Assert.Equal("client 2", Read("Client/GameShare.exe"));

        // A plain open handle does not refuse deletion the way a mapped image does, so the running client is played by one without FILE_SHARE_DELETE.
        using (new FileStream(client + AppUpdateFiles.AsideSuffix, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AppUpdateFiles.Commit(Install);
            Assert.True(Exists(AppUpdateFiles.LeftoversFileName));
            AppUpdateFiles.CleanupLeftovers(Install); // still running: stays on the list
            Assert.True(Exists("Client/GameShare.exe" + AppUpdateFiles.AsideSuffix));
        }

        AppUpdateFiles.CleanupLeftovers(Install);

        Assert.False(Exists("Client/GameShare.exe" + AppUpdateFiles.AsideSuffix));
        Assert.False(Exists(AppUpdateFiles.LeftoversFileName));
        Assert.False(AppUpdateFiles.HasPendingSwap(Install));
    }

    [Fact]
    public async Task A_second_swap_is_refused_while_the_first_is_undecided()
    {
        var manifest = await ManifestAsync();
        await AppUpdateFiles.SwapAsync(Staging, Install, manifest);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AppUpdateFiles.SwapAsync(Staging, Install, manifest));
    }

    [Fact]
    public async Task A_journal_that_points_outside_the_installation_is_not_followed()
    {
        await AppUpdateFiles.SwapAsync(Staging, Install, await ManifestAsync());
        var journal = Path.Combine(Install, AppUpdateFiles.JournalFileName);
        File.WriteAllText(journal, File.ReadAllText(journal).Replace("\"Client/GameShare.exe\"", "\"../../Windows/win.ini\""));

        Assert.Throws<InvalidDataException>(() => AppUpdateFiles.Rollback(Install));
    }
}
