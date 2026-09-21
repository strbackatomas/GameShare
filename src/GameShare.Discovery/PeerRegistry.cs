using System.Net;

namespace GameShare.Discovery;

/// <summary>Another agent on the LAN. The address is what we saw on the wire, not what the peer claims.</summary>
public sealed record PeerInfo(
    string MachineId,
    string MachineName,
    IPAddress Address,
    int AgentPort,
    int ProtocolVersion,
    DateTimeOffset LastSeen);

public enum PeerEventKind { Joined, Changed, Left }

public enum PeerLeftReason { Timeout, Goodbye }

/// <param name="Previous">The old state for <see cref="PeerEventKind.Changed"/>.</param>
/// <param name="Reason">Set for <see cref="PeerEventKind.Left"/>.</param>
public sealed record PeerEvent(PeerEventKind Kind, PeerInfo Peer, PeerInfo? Previous = null, PeerLeftReason? Reason = null);

/// <summary>
/// Who is on the LAN right now. Pure state machine with no I/O and no clock of its own,
/// so join, leave, change and timeout behaviour can be tested exactly.
/// </summary>
public sealed class PeerRegistry
{
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <returns>An event when the message is news, null for a plain heartbeat.</returns>
    public PeerEvent? OnHello(DiscoveryMessage message, IPAddress source, DateTimeOffset now)
    {
        var peer = new PeerInfo(message.MachineId, message.MachineName, source, message.AgentPort, message.Version, now);
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer.MachineId, out var old))
            {
                _peers[peer.MachineId] = peer;
                return new PeerEvent(PeerEventKind.Joined, peer);
            }

            _peers[peer.MachineId] = peer; // always refresh LastSeen
            bool changed = !old.Address.Equals(peer.Address) || old.AgentPort != peer.AgentPort || old.MachineName != peer.MachineName;
            return changed ? new PeerEvent(PeerEventKind.Changed, peer, old) : null;
        }
    }

    public PeerEvent? OnGoodbye(string machineId)
    {
        lock (_gate)
        {
            return _peers.Remove(machineId, out var old)
                ? new PeerEvent(PeerEventKind.Left, old, Reason: PeerLeftReason.Goodbye)
                : null;
        }
    }

    /// <summary>Removes peers not heard from since <paramref name="cutoff"/>.</summary>
    public IReadOnlyList<PeerEvent> ExpireOlderThan(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            var expired = _peers.Values.Where(p => p.LastSeen < cutoff).ToList();
            foreach (var p in expired) _peers.Remove(p.MachineId);
            return expired.Select(p => new PeerEvent(PeerEventKind.Left, p, Reason: PeerLeftReason.Timeout)).ToList();
        }
    }

    public IReadOnlyList<PeerInfo> Snapshot()
    {
        lock (_gate) return _peers.Values.OrderBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
