namespace GameShare.Protocol;

/// <summary>
/// The app's version, from the <c>Version</c> property in <c>src\Directory.Build.props</c>, the one place it is set.
/// Every project under src\ is built with the same value, so this reads the same on the agent, the client, the
/// admin tools and in what an agent announces to its peers.
/// </summary>
public static class AppVersion
{
    public static readonly string Current = Format(typeof(AppVersion).Assembly.GetName().Version);

    // MSBuild turns "0.1.0" into AssemblyVersion "0.1.0.0"; the trailing revision is always 0 and not part of the version we chose.
    private static string Format(Version? v) => v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
}

/// <summary>
/// The published builds that update themselves, each from its own package. A PC takes only the package of the build it runs:
/// the installed service would break on the portable exe's files, and the other way round.
/// </summary>
public static class AppFlavors
{
    /// <summary>The Windows service with the client in <c>Client\</c>, self-contained (GameShare-Agent.zip).</summary>
    public const string Agent = "agent";

    /// <summary>The same for a PC with the .NET 10 runtime installed (GameShare-Agent-net10.zip).</summary>
    public const string AgentNet10 = "agent-net10";

    /// <summary>The portable GameShare-LanParty.exe.</summary>
    public const string LanParty = "lanparty";

    public static readonly IReadOnlyList<string> All = [Agent, AgentNet10, LanParty];

    public static bool IsKnown(string? flavor) => flavor is not null && All.Contains(flavor, StringComparer.Ordinal);
}
