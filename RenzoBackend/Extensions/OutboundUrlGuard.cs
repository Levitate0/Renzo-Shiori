using System.Net;
using System.Net.Sockets;

namespace RenzoBackend.Extensions
{
    /// <summary>
    /// Keeps server-side fetches of stored URLs (covers) on the public internet.
    ///
    /// 2026-11-15 audit: a series added with POST /api/serie keeps its
    /// thumbnailUrl as sent, and GET /api/image/{key} fetched it and handed the
    /// body back. The container runs with network_mode: host, so
    /// "http://127.0.0.1:9835/..." or a LAN address read the sidecar, the OAuth
    /// proxy or anything else on the network. Covers come from public CDNs, so
    /// a loopback/private/link-local target is never legitimate here.
    ///
    /// Checking the name before the request is not enough on its own: DNS can
    /// answer "public" to the check and "127.0.0.1" to the connect (rebinding),
    /// and a redirect can point anywhere. So the handler from
    /// <see cref="CreateHandler"/> resolves the host itself and connects only to
    /// a checked address, on the first hop and on every redirect.
    /// </summary>
    public static class OutboundUrlGuard
    {
        /// <summary>No cover is anywhere near this; it stops a URL from filling memory.</summary>
        public const long MaxImageBytes = 20L * 1024 * 1024;

        /// <summary>
        /// True for an address on the public internet; false for loopback,
        /// RFC 1918, link-local (incl. cloud metadata 169.254.169.254), CGNAT,
        /// unique-local/site-local IPv6, multicast, unspecified and reserved.
        /// IPv4 carried inside IPv6 (mapped, 6to4, NAT64) is judged as IPv4.
        /// </summary>
        public static bool IsPublicAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return !(b[0] == 0                              // 0.0.0.0/8 "this network"
                    || b[0] == 10                               // 10/8
                    || b[0] == 127                              // loopback
                    || (b[0] == 100 && (b[1] & 0xC0) == 64)     // 100.64/10 CGNAT
                    || (b[0] == 169 && b[1] == 254)             // link-local, metadata
                    || (b[0] == 172 && (b[1] & 0xF0) == 16)     // 172.16/12
                    || (b[0] == 192 && b[1] == 168)             // 192.168/16
                    || (b[0] == 192 && b[1] == 0 && b[2] == 0)  // 192.0.0/24 protocol assignments
                    || (b[0] == 198 && (b[1] & 0xFE) == 18)     // 198.18/15 benchmarking
                    || b[0] >= 224);                            // multicast, reserved, broadcast
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
                    return false;
                // ::/96 covers ::, ::1 and the deprecated IPv4-compatible form.
                if (b.Take(12).All(x => x == 0))
                    return false;
                if (b.Take(8).All(x => x == 0) && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0)
                    return IsPublicAddress(new IPAddress(b[12..16])); // IPv4-translated ::ffff:0:0:0/96
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
                    return false;                               // local-use NAT64 64:ff9b:1::/48 reaches anything
                if (b[0] == 0x20 && b[1] == 0x02)               // 6to4 2002::/16
                    return IsPublicAddress(new IPAddress(b[2..6]));
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))
                    return IsPublicAddress(new IPAddress(b[12..16])); // NAT64 64:ff9b::/96
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
                    return false;                               // Teredo 2001::/32 tunnels to anywhere
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="url"/> is http(s) and every address its host
        /// resolves to is public. For handing a URL to something that fetches it
        /// itself (the Cloudflare solver, the sidecar), where the connect cannot be
        /// pinned; our own fetches go through <see cref="CreateHandler"/>.
        /// </summary>
        public static async Task<bool> IsPublicHttpUrlAsync(string? url, CancellationToken token = default)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return false;
            IPAddress[] addresses = await ResolveAsync(uri.IdnHost, token).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }

        private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            string bare = host.Trim('[', ']');
            if (IPAddress.TryParse(bare, out IPAddress? literal))
                return [literal];
            try
            {
                return await Dns.GetHostAddressesAsync(bare, token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return [];
            }
        }

        /// <summary>
        /// A handler that only ever connects to public addresses. Proxies are off
        /// on purpose: through a proxy the connect would go to the proxy, and the
        /// target would never be checked.
        /// </summary>
        public static SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = ConnectToPublicAddressAsync,
        };

        private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            string host = context.DnsEndPoint.Host;
            IPAddress[] allowed = (await ResolveAsync(host, token).ConfigureAwait(false)).Where(IsPublicAddress).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"Refusing to fetch from {host}: it is not a public internet address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Copies at most <paramref name="maxBytes"/>; throws InvalidDataException
        /// past that rather than buffering whatever a URL chooses to send.
        /// </summary>
        public static async Task CopyCappedAsync(Stream source, Stream destination, long maxBytes, CancellationToken token)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException($"Response is larger than {maxBytes / (1024 * 1024)} MB.");
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
    }
}
