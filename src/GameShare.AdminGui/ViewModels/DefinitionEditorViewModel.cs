using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.AdminGui.ViewModels;

/// <summary>
/// Edits a game folder's gameshare.json: which programs start it, and what preparing a PC for it does. Only offers what is in the
/// folder, checks the result the way the agents will, and keeps whatever the file had that the form does not show (volatile patterns,
/// an icon). Signing happens when the folder is scanned and added to the list, the definition is signed together with the files.
/// </summary>
public sealed partial class DefinitionEditorViewModel : ObservableObject
{
    private readonly string _folder;
    private readonly GameDefinition _original;

    private DefinitionEditorViewModel(string folder, GameDefinition original, IReadOnlyList<string> files, IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> redists)
    {
        _folder = folder;
        _original = original;
        Programs = DefinitionChecks.Programs(files);
        RegFiles = DefinitionChecks.RegFiles(files);
        Installers = DefinitionChecks.Installers(files);
        Folders = folders;

        GameId = original.GameId;
        Name = original.Name;
        GameVersion = original.Version ?? "";
        foreach (var e in original.LaunchEntries()) Launch.Add(new LaunchRow(this) { Name = e.Name ?? "", Executable = e.Executable, Arguments = e.Arguments ?? "", WorkingDirectory = e.WorkingDirectory, RunAsAdmin = e.RunAsAdmin });
        if (Launch.Count == 0) AddLaunch();

        var setup = original.Setup ?? new GameSetup();
        foreach (var (id, name) in redists) Requires.Add(new RequireRow(id, name) { IsChecked = setup.Requires.Contains(id, StringComparer.OrdinalIgnoreCase) });
        foreach (var id in setup.Requires.Where(id => !redists.ContainsKey(id))) Requires.Add(new RequireRow(id, $"{id} (balíček vedle hry ho nenabízí)") { IsChecked = true });
        foreach (var r in setup.Redist) GameRedists.Add(new RedistRow(this) { File = r.File, Args = r.Args ?? "" });
        foreach (var r in setup.Registry) Registry.Add(new RegistryRow(this) { File = r.File, Cleanup = r.Cleanup ?? "", OriginalPath = r.OriginalPath ?? "" });
        foreach (var c in setup.Compatibility) Compatibility.Add(new CompatibilityRow(this) { Executable = c.Executable, Layers = c.Layers });
        foreach (var p in setup.Profile) Profile.Add(new ProfileRow(this) { From = p.From, To = p.To });
    }

