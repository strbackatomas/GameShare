using System.Net;
using GameShare.Discovery;

namespace GameShare.Agent;

/// <summary>
/// The agent has two listeners with different trust, and this keeps them apart no matter what a client sends.
/// Control API and event hub: only the loopback port, only callers on this machine.
/// Peer API: only its own port, only callers on a private network.
/// The check uses the socket the request arrived on, never the Host header, which a client controls.
/// The control API also refuses what a web page in a browser on this PC can send: anything with an Origin header, and a Host that is not
/// this machine's own name for itself, which is what a page gets with DNS rebinding (its domain turned into 127.0.0.1 after it loaded).
/// GameShare's own programs send neither.
/// </summary>
public sealed class AccessGuard
{
    private readonly RequestDelegate _next;
    private readonly AgentOptions _options;
    private readonly ILogger<AccessGuard> _log;

    public AccessGuard(RequestDelegate next, AgentOptions options, ILogger<AccessGuard> log)
    {
        _next = next;
        _options = options;
        _log = log;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        int port = context.Connection.LocalPort;
        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;

        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hub"))
        {
            if (port != _options.LocalApiPort || !IPAddress.IsLoopback(remote))
                return Deny(context, remote, "control API is local only");
            if (context.Request.Headers.Origin.Count > 0)
                return Deny(context, remote, $"control API does not answer web pages (Origin {context.Request.Headers.Origin})");
            if (!IsLoopbackName(context.Request.Host.Host))
                return Deny(context, remote, $"control API answers only to a loopback name, not Host {context.Request.Host}");
        }
        else if (path.StartsWithSegments("/peer"))
        {
            if (port != _options.PeerApiPort)
                return Deny(context, remote, "peer API is not served on this port");
            if (_options.LanOnly && !LanAddress.IsPrivate(remote))
                return Deny(context, remote, "peer API only answers the local network");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return _next(context);
    }

    /// <summary>127.0.0.1, localhost or ::1: the names GameShare's programs use. Any other name came from a browser.</summary>
    private static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private Task Deny(HttpContext context, IPAddress remote, string reason)
    {
        _log.LogWarning("Refused {Method} {Path} from {Remote} on port {Port}: {Reason}",
            context.Request.Method, context.Request.Path, remote, context.Connection.LocalPort, reason);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
