using GameShare.Protocol;
using GameShare.Storage;

namespace GameShare.Agent.Tests;

/// <summary>
/// The steps of the service's update helper, with the service played by a fake: stop, swap, start, and keep the new files only
/// when the new version answers. A real Windows service is left to the manual test, see the plan of the updater.
/// </summary>
public sealed class AppUpdateHelperTests : IDisposable
{
    private readonly string _root = TestGame.NewTempDir();
    private string Install => Path.Combine(_root, "install");
    private string Staging => Path.Combine(_root, "staging");

    public AppUpdateHelperTests()
    {
        Write(Install, "GameShare.Agent.exe", "agent 0.4.3");
        Write(Install, "Client/GameShare.exe", "client 0.4.3");
        Write(Install, "appsettings.json", "{ \"Agent\": { \"TorrentPort\": 7000 } }");
        Write(Staging, "GameShare.Agent.exe", "agent 0.5.0");
        Write(Staging, "Client/GameShare.exe", "client 0.5.0");
        Write(Staging, "appsettings.json", "{ }");
    }

    public void Dispose() => TestGame.DeleteQuietly(_root);

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class FakeService : IServiceControl
    {
        /// <summary>Whether the outcome was already written each time the service was started: the agent reads it once, at startup.</summary>
        public Func<bool> ResultWritten { get; set; } = () => false;
        public List<string> Calls { get; } = [];
        public void Stop() => Calls.Add("stop");
        public void Start() => Calls.Add(ResultWritten() ? "start (result written)" : "start");
    }

    private async Task<AppUpdateApplyRequest> RequestAsync()
    {
        var manifest = ManifestBuilder.Build(await ContentScanner.ScanAsync(Staging)) with { Version = "0.5.0" };
        return new AppUpdateApplyRequest("0.5.0", manifest, Staging, Install, null, "GameShare Agent", 0, 47701,
            Path.Combine(_root, AppUpdateResult.FileName), Path.Combine(_root, "update.log"));
    }

    private AppUpdateResult Result() => GameShareJson.Deserialize<AppUpdateResult>(File.ReadAllText(Path.Combine(_root, AppUpdateResult.FileName)));

    [Fact]
    public async Task When_the_new_version_answers_its_files_are_kept_and_the_old_ones_deleted()
    {
        var service = new FakeService();
        var answers = new Queue<string?>(["", null, "0.4.3", "0.5.0"]); // restarting: nothing, then the new version

        var code = await AppUpdateHelper.RunAsync(await RequestAsync(), service, _ => Task.FromResult(answers.Count > 1 ? answers.Dequeue() : answers.Peek()), TimeSpan.FromSeconds(20));

        Assert.Equal(0, code);
        Assert.Equal(["stop", "start"], service.Calls);
        Assert.Equal("agent 0.5.0", Read("GameShare.Agent.exe"));
        Assert.Equal("client 0.5.0", Read("Client/GameShare.exe"));
        Assert.Contains("7000", Read("appsettings.json"));
        Assert.False(AppUpdateFiles.HasPendingSwap(Install));
        Assert.Empty(Directory.EnumerateFiles(Install, "*" + AppUpdateFiles.AsideSuffix, SearchOption.AllDirectories));
        Assert.True(Result().Ok);
    }

    [Fact]
    public async Task When_the_new_version_never_answers_the_previous_one_is_put_back_and_started()
    {
        var service = new FakeService();

        service.ResultWritten = () => File.Exists(Path.Combine(_root, AppUpdateResult.FileName));

        var code = await AppUpdateHelper.RunAsync(await RequestAsync(), service, _ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(2));

        Assert.Equal(1, code);
        // The agent that comes back must find the reason when it starts, or the user never learns the update failed.
        Assert.Equal(["stop", "start", "stop", "start (result written)"], service.Calls);
        Assert.Equal("agent 0.4.3", Read("GameShare.Agent.exe"));
        Assert.Equal("client 0.4.3", Read("Client/GameShare.exe"));
        Assert.False(AppUpdateFiles.HasPendingSwap(Install));
        var result = Result();
        Assert.False(result.Ok);
        Assert.Contains("nerozběhla", result.Error);
        Assert.Contains("did not answer", File.ReadAllText(Path.Combine(_root, "update.log")));
    }

    [Fact]
    public async Task When_the_files_cannot_be_swapped_the_old_version_is_started_again_untouched()
    {
        var service = new FakeService { ResultWritten = () => File.Exists(Path.Combine(_root, AppUpdateResult.FileName)) };
        var request = await RequestAsync();
        Write(Staging, "GameShare.Agent.exe", "agent 0.5.0, changed after it was checked");

        var code = await AppUpdateHelper.RunAsync(request, service, _ => Task.FromResult<string?>("0.5.0"), TimeSpan.FromSeconds(2));

        Assert.Equal(1, code);
        Assert.Equal(["stop", "start (result written)"], service.Calls);
        Assert.Equal("agent 0.4.3", Read("GameShare.Agent.exe"));
        Assert.Contains("vyměnit", Result().Error);
    }

    [Fact]
    public async Task A_swap_an_earlier_helper_never_decided_on_is_kept_before_the_next_one()
    {
        // As if the helper of the last update died after the swap: the agent that runs, and asked for this update, runs from those files.
        await AppUpdateFiles.SwapAsync(Staging, Install, (await RequestAsync()).Manifest);
        Write(Staging, "GameShare.Agent.exe", "agent 0.6.0");
        Write(Staging, "Client/GameShare.exe", "client 0.6.0");
        var next = await RequestAsync() with { Version = "0.6.0" };

        var code = await AppUpdateHelper.RunAsync(next, new FakeService(), _ => Task.FromResult<string?>("0.6.0"), TimeSpan.FromSeconds(5));

        Assert.Equal(0, code);
        Assert.Equal("agent 0.6.0", Read("GameShare.Agent.exe"));
        Assert.Empty(Directory.EnumerateFiles(Install, "*" + AppUpdateFiles.AsideSuffix, SearchOption.AllDirectories));
    }
}
