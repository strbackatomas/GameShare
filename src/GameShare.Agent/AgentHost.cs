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
    /// <param name="configureServices">Applied after the agent's own services, so a host can replace one, such as the <see cref="IAppUpdateApplier"/>.</param>
    public static async Task<WebApplication> BuildAsync(
        string[] args, Action<AgentOptions>? configure = null, CancellationToken ct = default, Action<IServiceCollection>? configureServices = null)
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
            // Always Debug: cheap on its own, the actual noise (extra libtorrent notification categories) is gated by
            // Settings.TorrentDebugLogging when the transfer engine is built, below.
            .MinimumLevel.Override("GameShare.Torrent", LogEventLevel.Debug)
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
        var remote = await RemoteAccessService.LoadAsync(db, machineId, ct);
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
        services.AddSingleton(remote);

        services.AddSingleton(sp => new TorrentEngine(new TorrentEngineOptions
        {
            ListenPort = options.TorrentPort,
            LanOnly = options.LanOnly,
            AllowMultipleConnectionsPerIp = options.AllowMultipleConnectionsPerIp,
            OpenFileLimit = options.OpenFileLimit,
            IdleReleaseAfter = options.SeedIdleRelease,
            MaxUploadBytesPerSecond = SettingsService.ToBytesPerSecond(settings.Current.MaxUploadMBps),
            MaxDownloadBytesPerSecond = SettingsService.ToBytesPerSecond(settings.Current.MaxDownloadMBps),
            DebugLogging = settings.Current.TorrentDebugLogging,
            Tuning = settings.Current.Tuning,
        }, sp.GetRequiredService<ILogger<TorrentEngine>>()));

        services.AddSingleton<GameLibrary>();
        services.AddSingleton<SourceLibrary>();
        services.AddSingleton<SeedManager>();
        services.AddSingleton(sp => new GameChangeTracker(
            sp.GetRequiredService<GameShareDb>(), sp.GetRequiredService<GameLibrary>(), sp.GetRequiredService<ILogger<GameChangeTracker>>(),
            options.ChangeQuietPeriod));
        services.AddSingleton(sp => new DownloadManager(
            sp.GetRequiredService<TorrentEngine>(), sp.GetRequiredService<GameShareDb>(), sp.GetRequiredService<SeedManager>(),
            sp.GetRequiredService<ILogger<DownloadManager>>(),
            new DownloadManagerOptions { TickInterval = options.DownloadTickInterval, ResumeSaveInterval = options.ResumeSaveInterval }));

        services.AddSingleton<IDatagramTransport>(sp => new UdpDatagramTransport(options.DiscoveryPort,
            logger: sp.GetRequiredService<ILogger<UdpDatagramTransport>>(),
            unignoredAdapterIds: () => new HashSet<string>(settings.Current.AllowedVirtualAdapterIds ?? [], StringComparer.Ordinal)));
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
        services.AddSingleton<IconService>();
        services.AddSingleton<GameShare.Storage.ISetupProbe, WindowsSetupProbe>();
        services.AddSingleton<DefinitionSync>();
        services.AddSingleton<SetupService>();

        // The only request that may leave the LAN: a small download of the administrator's signed list. It carries nothing about this PC,
        // and the list is only used when its signature verifies. Off unless the administrator turned it on.
        services.AddHttpClient("trust", c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton(sp => new TrustService(
            options, sp.GetRequiredService<IHttpClientFactory>().CreateClient("trust"), sp.GetRequiredService<ILogger<TrustService>>()));

        // GameShare's own releases: a small signed description, then the package's zip unless the LAN has it. Carries nothing about this PC.
        // Redirects are followed, GitHub's download links are one. Reading the zip has its own stall timeout.
        services.AddHttpClient("update", c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<IAppUpdateApplier, ServiceUpdateApplier>();
        services.AddSingleton(sp => new AppUpdateService(
            options, sp.GetRequiredService<IHttpClientFactory>().CreateClient("update"), sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<TorrentEngine>(), sp.GetRequiredService<DiscoveryService>(), sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IAppUpdateApplier>(), sp.GetRequiredService<ILogger<AppUpdateService>>()));

        // Remote management: its own TLS listener, opened only while turned on, and the client side that passes requests to paired PCs.
        // Calls this PC's own control API over loopback, like the local client does.
        services.AddHttpClient("remote-local").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        });
        services.AddSingleton<IWakeOnLan, WakeOnLan>();
        services.AddSingleton<RemoteAccessService>();
        services.AddSingleton<NetworkCheck>();
        services.AddSingleton<IAgentRestarter, AgentRestarter>();

        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Order matters: the bridge subscribes before the worker starts producing events.
        services.AddHostedService<EventBridge>();
        services.AddHostedService<AgentWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<RemoteAccessService>());
        configureServices?.Invoke(services);

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<AccessGuard>();
        app.MapHub<EventsHub>(Protocol.GameShareEvents.HubPath);
        LocalApi.Map(app);
        RemoteApi.Map(app);
        PeerApi.Map(app);
        return app;
    }
}
