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

        // Nothing configured and no request to learn from: detect this machine's
        // own LAN address rather than emitting localhost, which is wrong for
        // every recipient of the link by definition — an invite or OPDS URL is
        // only useful from another device.
        string? detected = DetectLanOrigin(request);
        if (detected != null)
            return detected;

        return Fallback;
    }

    /// <summary>
    /// This host's LAN IPv4, as an origin. Any private range counts — 10/8 and
    /// 172.16/12 are as common on real networks as 192.168/16, so nothing here
    /// assumes a particular one.
    ///
    /// Interfaces are ranked rather than "first wins". A server host routinely
    /// has many up interfaces — Docker bridges, libvirt's virbr0, LXC, ZeroTier,
    /// VPN tunnels — and most are reachable by nothing outside the box, so
    /// picking the wrong one yields a link that silently goes nowhere.
    ///
    /// The deciding signal is the DEFAULT ROUTE: the interface carrying a
    /// gateway is the one that actually talks to the rest of the network. That
    /// beats matching interface names, which is guesswork that fails on exactly
    /// the machines this matters for (here, name-matching would have ranked an
    /// LXC bridge and a ZeroTier link alongside the real LAN, and the only
    /// 192.168 address present belongs to libvirt).
    /// </summary>
    private static string? DetectLanOrigin(HttpRequest? request)
    {
        try
        {
            int port = request?.Host.Port ?? 9833;
            string scheme = request?.Scheme ?? Uri.UriSchemeHttp;

            var candidates = new List<(int rank, string ip)>();
            foreach (System.Net.NetworkInformation.NetworkInterface nic in
                     System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                System.Net.NetworkInformation.IPInterfaceProperties props = nic.GetIPProperties();

                // Carries a real gateway => this is the way off the machine.
                bool routes = props.GatewayAddresses.Any(g =>
                    g.Address != null &&
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));

                bool virtualish =
                    nic.Name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("br-", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("veth", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("lxcbr", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("vmnet", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("vboxnet", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("zt", StringComparison.OrdinalIgnoreCase)      // ZeroTier
                    || nic.Name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)      // WireGuard
                    || nic.Name.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.StartsWith("tap", StringComparison.OrdinalIgnoreCase);

                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation addr in
                         props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    if (ClassifyHost(addr.Address.ToString()) != HostKind.PrivateIp)
                        continue;

                    byte[] b = addr.Address.GetAddressBytes();
                    bool linkLocal = b[0] == 169 && b[1] == 254;      // no DHCP; last resort
                    // A virtual interface never wins on the gateway signal: VPN and
                    // overlay links (ZeroTier, WireGuard) legitimately carry routes
                    // of their own, and an address only that overlay can reach is
                    // no better than localhost for someone opening the link.
                    int rank = linkLocal ? 4
                             : virtualish ? 3
                             : routes ? 0                              // owns the default route
                             : 1;
                    candidates.Add((rank, addr.Address.ToString()));
                }
            }

            (int rank, string ip) best = candidates.OrderBy(c => c.rank).FirstOrDefault();
            return best.ip == null ? null : $"{scheme}://{best.ip}:{port}";
        }
        catch
        {
            return null;   // detection is best-effort; the caller still has Fallback
        }
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
