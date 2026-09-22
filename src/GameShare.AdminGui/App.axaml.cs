using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameShare.AdminGui.Services;
using GameShare.AdminGui.ViewModels;
using GameShare.AdminGui.Views;

namespace GameShare.AdminGui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow? window = null;
            var pickers = new AvaloniaPickers(() => window); // looked up when something is picked, the window does not exist yet here
            var main = new MainViewModel(pickers, pickers);
            window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
