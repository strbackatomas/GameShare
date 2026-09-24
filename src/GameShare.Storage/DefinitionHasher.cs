using System.Security.Cryptography;
using System.Text;
using GameShare.Protocol;

namespace GameShare.Storage;

/// <summary>
/// A fingerprint of a game definition, so the administrator can sign what a game's gameshare.json says as well as its files.
/// The definition is not part of the content hash (editing how a game starts must not make it a different game), and it arrives
/// from other PCs, so without this anyone could hand out a verified game with their own setup steps.
/// </summary>
public static class DefinitionHasher
{
    /// <summary>SHA-256, lowercase hex, over the definition serialised the one way GameShare serialises it, with its maps sorted.</summary>
    public static string Compute(GameDefinition definition)
    {
        var canonical = definition with
        {
            Provides = new SortedDictionary<string, RedistPackage>(definition.Provides.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(GameShareJson.Serialize(canonical))));
    }

    public static bool Same(GameDefinition? a, GameDefinition? b) =>
        a is null || b is null ? a is null && b is null : Compute(a) == Compute(b);
}
