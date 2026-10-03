using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using GameShare.Protocol;

namespace GameShare.Agent.Tests;

/// <summary>
/// Remote management between whole agents: turning it on, pairing with a code, driving an install on another PC, and everything
/// that must be refused on the way.
/// </summary>
public class RemoteTests
{
    private static Task<RemoteStatusDto> RemoteAsync(TestAgent agent) => agent.GetAsync<RemoteStatusDto>("/api/remote");

    private static async Task EnableAsync(TestAgent agent, bool enabled = true)
    {
        var response = await agent.SendAsync(HttpMethod.Put, "/api/remote/enabled", new RemoteEnableRequest(enabled));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> OpenPairingAsync(TestAgent agent)
    {
        var response = await agent.SendAsync(HttpMethod.Post, "/api/remote/pairing");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RemotePairingDto>(TestAgent.Json))!.Code;
    }

    private static async Task<HttpResponseMessage> PairAsync(TestAgent controller, TestAgent target, string code) =>
        await controller.SendAsync(HttpMethod.Post, "/api/remote/targets", new RemotePairRequest(await IdAsync(target), code));

    private static async Task<string> IdAsync(TestAgent agent) => (await agent.GetAsync<StatusDto>("/api/status")).MachineId;

    private static async Task<HttpResponseMessage> ViaAsync(TestAgent controller, string targetId, HttpMethod method, string path, object? body = null) =>
        await controller.SendAsync(method, $"/api/remote/targets/{targetId}/api/{path}", body);

    /// <summary>Two agents that see each other, the second with remote management on and the first paired with it.</summary>
    private static async Task<(TestAgent Controller, TestAgent Target, string TargetId)> PairedAsync(bool preloadGame = false)
    {
        int discovery = TestAgent.DiscoveryPort();
        var controller = await TestAgent.StartAsync("PC-01", discovery, preloadGame: preloadGame);
        var target = await TestAgent.StartAsync("PC-02", discovery);
        await Poll.UntilAsync(async () => (await controller.PeersAsync()).Count == 1, "PC-01 to see PC-02");
        await EnableAsync(target);
        var code = await OpenPairingAsync(target);
        var response = await PairAsync(controller, target, code);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (controller, target, await IdAsync(target));
    }

