using GameShare.Protocol;

namespace GameShare.AdminGui.Services;

/// <summary>
/// Remembers which key and which list the administrator last worked with, so they are not typed again every time
/// the tool is opened. Kept in the administrator's own profile, never on a share. Never the password.
/// </summary>
public static class LocalSettings
{
    /// <summary>Where this is kept on a real run. Tests pass their own path instead, so they never touch the real profile.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameShareAdmin", "gui-settings.json");

    public static GuiSettings Load(string path)
    {
        try { return GameShareJson.Deserialize<GuiSettings>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new GuiSettings(null, null);
        }
    }

    public static void Save(string path, GuiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, GameShareJson.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembering the paths is a minor inconvenience, never a reason to fail an action that already succeeded.
        }
    }
}
