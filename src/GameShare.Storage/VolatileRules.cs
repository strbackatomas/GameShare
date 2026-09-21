using System.Text;
using System.Text.RegularExpressions;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>
/// A path pattern such as <c>saves/**</c> or <c>*.ini</c>. Matches relative paths with forward slashes, ignoring case.
/// <c>*</c> stays inside one folder level, <c>**</c> crosses folders, <c>?</c> is one character.
/// A pattern without a slash matches at any depth, so <c>*.ini</c> finds <c>a/b/c.ini</c>. A trailing slash means the whole folder.
/// </summary>
public sealed class PathPattern
{
    private readonly Regex _regex;

    private PathPattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    /// <summary>Normalised form: forward slashes, no leading "./" or "/".</summary>
    public string Text { get; }

    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath);

    public static bool TryCreate(string raw, out PathPattern pattern, out string error)
    {
        pattern = null!;
        var text = (raw ?? "").Trim().Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        text = text.TrimStart('/');

        if (text.Length == 0) { error = "the pattern is empty"; return false; }
        if (text.Length > 260) { error = "the pattern is longer than 260 characters"; return false; }
        if (text.Any(char.IsControl)) { error = "the pattern contains control characters"; return false; }
        if (text.Contains(':')) { error = "drive letters are not allowed, use a path relative to the game folder"; return false; }
        if (text.Split('/').Any(s => s == "..")) { error = "'..' is not allowed, the pattern must stay inside the game folder"; return false; }
        // A pattern that matches every file would leave the game empty.
        if (Regex.IsMatch(text, @"^[\*/]+$")) { error = "the pattern would match every file"; return false; }

        pattern = new PathPattern(text, Compile(text));
        error = "";
        return true;
    }

    private static Regex Compile(string text)
    {
        bool anyDepth = !text.Contains('/');
        if (text.EndsWith('/')) text += "**";

        var sb = new StringBuilder("^");
        if (anyDepth) sb.Append("(?:.*/)?");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                bool atSegmentStart = i == 0 || text[i - 1] == '/';
                i++; // consume the second star
                if (atSegmentStart && i + 1 < text.Length && text[i + 1] == '/') { sb.Append("(?:.*/)?"); i++; } // "**/"
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>A set of patterns deciding which files are not game content.</summary>
public sealed class VolatileMatcher
{
    public static readonly VolatileMatcher None = new([], []);

    private readonly PathPattern[] _patterns;

    private VolatileMatcher(PathPattern[] patterns, IReadOnlyList<string> texts)
    {
        _patterns = patterns;
        Patterns = texts;
    }

    /// <summary>Normalised, sorted, without duplicates. This is what a manifest records.</summary>
    public IReadOnlyList<string> Patterns { get; }

    public bool IsMatch(string relativePath)
    {
        foreach (var p in _patterns) if (p.IsMatch(relativePath)) return true;
        return false;
    }

    /// <exception cref="InvalidDataException">A pattern is invalid. The message names it and says why.</exception>
    public static VolatileMatcher Create(IEnumerable<string> patterns)
    {
        var compiled = new Dictionary<string, PathPattern>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in patterns)
        {
            if (!PathPattern.TryCreate(raw, out var p, out var error))
                throw new InvalidDataException($"Invalid volatile pattern '{raw}': {error}.");
            compiled.TryAdd(p.Text, p);
        }
        if (compiled.Count > VolatileRules.MaxPatterns)
            throw new InvalidDataException($"At most {VolatileRules.MaxPatterns} volatile patterns are supported, got {compiled.Count}.");

        var ordered = compiled.Values.OrderBy(p => p.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        return new VolatileMatcher(ordered, ordered.Select(p => p.Text).ToList());
    }
}

/// <summary>Which files a game rewrites while it is played and that therefore must not count as game content.</summary>
public static class VolatileRules
{
    public const int MaxPatterns = 200;

    /// <summary>
    /// Files no game needs shipped and every game may write. Deliberately short: a wrong entry here would hide real content.
    /// Games with saves, settings or caches inside their folder list those per game.
    /// </summary>
    public static readonly IReadOnlyList<string> Defaults = ["*.log", "*.tmp", "*.dmp", "Thumbs.db"];

    /// <summary>
    /// The effective patterns for a folder: the built-in defaults, plus the definition file's list, plus whatever the stored
    /// manifest already excluded. Patterns only accumulate, so a file once excluded never re-enters the content and flips its identity.
    /// </summary>
    public static VolatileMatcher Resolve(GameDefinition? definition, IEnumerable<string>? alreadyRecorded = null, IEnumerable<string>? additional = null) =>
        VolatileMatcher.Create(Defaults
            .Concat(definition?.Volatile ?? [])
            .Concat(alreadyRecorded ?? [])
            .Concat(additional ?? []));

    public static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>Patterns an admin could add after seeing which files a game changed: a folder for files in folders, the name otherwise.</summary>
    public static IReadOnlyList<string> Suggest(IEnumerable<string> changedPaths) =>
        changedPaths
            .Select(p => p.Contains('/') ? p[..p.IndexOf('/')] + "/**" : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
