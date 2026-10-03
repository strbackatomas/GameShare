using GameShare.Client.ViewModels;
using static GameShare.Client.Tests.Data;

namespace GameShare.Client.Tests;

public class DownloadErrorTests
{
    [Fact]
    public async Task A_download_the_agent_paused_for_a_full_disk_shows_why_and_a_plain_pause_shows_nothing()
    {
        var agent = new FakeAgent
        {
            Downloads =
            [
                Download(1, A, "BeamNG.drive", "Paused", 40, error: "Paused, the disk is full: only 100 MB free on D:. Free some space there, then resume."),
                Download(2, B, "Factorio", "Paused", 10),
                Download(3, C, "ETS2", "Failed", 5, error: "The transfer engine reported an error."),
            ],
        };
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher());
        await app.StartAsync();

        Assert.True(app.Downloads.Single(d => d.Id == 1).ShowError);
        Assert.False(app.Downloads.Single(d => d.Id == 2).ShowError);
        Assert.True(app.Downloads.Single(d => d.Id == 3).ShowError);
    }
}
