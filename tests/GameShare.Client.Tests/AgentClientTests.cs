using System.Net;
using System.Text;
using GameShare.Client.Services;
using GameShare.Protocol;

namespace GameShare.Client.Tests;

public class AgentClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (AgentClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new AgentClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:47701") }), handler);
    }

    [Fact]
    public async Task Games_are_read_with_their_state_as_text()
    {
        var (client, _) = Create((_, _) => Json("""
            [{"contentHash":"aa","gameId":"beamng","name":"BeamNG.drive","version":"0.38","totalSize":70000000000,"state":"AvailableOnLan",
              "installPath":null,"peerNames":["PC-01","PC-04"],"downloadId":null,"definition":null,"updatesContentHash":"bb"}]
            """));

        var g = Assert.Single(await client.GetGamesAsync());

        Assert.Equal(GameState.AvailableOnLan, g.State);
        Assert.Equal(["PC-01", "PC-04"], g.PeerNames);
        Assert.Equal("bb", g.UpdatesContentHash);
        Assert.Equal(70_000_000_000, g.TotalSize);
    }

    [Fact]
    public async Task An_empty_answer_is_an_empty_list_not_a_failure()
    {
        var (client, _) = Create((_, _) => Json("[]"));
        Assert.Empty(await client.GetGamesAsync());
        Assert.Empty(await client.GetPeersAsync());
        Assert.Empty(await client.GetDownloadsAsync());
    }

    [Fact]
    public async Task The_agents_own_explanation_is_what_the_user_sees()
    {
        var (client, _) = Create((_, _) => Json(
            """{"title":"Not possible in the current state","status":409,"detail":"TestGame is already installed at D:\\Games\\TestGame."}""", HttpStatusCode.Conflict));

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.InstallAsync(Data.A));

        Assert.Equal(@"TestGame is already installed at D:\Games\TestGame.", ex.Message);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Without_a_detail_the_title_is_used_and_without_either_the_status_code()
    {
        var (titleOnly, _) = Create((_, _) => Json("""{"title":"Not found","status":404}""", HttpStatusCode.NotFound));
        Assert.Equal("Not found", (await Assert.ThrowsAsync<AgentException>(() => titleOnly.GetStatusAsync())).Message);

        var (plain, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<html>boom</html>") });
        var ex = await Assert.ThrowsAsync<AgentException>(() => plain.GetStatusAsync());
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task A_stopped_agent_becomes_a_message_the_user_can_act_on()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        var client = new AgentClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:47701") });

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GetStatusAsync());

        Assert.Contains("neodpovídá", ex.Message);
        Assert.Contains("služba", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancelling_the_call_yourself_is_not_reported_as_an_agent_failure()
    {
        var (client, _) = Create((_, _) => Json("[]"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetGamesAsync(cts.Token));
    }

    [Fact]
    public async Task Install_sends_the_target_folder_only_when_one_was_chosen()
    {
        var (client, handler) = Create((_, _) => Json(
            """{"id":1,"contentHash":"aa","gameName":"G","state":"Downloading","bytesDone":0,"bytesTotal":10,"percent":0,"speedBytesPerSecond":0,"peers":0,"etaSeconds":null,"error":null,"peerDetails":[],"kind":"Install"}""",
            HttpStatusCode.Accepted));

        await client.InstallAsync(Data.A);
        await client.InstallAsync(Data.A, @"E:\Games");

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal($"/api/games/{Data.A}/install", handler.Requests[0].Url);
        Assert.Null(handler.Requests[0].Body);
        Assert.Contains(@"E:\\Games", handler.Requests[1].Body); // JSON escapes the backslash
        Assert.Contains("targetRoot", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Every_operation_goes_to_the_documented_address_with_the_documented_verb()
    {
        var download = """{"id":3,"contentHash":"aa","gameName":"G","state":"Paused","bytesDone":0,"bytesTotal":10,"percent":0,"speedBytesPerSecond":0,"peers":0,"etaSeconds":null,"error":null,"peerDetails":[],"kind":"Install"}""";
        var (client, handler) = Create((req, _) => req.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(download));

        await client.UpdateAsync(Data.A);
        await client.RepairAsync(Data.A);
        await client.PauseAsync(3);
        await client.ResumeAsync(3);
        await client.CancelAsync(3, deleteFiles: false);
        await client.CancelAsync(3, deleteFiles: true);

        Assert.Equal(
            [
                (HttpMethod.Post, $"/api/games/{Data.A}/update"),
                (HttpMethod.Post, $"/api/games/{Data.A}/repair"),
                (HttpMethod.Post, "/api/downloads/3/pause"),
                (HttpMethod.Post, "/api/downloads/3/resume"),
                (HttpMethod.Delete, "/api/downloads/3?deleteFiles=false"),
                (HttpMethod.Delete, "/api/downloads/3?deleteFiles=true"),
            ],
            handler.Requests.Select(r => (r.Method, r.Url)));
    }

    [Fact]
    public async Task Volatile_patterns_and_settings_are_sent_as_the_agent_expects()
    {
        var (client, handler) = Create((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/settings")
            ? Json("""{"gameRoots":["D:\\Games"],"seedingEnabled":true,"maxUploadMBps":80,"maxDownloadMBps":null}""")
            : Json("null"));

        await client.AddVolatileAsync(Data.A, ["saves/**", "*.ini"]);
        var saved = await client.SaveSettingsAsync(new SettingsDto([@"D:\Games"], true, 80, null));

        Assert.Equal($"/api/games/{Data.A}/volatile", handler.Requests[0].Url);
        Assert.Contains("saves/**", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Contains("\"maxUploadMBps\":80", handler.Requests[1].Body);
        Assert.Equal(80, saved.MaxUploadMBps);
    }

    [Fact]
    public async Task Starting_a_game_and_choosing_its_program_use_the_endpoints_the_agent_offers()
    {
        var (client, handler) = Create((req, _) => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/launch") => Json("""{"executablePath":"D:\\Games\\BeamNG\\Game.exe","arguments":"-windowed","workingDirectory":"D:\\Games\\BeamNG"}"""),
            var p when p.EndsWith("/executables") => Json("""["Launcher.exe","Bin64/Game.exe"]"""),
            _ => Json("null"),
        });

        var info = await client.LaunchAsync(Data.A);
        var programs = await client.GetExecutablesAsync(Data.A);
        await client.ChooseExecutableAsync(Data.A, "Bin64/Game.exe", "-windowed");

        Assert.Equal(("-windowed", @"D:\Games\BeamNG"), (info.Arguments, info.WorkingDirectory));
        Assert.Equal(["Launcher.exe", "Bin64/Game.exe"], programs);
        Assert.Equal(
            [(HttpMethod.Post, $"/api/games/{Data.A}/launch"), (HttpMethod.Get, $"/api/games/{Data.A}/executables"), (HttpMethod.Put, $"/api/games/{Data.A}/launcher")],
            handler.Requests.Select(r => (r.Method, r.Url)));
        Assert.Contains("Bin64/Game.exe", handler.Requests[2].Body);
    }

    [Fact]
    public async Task A_game_carries_whether_it_can_be_started_and_whether_it_runs()
    {
        var (client, _) = Create((_, _) => Json(
            $$"""[{"contentHash":"{{Data.A}}","gameId":"g","name":"BeamNG.drive","version":"0.38","totalSize":1,"state":"Installed","installPath":"D:/Games","peerNames":[],"downloadId":null,"definition":null,"launch":"NeedsExecutable","isRunning":true}]"""));

        var game = Assert.Single(await client.GetGamesAsync());

        Assert.Equal((LaunchState.NeedsExecutable, true), (game.Launch, game.IsRunning));
    }

    [Fact]
    public async Task The_trust_status_is_read_and_refreshing_asks_the_agent_to_load_the_list_again()
    {
        var (client, handler) = Create((_, _) => Json(
            """{"mode":"Require","source":"https://example.org/trust.json","hasList":true,"sequence":7,"issuedAt":"2026-09-21T10:00:00+00:00","validUntil":null,"verifiedCount":12,"revokedCount":1,"lastRefreshed":null,"lastError":null,"keyId":"0123456789abcdef"}"""));

        var status = await client.GetTrustAsync();
        await client.RefreshTrustAsync();

        Assert.Equal((TrustMode.Require, true, 7L, 12, 1), (status.Mode, status.HasList, status.Sequence, status.VerifiedCount, status.RevokedCount));
        Assert.Equal(
            [(HttpMethod.Get, "/api/trust"), (HttpMethod.Post, "/api/trust/refresh")],
            handler.Requests.Select(r => (r.Method, r.Url)));
    }

    [Fact]
    public async Task A_game_carries_what_the_administrators_list_says_about_it()
    {
        var (client, _) = Create((_, _) => Json(
            $$"""[{"contentHash":"{{Data.A}}","gameId":"g","name":"BeamNG.drive","version":"0.38","totalSize":1,"state":"AvailableOnLan","installPath":null,"peerNames":[],"downloadId":null,"definition":null,"trust":"Revoked","trustNote":"modified executable"}]"""));

        var game = Assert.Single(await client.GetGamesAsync());

        Assert.Equal(TrustVerdict.Revoked, game.Trust);
        Assert.Equal("modified executable", game.TrustNote);
    }

    [Fact]
    public async Task A_full_check_result_is_read()
    {
        var (client, _) = Create((_, _) => Json("""{"isIntact":false,"modified":["content/big.pak"],"missing":[],"added":["notes.txt"],"suggestedPatterns":["content/**","notes.txt"]}"""));

        var r = await client.CheckAsync(Data.A);

        Assert.False(r.IsIntact);
        Assert.Equal(["content/big.pak"], r.Modified);
        Assert.Equal(["content/**", "notes.txt"], r.SuggestedPatterns);
    }
}
