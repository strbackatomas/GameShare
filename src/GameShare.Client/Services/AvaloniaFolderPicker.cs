using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GameShare.Client.Services;

/// <summary>Never asks the OS for a folder. The default until a window exists to show a dialog on.</summary>
internal sealed class NoFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>Opens the real Windows folder picker on the app's window.</summary>
public sealed class AvaloniaFolderPicker : IFolderPicker
{
    private readonly Func<TopLevel?> _topLevel;

    /// <param name="topLevel">Looked up when a folder is actually picked, not at construction: the window may not exist yet.</param>
    public AvaloniaFolderPicker(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    public async Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        var top = _topLevel() ?? throw new InvalidOperationException("No window to show the folder picker on.");
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Vyber složku pro hry",
            AllowMultiple = false,
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