    /// <summary>Opens the folder's gameshare.json, or starts a new one named after the folder.</summary>
    /// <exception cref="InvalidDataException">The file exists but cannot be read. The message names the file.</exception>
    public static async Task<DefinitionEditorViewModel> OpenAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"'{folder}' is not a folder.");
        var name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\'));
        var definition = await GameDefinitionFile.TryLoadAsync(folder, ct).ConfigureAwait(false)
            ?? new GameDefinition { GameId = GameDefinitionFile.SlugFromFolderName(name), Name = name };
        var files = await Task.Run(() => DefinitionChecks.Files(folder), ct).ConfigureAwait(false);
        var redists = await DefinitionChecks.KnownRedistsAsync(folder, ct).ConfigureAwait(false);
        return new DefinitionEditorViewModel(folder, definition, files, DefinitionChecks.TopFolders(folder), redists);
    }

    public string Folder => _folder;

    public IReadOnlyList<string> Programs { get; }
    public IReadOnlyList<string> RegFiles { get; }
    public IReadOnlyList<string> Installers { get; }
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<string> ProfileTargets { get; } = SetupPlanner.ProfileTokens;

    [ObservableProperty] public partial string GameId { get; set; } = "";
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string GameVersion { get; set; } = "";

    public ObservableCollection<LaunchRow> Launch { get; } = [];
    public ObservableCollection<RequireRow> Requires { get; } = [];
    public ObservableCollection<RedistRow> GameRedists { get; } = [];
    public ObservableCollection<RegistryRow> Registry { get; } = [];
    public ObservableCollection<CompatibilityRow> Compatibility { get; } = [];
    public ObservableCollection<ProfileRow> Profile { get; } = [];

    public bool HasRegFiles => RegFiles.Count > 0;
    public bool HasInstallers => Installers.Count > 0;
    public bool HasRequires => Requires.Count > 0;

    /// <summary>What an agent would refuse, from the last save. Empty after a clean save.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty] public partial string Status { get; set; } = "";

    [RelayCommand] private void AddLaunch() => Launch.Add(new LaunchRow(this) { Executable = Programs.FirstOrDefault() ?? "" });
    [RelayCommand] private void AddRedist() => GameRedists.Add(new RedistRow(this) { File = Installers.FirstOrDefault() ?? "" });
    [RelayCommand] private void AddRegistry() => Registry.Add(new RegistryRow(this) { File = RegFiles.FirstOrDefault() ?? "", OriginalPath = @"C:\Games\" + Path.GetFileName(_folder.TrimEnd('\\')) });
    [RelayCommand] private void AddCompatibility() => Compatibility.Add(new CompatibilityRow(this) { Executable = Launch.FirstOrDefault()?.Executable ?? Programs.FirstOrDefault() ?? "", Layers = "WINXPSP3" });
    [RelayCommand] private void AddProfile() => Profile.Add(new ProfileRow(this) { From = Folders.FirstOrDefault(f => f.EndsWith("-profile", StringComparison.OrdinalIgnoreCase)) ?? Folders.FirstOrDefault() ?? "", To = @"{Documents}\" + Name });

    internal void Remove(object row)
    {
        switch (row)
        {
            case LaunchRow r: Launch.Remove(r); break;
            case RedistRow r: GameRedists.Remove(r); break;
            case RegistryRow r: Registry.Remove(r); break;
            case CompatibilityRow r: Compatibility.Remove(r); break;
            case ProfileRow r: Profile.Remove(r); break;
        }
    }

    internal void MoveUp(LaunchRow row)
    {
        var i = Launch.IndexOf(row);
        if (i > 0) Launch.Move(i, i - 1);
    }

    /// <summary>The definition as the form shows it, on top of what the file had.</summary>
    public GameDefinition Build()
    {
        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var setup = new GameSetup
        {
            Requires = Requires.Where(r => r.IsChecked).Select(r => r.Id).ToList(),
            Redist = GameRedists.Where(r => Blank(r.File) is not null).Select(r => new RedistStep { File = r.File.Trim(), Args = Blank(r.Args) }).ToList(),
            Registry = Registry.Where(r => Blank(r.File) is not null)
                .Select(r => new RegistryStep { File = r.File.Trim(), Cleanup = Blank(r.Cleanup), OriginalPath = Blank(r.OriginalPath) }).ToList(),
            Compatibility = Compatibility.Where(c => Blank(c.Executable) is not null)
                .Select(c => new CompatibilityStep { Executable = c.Executable.Trim(), Layers = c.Layers.Trim() }).ToList(),
            Profile = Profile.Where(p => Blank(p.From) is not null).Select(p => new ProfileStep { From = p.From.Trim(), To = p.To.Trim() }).ToList(),
        };
        return _original with
        {
            GameId = GameId.Trim(),
            Name = Name.Trim(),
            Version = Blank(GameVersion),
            // The list replaces the old single-program fields, so there is one place that says what starts the game.
            Executable = null, Arguments = null, WorkingDirectory = ".",
            Launch = Launch.Where(l => Blank(l.Executable) is not null).Select(l => new LaunchEntry
            {
                Name = Blank(l.Name), Executable = l.Executable.Trim(), Arguments = Blank(l.Arguments),
                WorkingDirectory = Blank(l.WorkingDirectory) ?? ".", RunAsAdmin = l.RunAsAdmin,
            }).ToList(),
            Setup = setup.IsEmpty ? null : setup,
        };
    }

    /// <summary>Checks and writes gameshare.json. A definition an agent would refuse is not written, the problems say why.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var definition = Build();
        var problems = await DefinitionChecks.ValidateAsync(definition, _folder).ConfigureAwait(true);
        Problems.Clear();
        foreach (var p in problems) Problems.Add(p);
        if (problems.Count > 0)
        {
            Status = "Neuloženo, agenti by tohle odmítli:";
            return;
        }
        await GameDefinitionFile.WriteAsync(_folder, definition).ConfigureAwait(true);
        Status = "Uloženo do gameshare.json. Aby to platilo i u hráčů s povinným ověřením, naskenuj složku a přidej ji do seznamu, tím se definice podepíše.";
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>After a clean save, so the window can offer to scan and sign right away.</summary>
    public event EventHandler? Saved;
}

public sealed partial class LaunchRow(DefinitionEditorViewModel editor) : ObservableObject
{
    public DefinitionEditorViewModel Editor => editor;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Executable { get; set; } = "";
    [ObservableProperty] public partial string Arguments { get; set; } = "";
    [ObservableProperty] public partial string WorkingDirectory { get; set; } = ".";
    [ObservableProperty] public partial bool RunAsAdmin { get; set; }

    /// <summary>A new program starts in its own folder, the way a shortcut to it would.</summary>
    partial void OnExecutableChanged(string value)
    {
        if (WorkingDirectory is "" or "." && value.Contains('/')) WorkingDirectory = value[..value.LastIndexOf('/')];
    }

    [RelayCommand] private void Remove() => editor.Remove(this);
    [RelayCommand] private void MoveUp() => editor.MoveUp(this);
}

public sealed partial class RequireRow(string id, string name) : ObservableObject
{
    public string Id => id;
    public string Text => name == id ? id : $"{name} ({id})";
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

public sealed partial class RedistRow(DefinitionEditorViewModel editor) : ObservableObject
{
    public DefinitionEditorViewModel Editor => editor;
    [ObservableProperty] public partial string File { get; set; } = "";
    [ObservableProperty] public partial string Args { get; set; } = "";
    [RelayCommand] private void Remove() => editor.Remove(this);
}

public sealed partial class RegistryRow(DefinitionEditorViewModel editor) : ObservableObject
{
    public DefinitionEditorViewModel Editor => editor;
    [ObservableProperty] public partial string File { get; set; } = "";
    [ObservableProperty] public partial string Cleanup { get; set; } = "";
    [ObservableProperty] public partial string OriginalPath { get; set; } = "";
    [RelayCommand] private void Remove() => editor.Remove(this);
}

public sealed partial class CompatibilityRow(DefinitionEditorViewModel editor) : ObservableObject
{
    public DefinitionEditorViewModel Editor => editor;
    [ObservableProperty] public partial string Executable { get; set; } = "";
    [ObservableProperty] public partial string Layers { get; set; } = "WINXPSP3";
    [RelayCommand] private void Remove() => editor.Remove(this);
}

public sealed partial class ProfileRow(DefinitionEditorViewModel editor) : ObservableObject
{
    public DefinitionEditorViewModel Editor => editor;
    [ObservableProperty] public partial string From { get; set; } = "";
    [ObservableProperty] public partial string To { get; set; } = "";
    [RelayCommand] private void Remove() => editor.Remove(this);
}
