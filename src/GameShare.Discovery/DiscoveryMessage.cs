using System.Text.Json;

namespace GameShare.Discovery;

/// <summary>
/// The only thing agents say to each other over UDP. Small on purpose: everything else
/// (games, manifests, torrents) is fetched over the agent HTTP API once a peer is known.
/// </summary>
public sealed record DiscoveryMessage(string Type, int Version, string MachineId, string MachineName, int AgentPort)
{
    public const string Hello = "hello";
    public const string Goodbye = "bye";
    public const int CurrentVersion = 1;

    /// <summary>Datagrams larger than this are dropped unread.</summary>
    public const int MaxSize = 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <returns>True when the datagram is a well-formed message. Otherwise <paramref name="error"/> says why.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out DiscoveryMessage message, out string error)
    {
        message = null!;
        if (data.Length == 0 || data.Length > MaxSize) { error = $"size {data.Length} outside 1..{MaxSize}"; return false; }

        DiscoveryMessage? m;
        try { m = JsonSerializer.Deserialize<DiscoveryMessage>(data, Json); }
        catch (JsonException ex) { error = $"not valid JSON: {ex.Message}"; return false; }

        if (m is null) { error = "empty JSON"; return false; }
        if (m.Type is not (Hello or Goodbye)) { error = $"unknown type '{m.Type}'"; return false; }
        if (string.IsNullOrWhiteSpace(m.MachineId) || m.MachineId.Length > 64) { error = "bad machineId"; return false; }
        if (string.IsNullOrWhiteSpace(m.MachineName) || m.MachineName.Length > 64) { error = "bad machineName"; return false; }
        if (m.AgentPort is < 1 or > 65535) { error = $"bad agentPort {m.AgentPort}"; return false; }

        message = m;
        error = "";
        return true;
    }
}
