using GameShare.Client.Services;
using GameShare.Client.ViewModels;

namespace GameShare.Client.Tests;

public class BackupClientTests
{
    private static async Task<(RemoteViewModel Remote, FakeAgent Agent, FakeFileDialogs Dialogs)> StartAsync()
    {
        var agent = new FakeAgent();
        var dialogs = new FakeFileDialogs();
        var app = new AppModel(agent, new FakeEvents(), new ImmediateDispatcher(), fileDialogs: dialogs);
        await app.StartAsync();
        return (new RemoteViewModel(app), agent, dialogs);
    }

    [Fact]
    public async Task A_backup_is_saved_only_with_a_long_enough_password_typed_twice_alike()
    {
        var (remote, agent, dialogs) = await StartAsync();

        remote.BackupPassword = "kratke";
        remote.BackupPasswordAgain = "kratke";
        await remote.SaveBackupCommand.ExecuteAsync(null);
        Assert.Equal("Heslo zálohy musí mít aspoň 8 znaků.", remote.Message);

        remote.BackupPassword = "dlouhe-heslo";
        remote.BackupPasswordAgain = "dlouhe-hesl0";
        await remote.SaveBackupCommand.ExecuteAsync(null);
        Assert.Equal("Hesla se neshodují.", remote.Message);
        Assert.DoesNotContain(agent.Calls, c => c.StartsWith("ExportPairing"));

        remote.BackupPasswordAgain = "dlouhe-heslo";
        await remote.SaveBackupCommand.ExecuteAsync(null);

        Assert.Contains("ExportPairing(dlouhe-heslo)", agent.Calls);
        var saved = Assert.Single(dialogs.Saved);
        Assert.Equal("GameShare-sparovani-PC-07.gsbackup", saved.Name);
        Assert.Equal(agent.Backup, saved.Content);
        Assert.StartsWith("Záloha je uložená.", remote.Message);
        Assert.Equal("", remote.BackupPassword); // not left lying around in the form
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_keeps_the_form_as_it_was()
    {
        var (remote, _, dialogs) = await StartAsync();
        dialogs.Cancel = true;
        remote.BackupPassword = remote.BackupPasswordAgain = "dlouhe-heslo";

        await remote.SaveBackupCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Saved);
        Assert.Equal("", remote.Message);
        Assert.Equal("dlouhe-heslo", remote.BackupPassword);
    }

    [Fact]
    public async Task Restoring_shows_whose_backup_it_is_asks_the_password_and_says_what_came_back()
    {
        var (remote, agent, dialogs) = await StartAsync();
        dialogs.ToOpen = new PickedFile("zaloha.gsbackup", agent.Backup);

        await remote.PickBackupCommand.ExecuteAsync(null);
        Assert.True(remote.IsRestoring);
        Assert.Equal("zaloha.gsbackup – záloha PC PC-07", remote.RestoreFileText);

        remote.RestorePassword = "dlouhe-heslo";
        await remote.RestoreCommand.ExecuteAsync(null);

        Assert.Contains($"RestorePairing({agent.Backup.Length}|dlouhe-heslo)", agent.Calls);
        Assert.False(remote.IsRestoring);
        Assert.StartsWith("Spárování PC PC-07 je obnovené: spravovat ho smí 1 PC, samo spravuje 4 PC.", remote.Message);
    }

    [Fact]
    public async Task A_wrong_password_keeps_the_restore_open_with_the_agents_reason()
    {
        var (remote, agent, dialogs) = await StartAsync();
        dialogs.ToOpen = new PickedFile("zaloha.gsbackup", agent.Backup);
        agent.FailNext["RestorePairing"] = new AgentException("The password is wrong, or the backup was changed after it was made.", 400);

        await remote.PickBackupCommand.ExecuteAsync(null);
        remote.RestorePassword = "spatne-heslo";
        await remote.RestoreCommand.ExecuteAsync(null);

        Assert.True(remote.IsRestoring);
        Assert.Equal("The password is wrong, or the backup was changed after it was made.", remote.Message);

        remote.CancelRestoreCommand.Execute(null);
        Assert.False(remote.IsRestoring);
        Assert.Equal("", remote.RestorePassword);
    }
}
