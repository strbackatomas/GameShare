using System.Net;

namespace GameShare.Discovery;

/// <param name="Source">Address the datagram really came from.</param>
public readonly record struct Datagram(byte[] Payload, IPAddress Source);

/// <summary>
/// "Say something to everyone on the LAN" and "hear what others say". Exists so the discovery logic
/// can be tested with an in-memory bus and so real UDP stays a small, replaceable piece.
/// </summary>
public interface IDatagramTransport : IDisposable
{
    ValueTask SendAsync(byte[] payload, CancellationToken cancellationToken);

    /// <summary>Datagrams from everyone, including this machine's own. Ends when cancelled or disposed.</summary>
    IAsyncEnumerable<Datagram> ReceiveAsync(CancellationToken cancellationToken);
}
