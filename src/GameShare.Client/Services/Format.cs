using System.Globalization;

namespace GameShare.Client.Services;

/// <summary>Turns numbers into the short readable text the interface shows. Czech decimal comma, binary units like Explorer.</summary>
public static class Format
{
    private static readonly CultureInfo Cz = CultureInfo.GetCultureInfo("cs-CZ");

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = Math.Max(0, bytes);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} B" : string.Create(Cz, $"{v:0.#} {units[i]}");
    }

    public static string Speed(long bytesPerSecond) => bytesPerSecond <= 0 ? "" : Size(bytesPerSecond) + "/s";

    /// <returns>For example "2 min 10 s", or an empty string when unknown.</returns>
    public static string Eta(double? seconds)
    {
        if (seconds is null or < 0 or double.NaN or double.PositiveInfinity) return "";
        var t = TimeSpan.FromSeconds(Math.Ceiling(seconds.Value));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    public static string Percent(double percent) => string.Create(Cz, $"{Math.Clamp(percent, 0, 100):0.#} %");

    /// <returns>For example "2 min 10 s", or an empty string when unknown.</returns>
    public static string Duration(double? seconds)
    {
        if (seconds is null or < 0 or double.NaN or double.PositiveInfinity) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds.Value));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    public static string Date(DateTimeOffset value) => string.Create(Cz, $"{value.ToLocalTime():d. M. yyyy}");

    /// <summary>Czech plural for "PC": 1 PC, 2 až 4 PC, 5 a více PC. The word does not change, only the count is shown.</summary>
    public static string PcCount(int count) => count == 1 ? "1 PC" : $"{count} PC";

    /// <summary>Czech plural for "soubor": 1 soubor, 2 až 4 soubory, 5 a více souborů.</summary>
    public static string FileCount(int count) => count == 1 ? "1 soubor" : count is >= 2 and <= 4 ? $"{count} soubory" : $"{count} souborů";
}
