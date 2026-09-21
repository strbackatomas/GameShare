using System.Text.Json;

namespace GameShare.Protocol;

/// <summary>One JSON configuration for everything that crosses a process or PC boundary.</summary>
public static class GameShareJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, // "Installed", not 0
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"JSON is null, expected {typeof(T).Name}.");
}
