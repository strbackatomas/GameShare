using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GameShare.AdminGui.Services;

/// <summary>Opens the real Windows dialogs on the app's window.</summary>
public sealed class AvaloniaPickers : IFolderPicker, IFilePicker
{
    private readonly Func<TopLevel?> _topLevel;

    /// <param name="topLevel">Looked up when something is actually picked, not at construction: the window may not exist yet.</param>
    public AvaloniaPickers(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    public async Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
    {
        var top = Top();
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExistingFileAsync(string title, CancellationToken ct = default)
    {
        var top = Top();
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileToSaveAsync(string title, string suggestedName, CancellationToken ct = default)
    {
        var top = Top();
        var picked = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
        return picked?.TryGetLocalPath();
    }

    private TopLevel Top() => _topLevel() ?? throw new InvalidOperationException("No window to show the dialog on.");
}
