using Avalonia;

namespace GameShare.Client;

internal static class Program
{
    // Nothing that needs Avalonia may run before the framework is initialised, so keep this minimal.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the previewer and by the headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
