package eu.kanade.tachiyomi.network.interceptor

import android.app.Application
import android.content.Context
import eu.kanade.tachiyomi.network.NetworkHelper
import extension.bridge.logging.androidCompatLogger
import extension.bridge.toCefCookie
import okhttp3.HttpUrl
import okhttp3.Request
import okhttp3.Response
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import uy.kohesive.injekt.injectLazy
import java.util.concurrent.ConcurrentHashMap

/**
 * Shiori's Cloudflare layer: what a phone does for an extension, rebuilt for
 * a server with no person in front of it.
 *
 * On Android a Cloudflare challenge is solved by a person in a WebView, and
 * WebView and OkHttp share ONE cookie store and ONE user agent, so the
 * clearance just works everywhere. Here none of that held:
 *
 *  - the external solver (a stealth Firefox) issues clearances bound to ITS
 *    user agent, and the old code made that the global user agent, so every
 *    source loaded afterwards froze a Firefox string into its headers while
 *    sources loaded before kept Chrome/120 — which one a source got depended
 *    on load order, and half of them sent a user agent their clearance was
 *    not issued to;
 *  - the HTTP cookie jar and Chromium kept separate stores, so a clearance
 *    one side earned never reached the other.
 *
 * So: the user agent a clearance was issued to is recorded PER SITE and sent
 * only to that site (see UserAgentInterceptor); solved cookies go into the
 * jar AND Chromium; and the global user agent stays the honest Chromium one
 * (see NetworkHelper). The solve itself still goes to the external solver,
 * because a headless Chromium does not pass a managed challenge on its own
 * (measured: mkissa.to, comix.to and mangaball.com sat on "Just a moment..."
 * for 40 s each), while it does accept a clearance once it has one.
 */
object ChallengeSolver {
    private val logger = androidCompatLogger(ChallengeSolver::class.java)
    private val network: NetworkHelper by injectLazy()
    private val prefs by lazy {
        Injekt.get<Application>().getSharedPreferences("cf_clearance_ua", Context.MODE_PRIVATE)
    }

    private data class Clearance(val userAgent: String, val expiresAt: Long)

    private val clearances = ConcurrentHashMap<String, Clearance>()

    private fun key(host: String) = host.lowercase().removePrefix("www.")

    /**
     * The user agent the live clearance for [host] (or a parent domain — an
     * image CDN subdomain rides its site's clearance) was issued to.
     */
    fun userAgentFor(host: String): String? {
        var h = key(host)
        while (true) {
            val c = clearances[h] ?: loadClearance(h)
            if (c != null) {
                if (c.expiresAt > System.currentTimeMillis()) return c.userAgent
                forget(h)
            }
            val dot = h.indexOf('.')
            if (dot < 0 || h.indexOf('.', dot + 1) < 0) return null
            h = h.substring(dot + 1)
        }
    }

    private fun loadClearance(h: String): Clearance? {
        val raw = runCatching { prefs.getString(h, null) }.getOrNull() ?: return null
        val sep = raw.indexOf('|')
        if (sep < 0) return null
        val exp = raw.substring(0, sep).toLongOrNull() ?: return null
        return Clearance(raw.substring(sep + 1), exp).also { clearances[h] = it }
    }

    private fun forget(h: String) {
        clearances.remove(h)
        runCatching { prefs.edit().remove(h).apply() }
    }

    /** Records that [host]'s clearance belongs to [userAgent] until [expiresAt]. */
    fun record(host: String, userAgent: String, expiresAt: Long) {
        if (userAgent.isBlank()) return
        val h = key(host)
        clearances[h] = Clearance(userAgent, expiresAt)
        runCatching { prefs.edit().putString(h, "$expiresAt|$userAgent").apply() }
    }

    /** A Cloudflare challenge (or block) rather than the page. */
    fun isChallenge(response: Response): Boolean =
        response.code in CHALLENGE_CODES &&
            response.header("Server")?.lowercase()?.startsWith("cloudflare") == true

    fun isChallengeTitle(title: String?): Boolean =
        title != null && (
            title.contains("Just a moment", ignoreCase = true) ||
                title.contains("Attention Required", ignoreCase = true) ||
                title.startsWith("Checking your browser", ignoreCase = true)
            )

    /**
     * Solves [url]'s challenge with the external solver. Cookies land in the
     * jar and in Chromium, and the user agent they belong to is recorded for
     * this site only. True when solved — including by a concurrent caller
     * while this one waited for the site's turn.
     */
    suspend fun solve(url: HttpUrl): Boolean {
        val before = CFClearance.clearance(url)
        val solved = CFClearance.resolveWithFlareSolver(
            Request.Builder().url(url).build(),
            onlyCookies = true,
            clearanceBefore = before,
        ) ?: return true
        if (solved.solution.cookies.none { it.name == "cf_clearance" } && solved.solution.status !in 200..299) {
            logger.warn { "Solver could not clear ${url.host}: ${solved.message.take(200)}" }
            return false
        }
        CFClearance.storeSolution(solved)
        logger.info { "Cleared Cloudflare for ${url.host}" }
        return true
    }

    /** Copies the jar's cookies for [url] into Chromium's store (a no-op until Chromium runs). */
    fun pushToChromium(url: HttpUrl) {
        runCatching {
            val cef = org.cef.network.CefCookieManager.getGlobalManager() ?: return
            for (c in network.cookieStore.get(url)) {
                cef.setCookie("https://" + c.domain, c.toCefCookie())
            }
        }.onFailure { logger.debug { "Could not push cookies for ${url.host} into Chromium: ${it.message}" } }
    }

    private val CHALLENGE_CODES = setOf(403, 503)
}
