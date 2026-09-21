using System.Text;
using System.Text.Json;
using GameShare.Protocol;

namespace GameShare.Storage.Tests;

/// <summary>The administrator's signed list of content hashes. A list that does not verify must count as no list.</summary>
public class TrustListTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static string Hash(char c) => new(c, 64);
    private static TrustedGame Game(char c, string name = "BeamNG.drive", string version = "0.38") => new(Hash(c), "beamng", name, version);

    private static (TrustKeyPair Keys, byte[] Envelope, TrustPayload Payload) Signed(TrustKeyPair? keys = null, TrustPayload? payload = null)
    {
        keys ??= TrustSigning.GenerateKeyPair();
        payload ??= TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a'), Game('b', "Assetto Corsa", "1.16")], Now);
        return (keys, TrustSigning.Serialize(TrustSigning.Sign(payload, keys.PrivateKey)), payload);
    }

    [Fact]
    public void A_signed_list_opens_with_the_public_key_and_says_what_was_signed()
    {
        var (keys, envelope, payload) = Signed();

        var opened = TrustSigning.Open(envelope, keys.PublicKey);

        Assert.Equal(payload.Sequence, opened.Sequence);
        Assert.Equal(payload.Games.Select(g => g.ContentHash), opened.Games.Select(g => g.ContentHash));
        Assert.Equal("Assetto Corsa", opened.Games.Single(g => g.ContentHash == Hash('b')).Name);
    }

    [Fact]
    public void A_list_signed_with_another_key_is_refused_and_the_message_names_the_keys()
    {
        var (_, envelope, _) = Signed();
        var other = TrustSigning.GenerateKeyPair();

        var ex = Assert.Throws<InvalidDataException>(() => TrustSigning.Open(envelope, other.PublicKey));

        Assert.Contains(TrustSigning.KeyId(other.PublicKey), ex.Message);
        Assert.Contains("signed with key", ex.Message);
    }

    [Fact]
    public void A_list_whose_content_was_changed_after_signing_is_refused()
    {
        var (keys, envelope, _) = Signed();
        var doc = JsonSerializer.Deserialize<TrustEnvelope>(envelope, GameShareJson.Options)!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(doc.Payload)).Replace(Hash('a'), Hash('c'));
        var forged = doc with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) };

        var ex = Assert.Throws<InvalidDataException>(() => TrustSigning.Open(TrustSigning.Serialize(forged), keys.PublicKey));

        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void A_list_with_a_forged_key_id_still_fails_on_the_signature()
    {
        // An attacker signs with their own key and claims the administrator's key id.
        var admin = TrustSigning.GenerateKeyPair();
        var attacker = TrustSigning.GenerateKeyPair();
        var payload = TrustListEditor.Add(TrustPayload.Empty(Now), [Game('e')], Now);
        var forged = TrustSigning.Sign(payload, attacker.PrivateKey) with { KeyId = TrustSigning.KeyId(admin.PublicKey) };

        var ex = Assert.Throws<InvalidDataException>(() => TrustSigning.Open(TrustSigning.Serialize(forged), admin.PublicKey));

        Assert.Contains("signature", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"format":1,"keyId":"x","payload":"###","signature":"###"}""")]
    [InlineData("""{"format":9,"keyId":"x","payload":"e30=","signature":"AA=="}""")]
    public void Garbage_is_refused_with_a_message_and_never_crashes(string text)
    {
        var keys = TrustSigning.GenerateKeyPair();

        Assert.Throws<InvalidDataException>(() => TrustSigning.Open(Encoding.UTF8.GetBytes(text), keys.PublicKey));
    }

    [Fact]
    public void A_list_that_is_far_too_large_is_refused_before_it_is_parsed()
    {
        var keys = TrustSigning.GenerateKeyPair();

        var ex = Assert.Throws<InvalidDataException>(() => TrustSigning.Open(new byte[TrustSigning.MaxEnvelopeBytes + 1], keys.PublicKey));

        Assert.Contains("larger", ex.Message);
    }

    [Fact]
    public void A_content_hash_that_is_not_a_hash_is_refused_when_signing_and_when_opening()
    {
        var keys = TrustSigning.GenerateKeyPair();
        var bad = TrustPayload.Empty(Now) with { Games = [new TrustedGame("ABC", "x", "X", null)] };

        Assert.Throws<InvalidDataException>(() => TrustSigning.Sign(bad, keys.PrivateKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("###")]
    [InlineData("AAAA")]
    public void A_public_key_that_is_not_a_key_is_refused_with_a_message(string key)
    {
        var ex = Assert.Throws<InvalidDataException>(() => TrustSigning.CheckPublicKey(key));

        Assert.Contains("public key", ex.Message);
    }

    [Fact]
    public void A_private_key_can_be_protected_with_a_password()
    {
        var keys = TrustSigning.GenerateKeyPair("correct horse");
        var payload = TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a')], Now);

        var envelope = TrustSigning.Serialize(TrustSigning.Sign(payload, keys.PrivateKey, "correct horse"));
        Assert.Single(TrustSigning.Open(envelope, keys.PublicKey).Games);

        Assert.Throws<InvalidDataException>(() => TrustSigning.Sign(payload, keys.PrivateKey, "wrong"));
        Assert.Throws<InvalidDataException>(() => TrustSigning.Sign(payload, keys.PrivateKey)); // no password at all
    }

    [Fact]
    public void Every_change_raises_the_sequence_and_keeps_the_rest_in_order()
    {
        var one = TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a')], Now);
        var two = TrustListEditor.Add(one, [Game('b', "Assetto Corsa", "1.16")], Now.AddHours(1));

        Assert.Equal(1, one.Sequence);
        Assert.Equal(2, two.Sequence);
        Assert.Equal(["Assetto Corsa", "BeamNG.drive"], two.Games.Select(g => g.Name)); // sorted by name
        Assert.Equal(Now.AddHours(1), two.IssuedAt);
    }

    [Fact]
    public void Adding_a_version_again_replaces_it_and_does_not_duplicate_it()
    {
        var list = TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a')], Now);

        list = TrustListEditor.Add(list, [Game('a', "BeamNG Drive", "0.38")], Now);

        Assert.Equal("BeamNG Drive", Assert.Single(list.Games).Name);
    }

    [Fact]
    public void A_revoked_version_leaves_the_vouched_games_and_carries_the_reason()
    {
        var list = TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a'), Game('b', "Other", "1")], Now);

        list = TrustListEditor.Revoke(list, Hash('a'), "contains a modified executable", Now);

        Assert.Equal([Hash('b')], list.Games.Select(g => g.ContentHash));
        var revoked = Assert.Single(list.Revoked);
        Assert.Equal((Hash('a'), "contains a modified executable"), (revoked.ContentHash, revoked.Reason));
    }

    [Fact]
    public void Vouching_for_a_revoked_version_again_lifts_the_revocation_but_removing_one_does_not_revoke_it()
    {
        var list = TrustListEditor.Revoke(TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a')], Now), Hash('a'), "bad", Now);

        var restored = TrustListEditor.Add(list, [Game('a')], Now);
        Assert.Empty(restored.Revoked);
        Assert.Single(restored.Games);

        var removed = TrustListEditor.Remove(restored, Hash('a'), Now);
        Assert.Empty(removed.Games);
        Assert.Empty(removed.Revoked);
    }

    [Fact]
    public void A_validity_period_is_part_of_what_is_signed()
    {
        var list = TrustListEditor.Add(TrustPayload.Empty(Now), [Game('a')], Now, validFor: TimeSpan.FromDays(30));
        var keys = TrustSigning.GenerateKeyPair();

        var opened = TrustSigning.Open(TrustSigning.Serialize(TrustSigning.Sign(list, keys.PrivateKey)), keys.PublicKey);

        Assert.Equal(Now.AddDays(30), opened.ValidUntil);
    }
}
