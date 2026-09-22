namespace GameShare.AdminGui.Services;

/// <summary>Lets the view model ask for a folder without depending on Avalonia, so it can be tested without a display.</summary>
public interface IFolderPicker
{
    /// <returns>The chosen folder, or null when the administrator cancelled.</returns>
    Task<string?> PickFolderAsync(string title, CancellationToken ct = default);
}

/// <summary>Lets the view model ask for a file, existing or new, without depending on Avalonia.</summary>
public interface IFilePicker
{
    /// <returns>The chosen file, or null when the administrator cancelled.</returns>
    Task<string?> PickExistingFileAsync(string title, CancellationToken ct = default);

    /// <summary>A file to write to: an existing one to reuse, or a new name in an existing folder.</summary>
    Task<string?> PickFileToSaveAsync(string title, string suggestedName, CancellationToken ct = default);
}

/// <summary>What is remembered between runs, in the administrator's own profile. Never the password.</summary>
public sealed record GuiSettings(string? PrivateKeyPath, string? ListPath);
