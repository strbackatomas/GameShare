using System.Net;
using System.Net.Http.Json;
using System.Text;
using GameShare.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace GameShare.Agent.Tests;

/// <summary>A backup of the pairings survives a reinstall: the PC comes back as itself and nobody has to pair again.</summary>
public class PairingBackupTests
{
    private const string Password = "spravne-heslo";

    /// <summary>Notes the restart instead of ending the test host; the test restarts the agent itself.</summary>
    private sealed class FakeRestarter : IAgentRestarter
    {
        public int Restarts { get; private set; }
        public void Restart() => Restarts++;
    }

    private static Task<TestAgent> StartAsync(string name, int discovery, FakeRestarter? restarter = null, bool preloadGame = false) =>
        TestAgent.StartAsync(name, discovery, preloadGame: preloadGame, services: s => s.AddSingleton<IAgentRestarter>(restarter ?? new FakeRestarter()));

    private static async Task<string> IdAsync(TestAgent agent) => (await agent.GetAsync<StatusDto>("/api/status")).MachineId;
    private static Task<RemoteStatusDto> RemoteAsync(TestAgent agent) => agent.GetAsync<RemoteStatusDto>("/api/remote");

    private static async Task PairAsync(TestAgent controller, TestAgent target)
    {
        Assert.True((await target.SendAsync(HttpMethod.Put, "/api/remote/enabled", new RemoteEnableRequest(true))).IsSuccessStatusCode);
        var code = (await (await target.SendAsync(HttpMethod.Post, "/api/remote/pairing")).Content.ReadFromJsonAsync<RemotePairingDto>(TestAgent.Json))!.Code;
        var paired = await controller.SendAsync(HttpMethod.Post, "/api/remote/targets", new RemotePairRequest(await IdAsync(target), code));
        Assert.True(paired.IsSuccessStatusCode, await paired.Content.ReadAsStringAsync());
    }

    private static async Task<byte[]> BackupAsync(TestAgent agent, string password = Password)
    {
        var response = await agent.SendAsync(HttpMethod.Post, "/api/remote/backup", new RemoteBackupRequest(password));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static Task<HttpResponseMessage> RestoreAsync(TestAgent agent, byte[] backup, string password = Password) =>
        agent.SendAsync(HttpMethod.Post, "/api/remote/restore", new RemoteRestoreRequest(password, Convert.ToBase64String(backup)));

    private static async Task ManagesAsync(TestAgent controller, string targetId) =>
        await Poll.UntilAsync(async () => (await controller.Api.GetAsync($"/api/remote/targets/{targetId}/api/status")).StatusCode == HttpStatusCode.OK,
            "the controller to manage the target", 20_000);

    [Fact]
    public async Task A_managed_pc_reinstalled_and_restored_from_its_backup_is_managed_again_without_pairing()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var controller = await StartAsync("PC-01", discovery);
        var target = await StartAsync("PC-02", discovery);
        await Poll.UntilAsync(async () => (await controller.PeersAsync()).Count == 1, "PC-01 to see PC-02");
        await PairAsync(controller, target);
        var targetId = await IdAsync(target);
        var fingerprint = (await RemoteAsync(target)).Fingerprint;
        await ManagesAsync(controller, targetId);
        var backup = await BackupAsync(target);
        Assert.StartsWith("{", Encoding.UTF8.GetString(backup).TrimStart());
        Assert.DoesNotContain(fingerprint, Encoding.UTF8.GetString(backup)); // nothing readable without the password

        // Windows reinstalled: PC-02 comes back with an empty data folder, as a new PC.
        await target.DisposeAsync();
        var restarter = new FakeRestarter();
        await using var reinstalled = await StartAsync("PC-02", discovery, restarter);
        Assert.NotEqual(targetId, await IdAsync(reinstalled));

        var restored = await RestoreAsync(reinstalled, backup);
        Assert.Equal(HttpStatusCode.Accepted, restored.StatusCode);
        var result = (await restored.Content.ReadFromJsonAsync<RemoteRestoreResultDto>(TestAgent.Json))!;
        Assert.Equal(("PC-02", 1, 0), (result.MachineName, result.Controllers, result.Targets));
        Assert.Equal(1, restarter.Restarts);
        await reinstalled.RestartAsync();

        // It is PC-02 again, with its certificate and its pairing, and PC-01 manages it as before.
        Assert.Equal(targetId, await IdAsync(reinstalled));
        var status = await RemoteAsync(reinstalled);
        Assert.Equal(fingerprint, status.Fingerprint);
        Assert.True(status.Listening);
        Assert.Equal("PC-01", Assert.Single(status.Controllers).MachineName);
        await ManagesAsync(controller, targetId);
    }

