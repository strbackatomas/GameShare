using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameShare.Client.ViewModels;

/// <summary>One entry of the navigation bar.</summary>
public sealed partial class NavItem(string title, ViewModelBase page, Func<int>? badge = null) : ObservableObject
{
    public string Title { get; } = title;
    public ViewModelBase Page { get; } = page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;
    public void Refresh() { if (badge is not null) Badge = badge(); }
}

public sealed partial class MainViewModel : ViewModelBase
{
    public MainViewModel(AppModel app)
    {
        App = app;
        Library = new LibraryViewModel(app);
        Downloads = new DownloadsViewModel(app);
        Network = new NetworkViewModel(app);
        Settings = new SettingsViewModel(app);
        Log = new LogViewModel(app);

        Items =
        [
            new NavItem("Knihovna", Library),
            new NavItem("Přenosy", Downloads, () => app.ActiveDownloadCount),
            new NavItem("Síť", Network, () => app.Peers.Count),
            new NavItem("Nastavení", Settings),
            new NavItem("Protokol", Log),
        ];
        SelectedItem = Items[0];

        // Badges follow the counts they show.
        app.Downloads.CollectionChanged += (_, _) => RefreshBadges();
        app.Peers.CollectionChanged += (_, _) => RefreshBadges();
        app.PropertyChanged += (_, _) => RefreshBadges();
        RefreshBadges();
    }

    public AppModel App { get; }
    public LibraryViewModel Library { get; }
    public DownloadsViewModel Downloads { get; }
    public NetworkViewModel Network { get; }
    public SettingsViewModel Settings { get; }
    public LogViewModel Log { get; }
    public ObservableCollection<NavItem> Items { get; }

    [ObservableProperty] public partial NavItem SelectedItem { get; set; }

    /// <summary>Settings, the log and uploads are read from the agent when their page is opened, so they are never stale.</summary>
    partial void OnSelectedItemChanged(NavItem value)
    {
        if (value.Page == Settings) _ = Settings.LoadAsync();
        else if (value.Page == Log) _ = Log.LoadAsync();
        else if (value.Page == Downloads) _ = Downloads.LoadUploadsAsync();
    }

    private void RefreshBadges()
    {
        foreach (var item in Items) item.Refresh();
    }
}
