package extension.bridge.server

import eu.kanade.tachiyomi.source.model.Page
import eu.kanade.tachiyomi.source.online.HttpSource
import eu.kanade.tachiyomi.network.interceptor.CloudflareInterceptor
import eu.kanade.tachiyomi.network.interceptor.NoBrowser
import okhttp3.Dns
import okhttp3.Interceptor
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.UnknownHostException

/**
 * Fetching a URL a USER stored (a series cover sent with POST /api/serie) with an
 * extension's headers. The app checks the host before asking, but OkHttp then
 * resolves it again (DNS rebinding) and follows redirects, so the check is
 * repeated here at every connect: the resolver only hands out public addresses,
 * and a network interceptor re-checks the address actually connected to on every
 * hop (OkHttp skips the resolver for IP literals). Mirrors the app's
 * OutboundUrlGuard.IsPublicAddress.
 */
internal object PublicOnlyFetch {
    private val publicDns = object : Dns {
        override fun lookup(hostname: String): List<InetAddress> {
            val ok = Dns.SYSTEM.lookup(hostname).filter(::isPublic)
            if (ok.isEmpty()) throw UnknownHostException("$hostname is not a public internet address")
            return ok
        }
    }

    private val connectedToPublic = Interceptor { chain ->
        val route = chain.connection()?.route()
        val target = route?.socketAddress
        val checked = if (route != null && route.proxy.type() == Proxy.Type.DIRECT && target is InetSocketAddress && target.address != null) {
            isPublic(target.address)
        } else {
            // Through a proxy the socket is the proxy's; judge the target name instead.
            runCatching { InetAddress.getAllByName(chain.request().url.host).all(::isPublic) }.getOrDefault(false)
        }
        if (!checked) throw IOException("Refusing to fetch from ${chain.request().url.host}: it is not a public internet address.")
        chain.proceed(chain.request())
    }

    private val imageRequest by lazy {
        HttpSource::class.java.getDeclaredMethod("imageRequest", Page::class.java).apply { isAccessible = true }
    }

    fun image(s: HttpSource, p: Page): Response {
        // Tagged so the Cloudflare interceptor never hands this request to the
        // browser (a fake challenge would load the host's page in Chromium), and
        // the interceptor is dropped from this client as well.
        val request = (imageRequest.invoke(s, p) as Request).newBuilder()
            .tag(NoBrowser::class.java, NoBrowser)
            .build()
        val client = s.client.newBuilder()
            .apply { interceptors().removeAll { it is CloudflareInterceptor } }
            .dns(publicDns)
            .addNetworkInterceptor(connectedToPublic)
            .build()
        return client.newCall(request).execute()
    }

    fun readCapped(body: ResponseBody, max: Long): ByteArray {
        if (body.contentLength() > max) throw IOException("Response is larger than ${max / (1024 * 1024)} MB.")
        val out = ByteArrayOutputStream()
        body.byteStream().use { input ->
            val buf = ByteArray(81920)
            var total = 0L
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                total += n
                if (total > max) throw IOException("Response is larger than ${max / (1024 * 1024)} MB.")
                out.write(buf, 0, n)
            }
        }
        return out.toByteArray()
    }

    fun isPublic(address: InetAddress): Boolean {
        val b = address.address
        if (address is Inet4Address) return isPublicV4(b)
        if (address !is Inet6Address || b.size != 16) return false
        val u = b.map { it.toInt() and 0xFF }
        if (address.isLinkLocalAddress || address.isSiteLocalAddress || address.isMulticastAddress) return false
        if ((u[0] and 0xFE) == 0xFC) return false                                        // fc00::/7 unique local
        if (u.subList(0, 10).all { it == 0 } && u[10] == 0xFF && u[11] == 0xFF)            // ::ffff:0:0/96 mapped
            return isPublicV4(b.copyOfRange(12, 16))
        if (u.subList(0, 12).all { it == 0 }) return false                                // ::/96 incl. ::, ::1
        if (u.subList(0, 8).all { it == 0 } && u[8] == 0xFF && u[9] == 0xFF && u[10] == 0 && u[11] == 0)
            return isPublicV4(b.copyOfRange(12, 16))                                      // ::ffff:0:0:0/96 translated
        if (u[0] == 0x20 && u[1] == 0x02) return isPublicV4(b.copyOfRange(2, 6))           // 6to4
        if (u[0] == 0x00 && u[1] == 0x64 && u[2] == 0xFF && u[3] == 0x9B) {
            if (u.subList(4, 12).all { it == 0 }) return isPublicV4(b.copyOfRange(12, 16)) // NAT64 64:ff9b::/96
            return false                                                                  // 64:ff9b:1::/48 local NAT64
        }
        if (u[0] == 0x20 && u[1] == 0x01 && u[2] == 0 && u[3] == 0) return false          // Teredo
        return true
    }

    private fun isPublicV4(b: ByteArray): Boolean {
        val u = b.map { it.toInt() and 0xFF }
        return !(u[0] == 0 || u[0] == 10 || u[0] == 127
            || (u[0] == 100 && (u[1] and 0xC0) == 64)
            || (u[0] == 169 && u[1] == 254)
            || (u[0] == 172 && (u[1] and 0xF0) == 16)
            || (u[0] == 192 && u[1] == 168)
            || (u[0] == 192 && u[1] == 0 && u[2] == 0)
            || (u[0] == 198 && (u[1] and 0xFE) == 18)
            || u[0] >= 224)
    }
}
