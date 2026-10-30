using System.Net;

namespace Noise.Server;

public static class LocalTeacherAccess
{
    public static bool IsLocal(HttpContext context)
    {
        // Check the actual peer and host together. Forwarded headers must never
        // grant access, and a foreign hostname must not enable DNS rebinding.
        var peer = context.Connection.RemoteIpAddress;
        var host = context.Request.Host.Host.Trim('[', ']');
        // WebSockets do not use browser CORS checks. An external page must not
        // read the local dashboard by opening a socket to the loopback listener.
        if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
            (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri) ||
             !originUri.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase) ||
             !originUri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))) return false;
        return peer is not null && IPAddress.IsLoopback(peer) &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
