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
