using GameShare.Admin;
using GameShare.Storage;

namespace GameShare.Storage.Tests;

/// <summary>The administrator's tool, run the way the administrator runs it: on real folders and real files.</summary>
public class AdminCliTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TestGame.NewTempDir();

    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private string P(string name) => Path.Combine(_dir, name);

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AdminCli.RunAsync(args, output, error, () => Now);
        return (code, output.ToString(), error.ToString());
    }

    private async Task<string> KeysAsync(string? password = null)
    {
        var result = password is null ? await RunAsync("keygen", "--out", _dir) : await RunAsync("keygen", "--out", _dir, "--password", password);
        Assert.Equal(0, result.Code);
        return P("trust-private.key");
    }

    [Fact]
    public async Task Adding_a_game_folder_signs_a_list_that_the_public_key_opens_and_that_names_the_game()
    {
        var key = await KeysAsync();
        using var game = new TestGame("BeamNG.drive");
        var expected = (await ManifestBuilder.ScanAndBuildAsync(game.GameDir)).Manifest;

        var added = await RunAsync("add", game.GameDir, "--key", key, "--list", P("trust.json"));

        Assert.Equal(0, added.Code);
        Assert.Contains(expected.ContentHash, added.Out);
        var list = TrustSigning.Open(File.ReadAllBytes(P("trust.json")), File.ReadAllText(P("trust-public.key")).Trim());
        var entry = Assert.Single(list.Games);
        Assert.Equal(expected.ContentHash, entry.ContentHash);
        Assert.Equal("BeamNG.drive", entry.Name);
        Assert.Equal(1, list.Sequence);
    }

    [Fact]
    public async Task Adding_more_extends_the_list_and_raises_the_sequence()
    {
        var key = await KeysAsync();
        using var one = new TestGame("One", seed: 1);
        using var two = new TestGame("Two", seed: 2);

        await RunAsync("add", one.GameDir, "--key", key, "--list", P("trust.json"));
        await RunAsync("add", two.GameDir, "--key", key, "--list", P("trust.json"), "--valid-days", "30");

        var list = TrustSigning.Open(File.ReadAllBytes(P("trust.json")), File.ReadAllText(P("trust-public.key")).Trim());
        Assert.Equal(["One", "Two"], list.Games.Select(g => g.Name));
        Assert.Equal(2, list.Sequence);
        Assert.Equal(Now.AddDays(30), list.ValidUntil);
    }

    [Fact]
    public async Task Revoking_and_removing_change_the_list_and_show_prints_what_it_says()
    {
        var key = await KeysAsync();
        using var one = new TestGame("One", seed: 1);
        using var two = new TestGame("Two", seed: 2);
        await RunAsync("add", one.GameDir, two.GameDir, "--key", key, "--list", P("trust.json"));
        var hashes = TrustSigning.Open(File.ReadAllBytes(P("trust.json")), File.ReadAllText(P("trust-public.key")).Trim()).Games.Select(g => g.ContentHash).ToList();

        Assert.Equal(0, (await RunAsync("revoke", hashes[0], "--reason", "modified executable", "--key", key, "--list", P("trust.json"))).Code);
        Assert.Equal(0, (await RunAsync("remove", hashes[1], "--key", key, "--list", P("trust.json"))).Code);
        var shown = await RunAsync("show", "--list", P("trust.json"), "--pub", P("trust-public.key"));

        Assert.Equal(0, shown.Code);
        Assert.Contains("Signature is valid", shown.Out);
        Assert.Contains("modified executable", shown.Out);
        Assert.Contains("0 verified", shown.Out);
        Assert.Contains("1 revoked", shown.Out);
    }

    [Fact]
    public async Task Show_says_when_the_signature_is_wrong_and_exits_with_an_error()
    {
        var key = await KeysAsync();
        using var game = new TestGame();
        await RunAsync("add", game.GameDir, "--key", key, "--list", P("trust.json"));
        var other = TrustSigning.GenerateKeyPair();

        var shown = await RunAsync("show", "--list", P("trust.json"), "--pub", other.PublicKey);

        Assert.Equal(1, shown.Code);
        Assert.Contains("signed with key", shown.Err);
    }

    [Fact]
    public async Task A_list_signed_by_someone_else_is_not_extended_with_this_key()
    {
        var key = await KeysAsync();
        using var game = new TestGame();
        var strangers = TrustSigning.GenerateKeyPair();
        var payload = TrustListEditor.Add(TrustPayload.Empty(Now), [new TrustedGame(new string('a', 64), "x", "X", null)], Now);
        File.WriteAllBytes(P("trust.json"), TrustSigning.Serialize(TrustSigning.Sign(payload, strangers.PrivateKey)));

        var result = await RunAsync("add", game.GameDir, "--key", key, "--list", P("trust.json"));

        Assert.Equal(1, result.Code);
        Assert.Contains("signed with key", result.Err);
    }

    [Fact]
    public async Task A_password_protected_key_needs_its_password()
    {
        var key = await KeysAsync("open sesame");
        using var game = new TestGame();

        var without = await RunAsync("add", game.GameDir, "--key", key, "--list", P("trust.json"));
        var wrong = await RunAsync("add", game.GameDir, "--key", key, "--list", P("trust.json"), "--password", "nope");
        var right = await RunAsync("add", game.GameDir, "--key", key, "--list", P("trust.json"), "--password", "open sesame");

        Assert.Equal(1, without.Code);
        Assert.Equal(1, wrong.Code);
        Assert.Equal(0, right.Code);
    }

    [Fact]
    public async Task Making_a_key_never_overwrites_an_existing_one()
    {
        await KeysAsync();
        var before = File.ReadAllText(P("trust-private.key"));

        var again = await RunAsync("keygen", "--out", _dir);

        Assert.Equal(1, again.Code);
        Assert.Contains("already exists", again.Err);
        Assert.Equal(before, File.ReadAllText(P("trust-private.key")));
    }

    [Theory]
    [InlineData("add")]                       // no folder
    [InlineData("add", "x", "--key")]         // an option without a value
    [InlineData("revoke", "abc")]             // no reason, no key
    [InlineData("frobnicate")]
    public async Task Wrong_usage_is_explained_and_fails(params string[] args)
    {
        var result = await RunAsync(args);

        Assert.NotEqual(0, result.Code);
        Assert.NotEmpty(result.Err);
    }
}
