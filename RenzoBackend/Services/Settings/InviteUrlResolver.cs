using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using RenzoBackend.Models.Dto;

namespace RenzoBackend.Services.Settings;

/// <summary>
/// Picks the base URL used to build user-facing links that leave the server
/// process (invite links, set-password links, OPDS URLs) — anywhere a
/// hardcoded "http://localhost:9833" would be wrong for anyone accessing the
/// instance from off-box.
///
/// Priority:
///   1. Settings.ExternalDomain, if the owner explicitly set one — this field
///      exists specifically to declare "this is my public URL" and always wins.
///   2. Otherwise, scan Settings.AllowedOrigins (what's actually configured
///      for CORS, so it reflects real, reachable origins) for the first entry
///      that looks like a real public domain (not an IP, not localhost).
///   3. Otherwise, the first AllowedOrigins entry that's a private/LAN IP
///      (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, link-local, ULA).
///   4. Otherwise, the first AllowedOrigins entry that's localhost/loopback.
///   5. Otherwise, if an HttpRequest is available, its own scheme+host (the
///      address the caller actually used to reach us right now).
///   6. Absolute last resort: http://localhost:9833 (the container's default).
/// </summary>
public static class InviteUrlResolver
{
    private const string Fallback = "http://localhost:9833";

    public static string ResolveBaseUrl(EditableSettingsDto settings, HttpRequest? request = null)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExternalDomain))
            return settings.ExternalDomain.Trim().TrimEnd('/');

        string? publicOrigin = null;
        string? privateIpOrigin = null;
        string? localhostOrigin = null;

        foreach (string? raw in settings.AllowedOrigins ?? [])
        {
            string origin = raw?.Trim().TrimEnd('/') ?? string.Empty;
            if (origin.Length == 0 || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
                continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                continue;

            switch (ClassifyHost(uri.Host))
            {
                case HostKind.Localhost:
                    localhostOrigin ??= origin;
                    break;
                case HostKind.PrivateIp:
                    privateIpOrigin ??= origin;
                    break;
                default:
                    publicOrigin ??= origin;
                    break;
            }
        }

        if (publicOrigin != null) return publicOrigin;
        if (privateIpOrigin != null) return privateIpOrigin;
        if (localhostOrigin != null) return localhostOrigin;

        if (request != null && request.Host.HasValue)
            return $"{request.Scheme}://{request.Host}";

        return Fallback;
    }

    private enum HostKind { Public, PrivateIp, Localhost }

    private static HostKind ClassifyHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return HostKind.Localhost;

        if (!IPAddress.TryParse(host, out IPAddress? ip))
            return HostKind.Public; // a real domain name

        if (IPAddress.IsLoopback(ip))
            return HostKind.Localhost;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            bool isPrivate =
                b[0] == 10 ||                              // 10.0.0.0/8
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||// 172.16.0.0/12
                (b[0] == 192 && b[1] == 168) ||             // 192.168.0.0/16
                (b[0] == 169 && b[1] == 254);                // 169.254.0.0/16 link-local
            return isPrivate ? HostKind.PrivateIp : HostKind.Public;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                return HostKind.PrivateIp;
            byte[] b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) // fc00::/7 unique local address
                return HostKind.PrivateIp;
            return HostKind.Public;
        }

        return HostKind.Public;
    }
}
