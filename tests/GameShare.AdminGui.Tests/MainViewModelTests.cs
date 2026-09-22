using GameShare.AdminGui.ViewModels;
using GameShare.Storage;

namespace GameShare.AdminGui.Tests;

/// <summary>The graphical admin tool, exercised the way a person would click through it, with real keys and real files.</summary>
public class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TestGame.NewTempDir();
    private readonly FakePickers _pickers = new();

    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private string P(string name) => Path.Combine(_dir, name);
    private MainViewModel Vm() => new(_pickers, _pickers, () => Now, P("settings.json"));

    private async Task<MainViewModel> KeyedAsync()
    {
        var vm = Vm();
        _pickers.NextFolder = P("keys");
        await vm.GenerateKeysCommand.ExecuteAsync(null);
        return vm;
    }

    private async Task<(MainViewModel Vm, TrustedGameRow Row)> WithOneGameAsync()
    {
        var vm = await KeyedAsync();
        vm.ListPath = P("trust.json");
        await vm.LoadListCommand.ExecuteAsync(null);
        using var game = new TestGame("BeamNG.drive");
        vm.GameFolder = game.GameDir;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.AddScannedGameCommand.ExecuteAsync(null);
        return (vm, vm.VerifiedGames.Single());
    }

    // ---- keys ----

    [Fact]
    public async Task Generating_keys_writes_real_files_and_shows_the_key_id()
    {
        var vm = Vm();
        _pickers.NextFolder = P("keys");

        await vm.GenerateKeysCommand.ExecuteAsync(null);

        Assert.True(vm.HasKey);
        Assert.Equal(Path.Combine(P("keys"), "trust-private.key"), vm.PrivateKeyPath);
        Assert.True(File.Exists(vm.PrivateKeyPath));
        Assert.True(File.Exists(Path.Combine(P("keys"), "trust-public.key")));
        Assert.Contains(vm.KeyId!, vm.Message);
        Assert.Contains("TrustPublicKey", vm.Message);
    }

    [Fact]
    public async Task Cancelling_the_folder_dialog_generates_nothing()
    {
        var vm = Vm();
        _pickers.NextFolder = null;

        await vm.GenerateKeysCommand.ExecuteAsync(null);

        Assert.False(vm.HasKey);
        Assert.Empty(Directory.GetFiles(_dir, "*.key", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Generating_keys_a_second_time_in_the_same_folder_explains_why_and_keeps_the_old_key()
    {
        var vm = await KeyedAsync();
        var firstId = vm.KeyId;

        await vm.GenerateKeysCommand.ExecuteAsync(null);

        Assert.Contains("already exists", vm.Message);
        Assert.Equal(firstId, vm.KeyId);
    }

    [Fact]
    public async Task A_key_protected_by_a_password_needs_the_password_to_load_again()
    {
        var vm = Vm();
        _pickers.NextFolder = P("keys");
        vm.Password = "hunter2";
        await vm.GenerateKeysCommand.ExecuteAsync(null);
        var madeId = vm.KeyId;

        var reloaded = Vm();
        reloaded.PrivateKeyPath = vm.PrivateKeyPath;
        await reloaded.LoadKeyCommand.ExecuteAsync(null);
        Assert.False(reloaded.HasKey);
        Assert.Contains("password", reloaded.Message);

        reloaded.Password = "hunter2";
        await reloaded.LoadKeyCommand.ExecuteAsync(null);
        Assert.Equal(madeId, reloaded.KeyId);
    }

    [Fact]
    public async Task Loading_a_key_that_was_never_chosen_is_explained_not_thrown()
    {
        var vm = Vm();

        await vm.LoadKeyCommand.ExecuteAsync(null);

        Assert.False(vm.HasKey);
        Assert.NotEmpty(vm.Message);
    }

    [Fact]
    public async Task Browsing_sets_the_path_and_cancelling_leaves_it_alone()
    {
        var vm = Vm();
        vm.PrivateKeyPath = "kept.key";
        _pickers.NextExistingFile = null;

        await vm.BrowsePrivateKeyCommand.ExecuteAsync(null);
        Assert.Equal("kept.key", vm.PrivateKeyPath);

        _pickers.NextExistingFile = "picked.key";
        await vm.BrowsePrivateKeyCommand.ExecuteAsync(null);
        Assert.Equal("picked.key", vm.PrivateKeyPath);
    }

    // ---- list ----

    [Fact]
    public async Task Loading_the_list_needs_a_key_first()
    {
        var vm = Vm();
        vm.ListPath = P("trust.json");

        await vm.LoadListCommand.ExecuteAsync(null);

        Assert.False(vm.HasList);
        Assert.Contains("klíč", vm.Message);
    }

    [Fact]
    public async Task A_missing_list_file_starts_a_new_empty_list_and_writes_nothing_until_something_is_added()
    {
        var vm = await KeyedAsync();
        vm.ListPath = P("trust.json");

        await vm.LoadListCommand.ExecuteAsync(null);

        Assert.True(vm.HasList);
        Assert.Equal(0, vm.Sequence);
        Assert.Empty(vm.VerifiedGames);
        Assert.False(File.Exists(vm.ListPath));
    }

    // ---- scanning and adding ----

    [Fact]
    public async Task Scanning_and_adding_a_game_signs_and_writes_a_list_that_opens_with_the_public_key()
    {
        var vm = await KeyedAsync();
        vm.ListPath = P("trust.json");
        await vm.LoadListCommand.ExecuteAsync(null);
        using var game = new TestGame("BeamNG.drive");
        vm.GameFolder = game.GameDir;

        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.HasScanned);
        Assert.Equal("BeamNG.drive", vm.ScannedName);

        await vm.AddScannedGameCommand.ExecuteAsync(null);

        Assert.False(vm.HasScanned); // cleared once it is safely in the list
        Assert.Equal(1, vm.Sequence);
        var row = Assert.Single(vm.VerifiedGames);
        Assert.Equal("BeamNG.drive", row.Name);
        var opened = TrustSigning.Open(File.ReadAllBytes(vm.ListPath), vm.PublicKey!);
        Assert.Equal(row.ContentHash, Assert.Single(opened.Games).ContentHash);
    }

    [Fact]
    public async Task Adding_without_a_list_loaded_explains_and_keeps_the_scan_for_another_try()
    {
        var vm = await KeyedAsync();
        using var game = new TestGame();
        vm.GameFolder = game.GameDir;
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.AddScannedGameCommand.ExecuteAsync(null);

        Assert.Contains("seznam", vm.Message);
        Assert.True(vm.HasScanned);
    }

    [Fact]
    public async Task Scanning_a_folder_that_does_not_exist_is_explained_not_thrown()
    {
        var vm = Vm();
        vm.GameFolder = P("nope");

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.HasScanned);
        Assert.NotEmpty(vm.Message);
    }

    // ---- revoke, remove, forget ----

    [Fact]
    public async Task Revoking_needs_a_reason_and_moves_the_game_to_the_revoked_list()
    {
        var (vm, row) = await WithOneGameAsync();

        row.StartRevokeCommand.Execute(null);
        Assert.True(row.IsRevoking);

        await row.ConfirmRevokeCommand.ExecuteAsync(null); // no reason typed
        Assert.True(row.IsRevoking);
        Assert.Single(vm.VerifiedGames);

        row.Reason = "upravený spustitelný soubor";
        await row.ConfirmRevokeCommand.ExecuteAsync(null);

        Assert.Empty(vm.VerifiedGames);
        var revoked = Assert.Single(vm.RevokedGames);
        Assert.Equal("upravený spustitelný soubor", revoked.Reason);
        Assert.Equal(2, vm.Sequence);
        var opened = TrustSigning.Open(File.ReadAllBytes(vm.ListPath), vm.PublicKey!);
        Assert.Empty(opened.Games);
        Assert.Single(opened.Revoked);
    }

    [Fact]
    public async Task Cancelling_a_revoke_leaves_the_game_verified_and_publishes_nothing()
    {
        var (vm, row) = await WithOneGameAsync();
        row.StartRevokeCommand.Execute(null);
        row.Reason = "typed but changed my mind";

        row.CancelRevokeCommand.Execute(null);

        Assert.False(row.IsRevoking);
        Assert.Equal("", row.Reason);
        Assert.Single(vm.VerifiedGames);
        Assert.Equal(1, vm.Sequence);
    }

    [Fact]
    public async Task Removing_a_verified_game_takes_it_out_without_revoking_it()
    {
        var (vm, row) = await WithOneGameAsync();

        await row.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(vm.VerifiedGames);
        Assert.Empty(vm.RevokedGames);
        Assert.Equal(2, vm.Sequence);
    }

    [Fact]
    public async Task Forgetting_a_revoked_version_removes_it_from_the_list()
    {
        var (vm, row) = await WithOneGameAsync();
        row.StartRevokeCommand.Execute(null);
        row.Reason = "bad build";
        await row.ConfirmRevokeCommand.ExecuteAsync(null);
        var revokedRow = vm.RevokedGames.Single();

        await revokedRow.ForgetCommand.ExecuteAsync(null);

        Assert.Empty(vm.RevokedGames);
        Assert.Equal(3, vm.Sequence);
    }

    // ---- remembering paths ----

    [Fact]
    public async Task The_key_and_list_paths_are_remembered_between_sessions_but_never_the_password()
    {
        var settingsPath = P("settings.json");
        var vm1 = new MainViewModel(_pickers, _pickers, () => Now, settingsPath);
        _pickers.NextFolder = P("keys");
        vm1.Password = "hunter2";
        await vm1.GenerateKeysCommand.ExecuteAsync(null);
        vm1.ListPath = P("trust.json");
        await vm1.LoadListCommand.ExecuteAsync(null);

        Assert.DoesNotContain("hunter2", File.ReadAllText(settingsPath));

        var vm2 = new MainViewModel(_pickers, _pickers, () => Now, settingsPath);
        Assert.Equal(vm1.PrivateKeyPath, vm2.PrivateKeyPath);
        Assert.Equal(vm1.ListPath, vm2.ListPath);
        Assert.Equal("", vm2.Password);
    }

    [Fact]
    public void No_settings_file_yet_starts_with_empty_paths_instead_of_throwing()
    {
        var vm = new MainViewModel(_pickers, _pickers, () => Now, P("does-not-exist.json"));

        Assert.Equal("", vm.PrivateKeyPath);
        Assert.Equal("", vm.ListPath);
    }
}
