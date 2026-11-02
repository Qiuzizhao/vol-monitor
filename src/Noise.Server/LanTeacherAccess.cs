using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Noise.Server;

public static class LanTeacherAccess
{
    public static bool IsAllowed(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return LocalTeacherAccess.IsLocal(context);
        if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
            (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || uri.Scheme != context.Request.Scheme ||
             !uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))) return false;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : ip.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }
}

public sealed class LanTeacherAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
        LanTeacherAccess.IsAllowed(Context)
            ? AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "teacher")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
}
