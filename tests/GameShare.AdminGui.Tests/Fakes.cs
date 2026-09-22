using GameShare.AdminGui.Services;

namespace GameShare.AdminGui.Tests;

/// <summary>Answers with preset paths, or null for a cancelled dialog. Records how many times each was opened.</summary>
internal sealed class FakePickers : IFolderPicker, IFilePicker
{
    public string? NextFolder { get; set; }
    public string? NextExistingFile { get; set; }
    public string? NextSaveFile { get; set; }

    public int FolderCalls { get; private set; }
    public int ExistingFileCalls { get; private set; }
    public int SaveFileCalls { get; private set; }

    public Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
    {
        FolderCalls++;
        return Task.FromResult(NextFolder);
    }

    public Task<string?> PickExistingFileAsync(string title, CancellationToken ct = default)
    {
        ExistingFileCalls++;
        return Task.FromResult(NextExistingFile);
    }

    public Task<string?> PickFileToSaveAsync(string title, string suggestedName, CancellationToken ct = default)
    {
        SaveFileCalls++;
        return Task.FromResult(NextSaveFile);
    }
}
