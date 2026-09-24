using Avalonia;

namespace GameShare.Client;

internal static class Program
{
    // Nothing that needs Avalonia may run before the framework is initialised, so keep this minimal.
    [STAThread]
    public static int Main(string[] args)
    {
        // Started again with administrator rights to run a game's preparation: do that, no window.
        if (Setup.SetupHost.TryRun(args) is { } exitCode) return exitCode;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the previewer and by the headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