    [Fact]
    public async Task Remote_management_is_off_until_turned_on_and_the_port_is_closed_meanwhile()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort());

        var status = await RemoteAsync(agent);
        Assert.True(status.Allowed);
        Assert.False(status.Enabled);
        Assert.False(status.Listening);
        Assert.Null((await agent.PeerApi.GetFromJsonAsync<PeerHelloDto>("/peer/hello", TestAgent.Json))!.RemotePort);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => UnpairedClient(agent).GetAsync("/api/status"));
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Post, "/api/remote/pairing")).StatusCode);

        await EnableAsync(agent);
        Assert.True((await RemoteAsync(agent)).Listening);
        Assert.Equal(agent.RemotePort, (await agent.PeerApi.GetFromJsonAsync<PeerHelloDto>("/peer/hello", TestAgent.Json))!.RemotePort);

        // Survives a restart.
        await agent.RestartAsync();
        Assert.True((await RemoteAsync(agent)).Listening);

        await EnableAsync(agent, false);
        Assert.False((await RemoteAsync(agent)).Listening);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => UnpairedClient(agent).GetAsync("/api/status"));
    }

    [Fact]
    public async Task Paired_pc_installs_a_game_on_the_target_and_the_target_notes_who_did_it()
    {
        var (controller, target, targetId) = await PairedAsync(preloadGame: true);
        await using var _c = controller;
        await using var _t = target;

        // Both sides know each other, with the certificate the other one really has.
        var controllerSide = await RemoteAsync(controller);
        var targetSide = await RemoteAsync(target);
        var pinned = Assert.Single(controllerSide.Targets);
        Assert.Equal("PC-02", pinned.MachineName);
        Assert.Equal(targetSide.Fingerprint, pinned.Fingerprint);
        Assert.True(pinned.Online);
        var allowed = Assert.Single(targetSide.Controllers);
        Assert.Equal("PC-01", allowed.MachineName);
        Assert.Equal(controllerSide.Fingerprint, allowed.Fingerprint);
        Assert.Null(targetSide.Pairing); // a code is good for one pairing

        // Looking at the target from the controller.
        var status = await (await ViaAsync(controller, targetId, HttpMethod.Get, "status")).Content.ReadFromJsonAsync<StatusDto>(TestAgent.Json);
        Assert.Equal("PC-02", status!.MachineName);

        // The target sees the controller's game on the LAN; the controller tells it to install it.
        GameDto? game = null;
        await Poll.UntilAsync(async () =>
        {
            var games = await (await ViaAsync(controller, targetId, HttpMethod.Get, "games")).Content.ReadFromJsonAsync<List<GameDto>>(TestAgent.Json);
            return (game = games!.FirstOrDefault(g => g.State == GameState.AvailableOnLan)) is not null;
        }, "PC-02 to see the game, asked through PC-01");
        await using var events = await EventRecorder.ConnectAsync(target);
        RemoteActionDto? noticed = null;
        events.On<RemoteActionDto>(GameShareEvents.RemoteAction, a => noticed = a);

        var install = await ViaAsync(controller, targetId, HttpMethod.Post, $"games/{game!.ContentHash}/install");
        Assert.True(install.StatusCode == HttpStatusCode.Accepted, await install.Content.ReadAsStringAsync());
        var download = (await install.Content.ReadFromJsonAsync<DownloadDto>(TestAgent.Json))!;
        await target.WaitForDownloadAsync(download.Id, "Completed");
        Assert.Equal(TestGame.HashTree(controller.InstalledPath), TestGame.HashTree(target.InstalledPath));

        // The person at the target is told, and the target remembers when the controller last did something.
        await Poll.UntilAsync(() => noticed is not null, "RemoteAction event on PC-02");
        Assert.Equal("PC-01", noticed!.ControllerName);
        Assert.Equal($"POST games/{game.ContentHash}/install", noticed.Action);
        Assert.Equal(202, noticed.Status);
        Assert.NotNull(Assert.Single((await RemoteAsync(target)).Controllers).LastUsed);
    }

    [Fact]
    public async Task What_is_not_on_the_list_is_refused_remotely_but_still_works_locally()
    {
        var (controller, target, targetId) = await PairedAsync();
        await using var _c = controller;
        await using var _t = target;

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "settings"),
            (HttpMethod.Put, "settings"),
            (HttpMethod.Get, "logs"),
            (HttpMethod.Post, "agent/restart"),
            (HttpMethod.Get, "remote"),
            (HttpMethod.Post, "remote/pairing"),
            (HttpMethod.Put, "remote/enabled"),
            (HttpMethod.Post, $"games/{new string('a', 64)}/launch"),
            (HttpMethod.Delete, $"games/{new string('a', 64)}"),
            (HttpMethod.Get, "games/../settings"),
        })
        {
            var response = await ViaAsync(controller, targetId, method, path, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { });
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {(int)response.StatusCode}");
        }

        // The target's own client is not affected.
        Assert.Equal(HttpStatusCode.OK, (await target.Api.GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_ends_the_pairing_so_the_right_one_no_longer_works_either()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var controller = await TestAgent.StartAsync("PC-01", discovery);
        await using var target = await TestAgent.StartAsync("PC-02", discovery);
        await Poll.UntilAsync(async () => (await controller.PeersAsync()).Count == 1, "PC-01 to see PC-02");
        await EnableAsync(target);

        // Not a code at all: refused before anything is sent, the pairing stays open.
        var code = await OpenPairingAsync(target);
        Assert.Equal(HttpStatusCode.BadRequest, (await PairAsync(controller, target, "1234")).StatusCode);
        Assert.NotNull((await RemoteAsync(target)).Pairing);

        // A wrong code is one attempt, and the pairing is gone.
        var wrong = code[..^1] + (code[^1] == '0' ? '1' : '0');
        var refused = await PairAsync(controller, target, wrong);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("does not match", await refused.Content.ReadAsStringAsync());
        Assert.Null((await RemoteAsync(target)).Pairing);
        Assert.Equal(HttpStatusCode.Conflict, (await PairAsync(controller, target, code)).StatusCode);
        Assert.Empty((await RemoteAsync(target)).Controllers);
        Assert.Empty((await RemoteAsync(controller)).Targets);

        // A new pairing works, and the code may be typed sloppily.
        var again = await OpenPairingAsync(target);
        var sloppy = again.Replace("-", " ").ToLowerInvariant().Replace('0', 'o').Replace('1', 'l');
        var paired = await PairAsync(controller, target, sloppy);
        Assert.True(paired.IsSuccessStatusCode, await paired.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        int discovery = TestAgent.DiscoveryPort();
        await using var controller = await TestAgent.StartAsync("PC-01", discovery);
        await using var target = await TestAgent.StartAsync("PC-02", discovery, tweak: o => o.RemotePairingLifetime = TimeSpan.FromSeconds(1));
        await Poll.UntilAsync(async () => (await controller.PeersAsync()).Count == 1, "PC-01 to see PC-02");
        await EnableAsync(target);

        var code = await OpenPairingAsync(target);
        await Task.Delay(1500);
        Assert.Null((await RemoteAsync(target)).Pairing);
        Assert.Equal(HttpStatusCode.Conflict, (await PairAsync(controller, target, code)).StatusCode);
    }

    [Fact]
    public async Task A_pc_that_is_not_paired_gets_nothing_even_with_a_certificate()
    {
        var (controller, target, _) = await PairedAsync();
        await using var _c = controller;
        await using var _t = target;

        using var stranger = UnpairedClient(target);
        var response = await stranger.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("not paired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Removing_the_controller_on_the_target_cuts_it_off_at_once()
    {
        var (controller, target, targetId) = await PairedAsync();
        await using var _c = controller;
        await using var _t = target;
        Assert.Equal(HttpStatusCode.OK, (await ViaAsync(controller, targetId, HttpMethod.Get, "status")).StatusCode);

        var controllerId = await IdAsync(controller);
        Assert.Equal(HttpStatusCode.NoContent, (await target.SendAsync(HttpMethod.Delete, $"/api/remote/controllers/{controllerId}")).StatusCode);

        // The same kept-alive connection, refused on the next request.
        Assert.Equal(HttpStatusCode.Forbidden, (await ViaAsync(controller, targetId, HttpMethod.Get, "status")).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_on_the_target_keeps_the_pairing_for_later_but_answers_nothing_meanwhile()
    {
        var (controller, target, targetId) = await PairedAsync();
        await using var _c = controller;
        await using var _t = target;

        await EnableAsync(target, false);
        var off = await ViaAsync(controller, targetId, HttpMethod.Get, "status");
        Assert.Equal(HttpStatusCode.BadGateway, off.StatusCode);
        Assert.Contains("does not take remote management now", await off.Content.ReadAsStringAsync());

        await EnableAsync(target);
        Assert.Equal(HttpStatusCode.OK, (await ViaAsync(controller, targetId, HttpMethod.Get, "status")).StatusCode);
    }

    [Fact]
    public async Task Forgetting_a_target_on_the_controller_ends_access_from_there()
    {
        var (controller, target, targetId) = await PairedAsync();
        await using var _c = controller;
        await using var _t = target;

        Assert.Equal(HttpStatusCode.NoContent, (await controller.SendAsync(HttpMethod.Delete, $"/api/remote/targets/{targetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ViaAsync(controller, targetId, HttpMethod.Get, "status")).StatusCode);
    }

    [Fact]
    public async Task The_portable_build_never_takes_remote_management()
    {
        await using var agent = await TestAgent.StartAsync("PC-01", TestAgent.DiscoveryPort(), tweak: o => o.RemoteManagementAllowed = false);

        Assert.False((await RemoteAsync(agent)).Allowed);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Put, "/api/remote/enabled", new RemoteEnableRequest(true))).StatusCode);
        Assert.False((await RemoteAsync(agent)).Listening);
    }

    [Fact]
    public void Codes_are_twelve_characters_that_cannot_be_misread_and_proofs_bind_both_certificates()
    {
        var code = RemotePairing.NewCode();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code);
        Assert.Equal(RemotePairing.NormalizeCode(code), RemotePairing.NormalizeCode(code.ToLowerInvariant().Replace("-", "")));
        Assert.Throws<ArgumentException>(() => RemotePairing.NormalizeCode("ABCD-EFGH-IJK!"));
        Assert.Throws<ArgumentException>(() => RemotePairing.NormalizeCode("ABCD-EFGH"));

        var proof = RemotePairing.OfferProof(code, "c", "fc", "t", "ft");
        Assert.True(RemotePairing.ProofsEqual(proof, RemotePairing.OfferProof(code, "c", "fc", "t", "ft")));
        Assert.False(RemotePairing.ProofsEqual(proof, RemotePairing.OfferProof(code, "c", "fc", "t", "other certificate")));
        Assert.False(RemotePairing.ProofsEqual(proof, RemotePairing.AcceptProof(code, "c", "fc", "t", "ft")));
    }

    [Theory]
    [InlineData("GET", "status", true)]
    [InlineData("POST", "games/0000000000000000000000000000000000000000000000000000000000000000/install", true)]
    [InlineData("DELETE", "downloads/12", true)]
    [InlineData("POST", "downloads/12/pause", true)]
    [InlineData("PUT", "settings", false)]
    [InlineData("GET", "settings", false)]
    [InlineData("POST", "games/0000000000000000000000000000000000000000000000000000000000000000/launch", false)]
    [InlineData("DELETE", "games/0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("POST", "games/0000000000000000000000000000000000000000000000000000000000000000/install/x", false)]
    [InlineData("GET", "remote", false)]
    [InlineData("POST", "remote/targets", false)]
    public void Whitelist(string method, string path, bool allowed) => Assert.Equal(allowed, RemoteWhitelist.Allows(method, path));

    /// <summary>An HTTPS client with a certificate of its own that was never paired, and that trusts whatever the agent shows.</summary>
    private static HttpClient UnpairedClient(TestAgent agent)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(RemotePairing.NewIdentity("stranger"), null);
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        handler.ClientCertificates.Add(certificate);
        return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{agent.RemotePort}"), Timeout = TimeSpan.FromSeconds(10) };
    }
}
