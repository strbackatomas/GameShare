using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameShare.Client.Services;
using GameShare.Client.ViewModels;
using GameShare.Client.Views;

namespace GameShare.Client;

public partial class App : Application
{
    /// <summary>The local agent's control API. Overridable with GAMESHARE_AGENT_URL for a non-default port.</summary>
    private static readonly Uri AgentUrl = new(Environment.GetEnvironmentVariable("GAMESHARE_AGENT_URL") ?? "http://127.0.0.1:47701");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow? window = null;
            var app = new AppModel(AgentClient.Create(AgentUrl), new AgentEventStream(AgentUrl), new AvaloniaDispatcher(),
                folderPicker: new AvaloniaFolderPicker(() => window)); // looked up when a folder is picked, the window does not exist yet here
            var main = new MainViewModel(app);
            window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
            desktop.Exit += async (_, _) => await app.DisposeAsync();

            // The window opens at once and fills in as the agent answers. A slow or missing agent never freezes it.
            _ = app.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
