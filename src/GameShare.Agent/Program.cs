using GameShare.Agent;

try
{
    var app = await AgentHost.BuildAsync(args);
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Logging may not be up yet, so make sure a startup failure is never silent. A service that dies leaves no other trace.
    var message = $"{DateTime.Now:O} GameShare agent could not start: {ex}";
    Console.Error.WriteLine(message);
    try
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameShare", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "startup-error.log"), message + Environment.NewLine);
    }
    catch (Exception) { /* nothing more can be done */ }
    return 1;
}
