using System.Net;
using System.Text.Json.Serialization;
using GameShare.Core;
using GameShare.Core.Data;
using GameShare.Discovery;
using GameShare.Protocol;
using GameShare.Torrent;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Serilog.Events;

namespace GameShare.Agent;

/// <summary>The composition root. Builds a fully wired agent, used by Program and by tests that run several agents in one process.</summary>
public static class AgentHost
{
    public const string ServiceName = "GameShare Agent";

    /// <param name="configure">Applied after appsettings and environment variables. Tests use it to pick ports and folders.</param>
    public static async Task<WebApplication> BuildAsync(string[] args, Action<AgentOptions>? configure = null, CancellationToken ct = default)
    {
        // ContentRoot is the exe folder, so appsettings.json is found when running as a service (whose working directory is System32).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

        var options = new AgentOptions();
        builder.Configuration.GetSection(AgentOptions.Section).Bind(options);
        configure?.Invoke(options);
        options.Validate();

        var dataDir = options.ResolveDataDir();
        Directory.CreateDirectory(dataDir);

        builder.Host.UseSerilog((_, cfg) => cfg
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(dataDir, "logs", "agent-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}"));

        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName); // does nothing when started from a console

        // State that must exist before the services do: the database, the machine identity and the saved settings.
        var db = await GameShareDb.OpenAsync(Path.Combine(dataDir, "gameshare.db"), cancellationToken: ct);
        var machineId = await db.GetSettingAsync("machine.id", ct);
        if (machineId is null)
        {
            machineId = Guid.NewGuid().ToString("N");
            await db.SetSettingAsync("machine.id", machineId, ct);
        }
        var settings = await SettingsService.LoadAsync(db, options.InitialGameRoots, ct);
        var identity = new AgentIdentity(machineId, options.ResolveMachineName());

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, options.LocalApiPort);          // control API and events: this machine only
            k.Listen(IPAddress.Any, options.PeerApiPort, l => l.Protocols = HttpProtocols.Http1); // read-only API for other PCs
        });

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(identity);
        services.AddSingleton(db);
        services.AddSingleton(settings);

        services.AddSingleton(sp => new TorrentEngine(new TorrentEngineOptions
        {
            ListenPort = options.TorrentPort,
            LanOnly = options.LanOnly,
            AllowMultipleConnectionsPerIp = options.AllowMultipleConnectionsPerIp,
            OpenFileLimit = options.OpenFileLimit,
            IdleReleaseAfter = options.SeedIdleRelease,
            MaxUploadBytesPerSecond = SettingsService.ToBytesPerSecond(settings.Current.MaxUploadMBps),
            MaxDownloadBytesPerSecond = SettingsService.ToBytesPerSecond(settings.Current.MaxDownloadMBps),
        }, sp.GetRequiredService<ILogger<TorrentEngine>>()));

        services.AddSingleton<GameLibrary>();
        services.AddSingleton<SeedManager>();
        services.AddSingleton(sp => new GameChangeTracker(
            sp.GetRequiredService<GameShareDb>(), sp.GetRequiredService<GameLibrary>(), sp.GetRequiredService<ILogger<GameChangeTracker>>(),
            options.ChangeQuietPeriod));
        services.AddSingleton(sp => new DownloadManager(
            sp.GetRequiredService<TorrentEngine>(), sp.GetRequiredService<GameShareDb>(), sp.GetRequiredService<SeedManager>(),
            sp.GetRequiredService<ILogger<DownloadManager>>(),
            new DownloadManagerOptions { TickInterval = options.DownloadTickInterval, ResumeSaveInterval = options.ResumeSaveInterval }));

        services.AddSingleton<IDatagramTransport>(sp => new UdpDatagramTransport(options.DiscoveryPort, logger: sp.GetRequiredService<ILogger<UdpDatagramTransport>>()));
        services.AddSingleton(sp => new DiscoveryService(
            new DiscoveryOptions
            {
                MachineId = identity.MachineId,
                MachineName = identity.MachineName,
                AgentPort = options.PeerApiPort, // peers reach our read-only API here
                AppVersion = AppVersion.Current,
                HelloInterval = options.HelloInterval,
                PeerTimeout = options.PeerTimeout,
            },
            sp.GetRequiredService<IDatagramTransport>(), sp.GetRequiredService<ILogger<DiscoveryService>>()));

        services.AddHttpClient("peer").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a peer must not bounce us somewhere else
            UseProxy = false,          // LAN traffic never goes through a proxy
            ConnectTimeout = TimeSpan.FromSeconds(3),
        });
        services.AddSingleton<PeerCatalog>();
        services.AddSingleton<GameView>();
        services.AddSingleton<ScanService>();
        services.AddSingleton<RunningGames>();
        services.AddSingleton<LaunchService>();

        // The only request that may leave the LAN: a small download of the administrator's signed list. It carries nothing about this PC,
        // and the list is only used when its signature verifies. Off unless the administrator turned it on.
        services.AddHttpClient("trust", c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton(sp => new TrustService(
            options, sp.GetRequiredService<IHttpClientFactory>().CreateClient("trust"), sp.GetRequiredService<ILogger<TrustService>>()));

        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Order matters: the bridge subscribes before the worker starts producing events.
        services.AddHostedService<EventBridge>();
        services.AddHostedService<AgentWorker>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<AccessGuard>();
        app.MapHub<EventsHub>(Protocol.GameShareEvents.HubPath);
        LocalApi.Map(app);
        PeerApi.Map(app);
        return app;
    }
}
