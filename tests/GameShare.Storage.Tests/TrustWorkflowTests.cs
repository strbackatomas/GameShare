namespace GameShare.Storage.Tests;

/// <summary>The steps shared by the CLI and the GUI admin tool. Behaviour with real keys and real folders.</summary>
public class TrustWorkflowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TestGame.NewTempDir();

    public void Dispose() => TestGame.DeleteQuietly(_dir);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Generating_keys_a_second_time_in_the_same_folder_is_refused()
    {
        TrustWorkflow.GenerateKeys(_dir);

        var ex = Assert.Throws<InvalidOperationException>(() => TrustWorkflow.GenerateKeys(_dir));

        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void A_key_made_and_loaded_again_agree_on_the_public_key_and_the_key_id()
    {
        var made = TrustWorkflow.GenerateKeys(_dir, "hunter2");

        var loaded = TrustWorkflow.LoadKey(made.PrivateKeyPath, "hunter2");

        Assert.Equal((made.PublicKey, made.KeyId), (loaded.PublicKey, loaded.KeyId));
    }

    [Fact]
    public void Loading_a_key_with_the_wrong_password_is_refused_with_a_reason()
    {
        var made = TrustWorkflow.GenerateKeys(_dir, "right");

        var ex = Assert.Throws<InvalidDataException>(() => TrustWorkflow.LoadKey(made.PrivateKeyPath, "wrong"));

        Assert.Contains("password", ex.Message);
    }

    [Fact]
    public void Loading_a_key_that_does_not_exist_says_so()
    {
        Assert.Throws<FileNotFoundException>(() => TrustWorkflow.LoadKey(P("missing.key")));
    }

    [Fact]
    public void A_missing_list_file_starts_empty_at_the_given_time()
    {
        var keys = TrustSigning.GenerateKeyPair();

        var fresh = TrustWorkflow.LoadOrStartList(P("trust.json"), keys.PublicKey, Now);

        Assert.Equal(0, fresh.Sequence);
        Assert.Equal(Now, fresh.IssuedAt);
        Assert.Empty(fresh.Games);
    }

    [Fact]
    public void A_present_list_file_is_checked_against_the_key_a_wrong_key_is_refused()
    {
        var keys = TrustWorkflow.GenerateKeys(_dir);
        var other = TrustSigning.GenerateKeyPair();
        TrustWorkflow.Publish(TrustListEditor.Add(TrustPayload.Empty(Now), [new TrustedGame(new string('a', 64), "g", "G", "1")], Now),
            keys.PrivateKeyPath, null, P("trust.json"));

        Assert.Single(TrustWorkflow.LoadOrStartList(P("trust.json"), keys.PublicKey, Now).Games);
        Assert.Throws<InvalidDataException>(() => TrustWorkflow.LoadOrStartList(P("trust.json"), other.PublicKey, Now));
    }

    [Fact]
    public async Task Scanning_a_folder_that_does_not_exist_says_so()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => TrustWorkflow.ScanAsync(P("nope")));
    }

    [Fact]
    public async Task A_scanned_folder_becomes_a_candidate_that_publish_can_add_and_a_later_load_can_verify()
    {
        using var game = new TestGame("BeamNG.drive");
        var keys = TrustWorkflow.GenerateKeys(_dir);

        var candidate = await TrustWorkflow.ScanAsync(game.GameDir);
        var list = TrustListEditor.Add(TrustWorkflow.LoadOrStartList(P("trust.json"), keys.PublicKey, Now), [candidate], Now);
        TrustWorkflow.Publish(list, keys.PrivateKeyPath, null, P("trust.json"));

        var reopened = TrustWorkflow.LoadOrStartList(P("trust.json"), keys.PublicKey, Now);
        Assert.Equal(candidate, Assert.Single(reopened.Games));
    }

    [Fact]
    public async Task A_scanned_game_with_a_definition_is_signed_together_with_it()
    {
        using var game = new TestGame("Battlefield 2");
        await File.WriteAllTextAsync(Path.Combine(game.GameDir, "gameshare.json"), DefinitionTests.Battlefield);
        var keys = TrustWorkflow.GenerateKeys(_dir);

        var candidate = await TrustWorkflow.ScanAsync(game.GameDir);
        TrustWorkflow.Publish(TrustListEditor.Add(TrustPayload.Empty(Now), [candidate], Now), keys.PrivateKeyPath, null, P("trust.json"));

        var definition = await GameDefinitionFile.TryLoadAsync(game.GameDir);
        var signed = Assert.Single(TrustWorkflow.LoadOrStartList(P("trust.json"), keys.PublicKey, Now).Games);
        Assert.Equal(DefinitionHasher.Compute(definition!), signed.DefinitionHash);

        using var plain = new TestGame("Plain");
        Assert.Null((await TrustWorkflow.ScanAsync(plain.GameDir)).DefinitionHash); // nothing to sign
    }

    [Fact]
    public void A_list_made_before_definitions_were_signed_still_opens()
    {
        // The payload exactly as an older gameshare-admin wrote it, no definitionHash at all. Open reads the verified payload this way.
        var hash = new string('a', 64);
        var list = GameShare.Protocol.GameShareJson.Deserialize<TrustPayload>(
            $$"""{"sequence":1,"issuedAt":"2026-09-01T00:00:00+00:00","validUntil":null,"games":[{"contentHash":"{{hash}}","gameId":"g","name":"G","version":"1"}],"revoked":[]}""");

        Assert.Null(Assert.Single(list.Games).DefinitionHash);
    }

    [Fact]
    public void Publish_refuses_a_key_file_that_does_not_exist()
    {
        var list = TrustPayload.Empty(Now);
        Assert.Throws<FileNotFoundException>(() => TrustWorkflow.Publish(list, P("missing.key"), null, P("trust.json")));
    }
}
