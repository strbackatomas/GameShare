using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GameShare.Client.Services;

/// <summary>Never shows a dialog. The default until a window exists to show one on, and in tests.</summary>
internal sealed class NoFileDialogs : IFileDialogs
{
    public Task<bool> SaveAsync(string suggestedName, byte[] content, CancellationToken ct = default) => Task.FromResult(false);
    public Task<PickedFile?> OpenAsync(CancellationToken ct = default) => Task.FromResult<PickedFile?>(null);
}

/// <summary>The real Windows save and open dialogs, for the pairing backup.</summary>
public sealed class AvaloniaFileDialogs : IFileDialogs
{
    /// <summary>A backup is a few kilobytes. Anything much bigger is not one.</summary>
    private const int MaxBytes = 1024 * 1024;

    private static readonly FilePickerFileType BackupType = new("Záloha spárování GameShare") { Patterns = ["*.gsbackup"] };
    private readonly Func<TopLevel?> _topLevel;

    /// <param name="topLevel">Looked up when a dialog is actually shown, not at construction: the window may not exist yet.</param>
    public AvaloniaFileDialogs(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    public async Task<bool> SaveAsync(string suggestedName, byte[] content, CancellationToken ct = default)
    {
        var top = _topLevel() ?? throw new InvalidOperationException("No window to show the dialog on.");
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Uložit zálohu spárování",
            SuggestedFileName = suggestedName,
            DefaultExtension = "gsbackup",
            FileTypeChoices = [BackupType],
            ShowOverwritePrompt = true,
        });
        if (file is null) return false;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(content, ct);
        return true;
    }

    public async Task<PickedFile?> OpenAsync(CancellationToken ct = default)
    {
        var top = _topLevel() ?? throw new InvalidOperationException("No window to show the dialog on.");
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Obnovit spárování ze zálohy",
            AllowMultiple = false,
            FileTypeFilter = [BackupType, FilePickerFileTypes.All],
        });
        if (picked.Count == 0) return null;
        await using var stream = await picked[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > MaxBytes) throw new AgentException("Tenhle soubor je na zálohu spárování moc velký.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return new PickedFile(picked[0].Name, buffer.ToArray());
    }
}
