using System.Text.Json;
using System.Text.RegularExpressions;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>Reads the optional <c>gameshare.json</c> from a game root.</summary>
public static partial class GameDefinitionFile
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex GameIdPattern();

    public static bool IsValidGameId(string id) => GameIdPattern().IsMatch(id);

    /// <summary>Lowercase slug for a folder name, used when there is no definition file.</summary>
    public static string SlugFromFolderName(string folderName)
    {
        var slug = Regex.Replace(folderName.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.', '_');
        return slug.Length == 0 ? "game" : slug.Length > 64 ? slug[..64] : slug;
    }

    private static readonly JsonSerializerOptions WriteOptions = new(GameShareJson.Options)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps Czech letters readable instead of \u escapes, the file is edited by hand
    };

    /// <summary>
    /// Writes the definition into a game folder, so the folder carries it: a later scan, or another PC that installs from this one, reads it back.
    /// The file is not part of the game's content, so this never changes the game's identity.
    /// </summary>
    public static async Task WriteAsync(string gameDirectory, GameDefinition definition, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(gameDirectory, ContentScanner.DefinitionFileName);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(definition, WriteOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <returns>The definition, or null when the directory has no definition file.</returns>
    /// <exception cref="InvalidDataException">The file exists but is malformed. The message names the file.</exception>
    public static async Task<GameDefinition?> TryLoadAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(gameDirectory, ContentScanner.DefinitionFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var def = GameShareJson.Deserialize<GameDefinition>(json);
            if (!IsValidGameId(def.GameId))
                throw new InvalidDataException($"Invalid gameId '{def.GameId}' in {path}. Use lowercase letters, digits, '.', '_' or '-'.");
            VolatileMatcher.Create(def.Volatile); // fails with a message naming the bad pattern
            return def;
        }
        catch (InvalidDataException ex) when (!ex.Message.Contains(path, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{ex.Message} (in {path})", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Cannot parse game definition {path}: {ex.Message}", ex);
        }
    }
}