    [Fact]
    public async Task A_managing_pc_reinstalled_and_restored_manages_all_its_pcs_again()
    {
        int discovery = TestAgent.DiscoveryPort();
        var controller = await StartAsync("PC-01", discovery);
        await using var target = await StartAsync("PC-02", discovery);
        await Poll.UntilAsync(async () => (await controller.PeersAsync()).Count == 1, "PC-01 to see PC-02");
        await PairAsync(controller, target);
        var targetId = await IdAsync(target);
        var backup = await BackupAsync(controller);

        await controller.DisposeAsync();
        await using var reinstalled = await StartAsync("PC-01", discovery);
        Assert.Equal(HttpStatusCode.Accepted, (await RestoreAsync(reinstalled, backup)).StatusCode);
        await reinstalled.RestartAsync();

        Assert.Equal("PC-02", Assert.Single((await RemoteAsync(reinstalled)).Targets).MachineName);
        await ManagesAsync(reinstalled, targetId);
    }

    [Fact]
    public async Task A_backup_is_not_restored_while_its_pc_is_on_the_network_or_with_the_wrong_password()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var original = await StartAsync("PC-02", discovery);
        var restarter = new FakeRestarter();
        await using var other = await StartAsync("PC-03", discovery, restarter);
        await Poll.UntilAsync(async () => (await other.PeersAsync()).Count == 1, "PC-03 to see PC-02");
        var backup = await BackupAsync(original);

        var wrong = await RestoreAsync(other, backup, "spatne-heslo");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("password is wrong", await wrong.Content.ReadAsStringAsync());

        var twin = await RestoreAsync(other, backup);
        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        Assert.Contains("is on the network now as PC-02", await twin.Content.ReadAsStringAsync());

        Assert.Equal(0, restarter.Restarts);
        Assert.NotEqual(await IdAsync(original), await IdAsync(other)); // nothing changed

        var shortPassword = await other.SendAsync(HttpMethod.Post, "/api/remote/backup", new RemoteBackupRequest("kratke"));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
    }

    [Fact]
    public void The_file_opens_only_with_its_password_and_only_as_it_was_made()
    {
        var payload = new PairingBackupPayload("abc", Convert.ToBase64String([1, 2, 3]), "{}");
        var file = PairingBackup.Create(payload, Password, "PC-07");

        Assert.Equal(payload, PairingBackup.Open(file, Password));
        Assert.Equal("PC-07", PairingBackup.MachineNameOf(file));
        Assert.Contains("password is wrong", Assert.Throws<ArgumentException>(() => PairingBackup.Open(file, "jine-heslo!")).Message);

        // The plain header is bound into the encryption: renaming the PC in the file breaks it.
        var renamed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file).Replace("PC-07", "PC-99"));
        Assert.Throws<ArgumentException>(() => PairingBackup.Open(renamed, Password));

        Assert.Contains("not a GameShare pairing backup", Assert.Throws<ArgumentException>(() => PairingBackup.Open("{}"u8.ToArray(), Password)).Message);
        Assert.Throws<ArgumentException>(() => PairingBackup.Create(payload, "kratke", "PC-07"));
    }
}
