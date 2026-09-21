using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameShare.Agent;
using Microsoft.AspNetCore.Builder;
using GameShare.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace GameShare.Agent.Tests;

/// <summary>A complete agent running in this process on its own ports and folders, talking to real sockets.</summary>
internal sealed class TestAgent : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private WebApplication _app = null!;
    private readonly Action<AgentOptions> _configure;

    public string Name { get; }
    public string Dir { get; }

    /// <summary>The agent's own services, for a test that has to look at what the API does not show.</summary>
    public IServiceProvider Services => _app.Services;
    public string GamesRoot { get; }
    public int LocalPort { get; }
    public int PeerPort { get; }
    public HttpClient Api { get; }
    public HttpClient PeerApi { get; }

    private TestAgent(string name, string dir, int discoveryPort, Action<AgentOptions>? tweak)
    {
        Name = name;
        Dir = dir;
        GamesRoot = Path.Combine(dir, "Games");
        Directory.CreateDirectory(GamesRoot);
        LocalPort = FreePort();
        PeerPort = FreePort();
        int torrentPort = FreePort();

        _configure = o =>
        {
            o.DataDir = Path.Combine(dir, "data");
            o.MachineName = name;
            o.LocalApiPort = LocalPort;
            o.PeerApiPort = PeerPort;
            o.DiscoveryPort = discoveryPort;
            o.TorrentPort = torrentPort;
            o.AllowMultipleConnectionsPerIp = true; // several agents share this machine's IP address
            o.HelloInterval = TimeSpan.FromMilliseconds(300);
            o.PeerTimeout = TimeSpan.FromMilliseconds(1500);
            o.CatalogRefreshInterval = TimeSpan.FromMilliseconds(500);
            o.DownloadTickInterval = TimeSpan.FromMilliseconds(100);
            o.ResumeSaveInterval = TimeSpan.FromMilliseconds(500);
            o.RescanInterval = TimeSpan.FromHours(1);
            o.InitialGameRoots = [GamesRoot];
            tweak?.Invoke(o);
        };
        Api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{LocalPort}") };
        PeerApi = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{PeerPort}") };
    }

    /// <param name="existingDir">Restart an agent on the data it had before.</param>
    /// <param name="preloadGame">Put a fake game in the game folder before the agent starts, so its first scan finds it.</param>
    public static async Task<TestAgent> StartAsync(
        string name, int discoveryPort, bool preloadGame = false, long bigFileBytes = 20_000_000, string? existingDir = null,
        Action<AgentOptions>? tweak = null, Action<string>? customiseGame = null)
    {
        var agent = new TestAgent(name, existingDir ?? TestGame.NewTempDir(), discoveryPort, tweak);
        if (preloadGame)
        {
            using var template = new TestGame(largeFileBytes: bigFileBytes);
            var target = Path.Combine(agent.GamesRoot, "TestGame");
            TestGame.CopyDirectory(template.GameDir, target);
            customiseGame?.Invoke(target); // for example a program to start, and a definition that names it
        }
        await agent.BootAsync();
        return agent;
    }

    private async Task BootAsync()
    {
        _app = await AgentHost.BuildAsync([], _configure);
        await _app.StartAsync();
    }

    /// <summary>Graceful stop, like stopping the Windows service. Sends a goodbye and saves resume data.</summary>
    public async Task StopAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public static int DiscoveryPort() => Random.Shared.Next(41000, 49000);

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---- typed helpers over the control API ----

    public async Task<T> GetAsync<T>(string path) =>
        (await Api.GetFromJsonAsync<T>(path, Json))!;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await Api.SendAsync(request);
    }

    public Task<List<GameDto>> GamesAsync() => GetAsync<List<GameDto>>("/api/games");
    public Task<List<PeerDto>> PeersAsync() => GetAsync<List<PeerDto>>("/api/peers");

    public async Task<GameDto> WaitForGameAsync(Func<GameDto, bool> predicate, string what, int timeoutMs = 20_000)
    {
        GameDto? found = null;
        await Poll.UntilAsync(async () => (found = (await GamesAsync()).FirstOrDefault(predicate)) is not null, what, timeoutMs);
        return found!;
    }

    public async Task<DownloadDto> WaitForDownloadAsync(long id, string state, int timeoutMs = 60_000)
    {
        DownloadDto? d = null;
        await Poll.UntilAsync(async () => (d = await GetAsync<DownloadDto>($"/api/downloads/{id}")).State == state, $"{Name} download {id} to reach {state}", timeoutMs);
        return d!;
    }

    public async Task<DownloadDto> InstallAsync(string contentHash)
    {
        var response = await SendAsync(HttpMethod.Post, $"/api/games/{contentHash}/install");
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"install answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DownloadDto>(Json))!;
    }

    public async Task SetSettingsAsync(SettingsDto s)
    {
        var response = await SendAsync(HttpMethod.Put, "/api/settings", s);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public string InstalledPath => Path.Combine(GamesRoot, "TestGame");

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch (ObjectDisposedException) { /* already stopped by the test */ }
        Api.Dispose();
        PeerApi.Dispose();
        SqliteConnection.ClearAllPools();
        TestGame.DeleteQuietly(Dir);
    }
}

/// <summary>Everything the agent pushes over SignalR, in order.</summary>
internal sealed class EventRecorder : IAsyncDisposable
{
    private readonly HubConnection _connection;
    public ConcurrentQueue<(string Name, object Payload)> Events { get; } = new();

    private EventRecorder(HubConnection connection) => _connection = connection;

    public static async Task<EventRecorder> ConnectAsync(TestAgent agent)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{agent.LocalPort}{GameShareEvents.HubPath}")
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        var recorder = new EventRecorder(connection);

        void Listen<T>(string name) where T : notnull => connection.On<T>(name, p => recorder.Events.Enqueue((name, p)));
        Listen<PeerDto>(GameShareEvents.PeerConnected);
        Listen<PeerDto>(GameShareEvents.PeerDisconnected);
        Listen<GameDto>(GameShareEvents.GameDiscovered);
        Listen<GameDto>(GameShareEvents.GameUpdated);
        Listen<GameDto>(GameShareEvents.GameRemoved);
        Listen<DownloadDto>(GameShareEvents.DownloadStarted);
        Listen<DownloadDto>(GameShareEvents.DownloadProgress);
        Listen<DownloadDto>(GameShareEvents.DownloadPaused);
        Listen<DownloadDto>(GameShareEvents.DownloadCompleted);
        Listen<DownloadDto>(GameShareEvents.DownloadFailed);
        Listen<DownloadDto>(GameShareEvents.DownloadCancelled);
        Listen<SeedDto>(GameShareEvents.SeedStarted);
        Listen<SeedDto>(GameShareEvents.SeedStopped);

        await connection.StartAsync();
        return recorder;
    }

    public IEnumerable<string> Names => Events.Select(e => e.Name);
    public IEnumerable<T> Of<T>(string name) => Events.Where(e => e.Name == name).Select(e => (T)e.Payload);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

internal static class Poll
{
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(50);
        }
    }

    public static Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 20_000) =>
        UntilAsync(() => Task.FromResult(condition()), what, timeoutMs);
}
