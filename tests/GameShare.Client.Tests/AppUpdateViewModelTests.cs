using GameShare.Client.Services;
using GameShare.Client.ViewModels;
using GameShare.Protocol;

namespace GameShare.Client.Tests;

/// <summary>How the window shows an update of GameShare itself, and the client starting again once the service replaced its files.</summary>
public class AppUpdateViewModelTests
{
    private sealed class FakeRestarter(string? onDisk) : IClientRestarter
    {
        public int Restarts { get; private set; }
        public string? VersionOnDisk() => onDisk;
        public void Restart() => Restarts++;
    }

    private static AppUpdateStatusDto Status(AppUpdateState state, string? version = "0.5.0", bool canApply = false, string? cannot = null,
        long done = 0, long total = 0, string? source = null, string? error = null, string? notes = "Novinky.") =>
        new(AppVersion.Current, "agent", state, version, notes, DateTimeOffset.UtcNow, done, total, source, error, DateTimeOffset.UtcNow, canApply, cannot, null);

    private static (AppModel App, FakeAgent Agent, FakeEvents Events, FakeRestarter Restarter) Create(string? onDisk = null)
    {
        var agent = new FakeAgent { Version = AppVersion.Current };
        var events = new FakeEvents();
        var restarter = new FakeRestarter(onDisk ?? AppVersion.Current);
        return (new AppModel(agent, events, new ImmediateDispatcher(), restarter: restarter), agent, events, restarter);
    }

    [Fact]
    public async Task A_ready_version_shows_the_strip_and_the_button_puts_it_in_place()
    {
        var (app, agent, _, _) = Create();
        agent.AppUpdate = Status(AppUpdateState.Ready, canApply: true);
        await app.StartAsync();

        Assert.True(app.HasUpdateBanner);
        Assert.Contains("0.5.0", app.UpdateBannerText);
        Assert.True(app.ApplyUpdateCommand.CanExecute(null));

        await app.ApplyUpdateCommand.ExecuteAsync(null);

        Assert.Contains("ApplyAppUpdate", agent.Calls);
        Assert.Equal(AppUpdateState.Applying, app.AppUpdate!.State);
        Assert.Contains("restartuje", app.UpdateBannerText);
        Assert.False(app.ApplyUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_version_waiting_for_the_lan_says_until_when_instead_of_standing_at_zero_percent()
    {
        var (app, agent, _, _) = Create();
        var until = new DateTimeOffset(2026, 9, 30, 12, 37, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 30)));
        agent.AppUpdate = Status(AppUpdateState.Downloading, version: "0.6.5", total: 100) with { WaitingForLanUntil = until };
        await app.StartAsync();

        Assert.Contains("0.6.5", app.UpdateText);
        Assert.Contains("12:37", app.UpdateText);
        Assert.Contains("Zkontrolovat aktualizace", app.UpdateText);
        Assert.DoesNotContain("0 %", app.UpdateText);
    }

    [Fact]
    public async Task A_version_that_cannot_be_applied_here_says_why_and_offers_no_button()
    {
        var (app, agent, _, _) = Create();
        agent.AppUpdate = Status(AppUpdateState.Ready, cannot: "Do složky C:\\Hry nejde zapisovat.");
        await app.StartAsync();

        Assert.True(app.HasUpdateBanner);
        Assert.Contains("nejde zapisovat", app.UpdateBannerText);
        Assert.False(app.CanApplyUpdate);
        Assert.False(app.ApplyUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_agents_events_keep_the_state_current_and_nothing_is_shown_while_up_to_date_or_downloading()
    {
        var (app, agent, events, _) = Create();
        agent.AppUpdate = Status(AppUpdateState.UpToDate, version: null);
        await app.StartAsync();
        Assert.False(app.HasUpdateBanner);

        events.Raise(GameShareEvents.AppUpdateChanged, Status(AppUpdateState.Downloading, done: 45, total: 100, source: "LAN"));
        Assert.False(app.HasUpdateBanner);
        Assert.Contains("45 %", app.UpdateText);

        events.Raise(GameShareEvents.AppUpdateChanged, Status(AppUpdateState.Ready, canApply: true));
        Assert.True(app.HasUpdateBanner);
        Assert.Equal("Novinky.", app.UpdateNotes);
    }

    [Fact]
    public async Task A_refused_update_shows_the_agents_reason()
    {
        var (app, agent, _, _) = Create();
        agent.AppUpdate = Status(AppUpdateState.Ready, canApply: true);
        await app.StartAsync();
        agent.FailNext["ApplyAppUpdate"] = new AgentException("Stažené soubory verze 0.5.0 se od kontroly změnily.", 422);

        await app.ApplyUpdateCommand.ExecuteAsync(null);

        Assert.Contains("změnily", app.UpdateMessage);
    }

    [Fact]
    public async Task An_agent_from_before_updates_is_still_used_and_the_page_says_it_does_not_know()
    {
        var (app, agent, _, _) = Create();
        agent.AppUpdate = null;

        await app.StartAsync();

        Assert.True(app.IsConnected);
        Assert.False(app.HasUpdateBanner);
        Assert.Contains("starší verze", app.UpdateText);
    }

    [Theory]
    [InlineData(AppUpdateState.Downloading, "LAN", null, "Stahuji verzi 0.5.0 od ostatních PC v síti: 45 %.")]
    [InlineData(AppUpdateState.Downloading, "https://github.com/x/releases/latest/download/", null, "Stahuji verzi 0.5.0 z internetu: 45 %.")]
    [InlineData(AppUpdateState.Downloading, @"\\server\hry", null, "Stahuji verzi 0.5.0 ze sdílené složky: 45 %.")]
    [InlineData(AppUpdateState.Available, null, "Zdroj přestal odpovídat.", "Je k dispozici verze 0.5.0, stažení se zkusí znovu. Zdroj přestal odpovídat.")]
    public void The_state_is_described_in_one_sentence(AppUpdateState state, string? source, string? error, string expected)
    {
        Assert.Equal(expected, AppModel.DescribeUpdate(Status(state, done: 45, total: 100, source: source, error: error)));
    }

    [Fact]
    public async Task Once_the_service_replaced_the_clients_files_it_starts_again_once()
    {
        var (app, agent, _, restarter) = Create(onDisk: "99.0.0");
        agent.Version = "99.0.0"; // the agent came back as the new version, and the program file on disk is the new one

        await app.StartAsync();
        await app.RefreshAllAsync();

        Assert.Equal(1, restarter.Restarts);
    }

    [Fact]
    public async Task A_client_whose_own_file_did_not_change_does_not_restart_for_nothing()
    {
        // The portable exe attached to an installed service: the service updated, this exe did not.
        var (app, agent, _, restarter) = Create(onDisk: AppVersion.Current);
        agent.Version = "99.0.0";

        await app.StartAsync();

        Assert.Equal(0, restarter.Restarts);
        Assert.True(app.HasVersionMismatch);
    }
}
