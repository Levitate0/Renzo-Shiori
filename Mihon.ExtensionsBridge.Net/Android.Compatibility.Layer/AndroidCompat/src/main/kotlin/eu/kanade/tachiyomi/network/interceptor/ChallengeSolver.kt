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
import kotlinx.coroutines.sync.withLock

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
 * (see NetworkHelper).
 *
 * The solve is Shiori's own (BrowserSolver): the embedded Chromium at a real
 * window size, waiting for the challenge to finish by itself. The October
 * measurement that it "does not pass a managed challenge" ran in a 1x1 px
 * window (JCEF never resized its off-screen view, fixed in bad1686) and with
 * the external solver's user agent forced onto the page. Sites that let
 * Chromium through without any challenge get their requests carried through
 * Chromium (ChromiumTransport). The external FlareSolverr backend is only a
 * fallback, and only when it is switched on.
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
        if (isCloudflareOwn(host)) return null
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
        if (userAgent.isBlank() || isCloudflareOwn(host)) return
        val h = key(host)
        clearances[h] = Clearance(userAgent, expiresAt)
        runCatching { prefs.edit().putString(h, "$expiresAt|$userAgent").apply() }
    }

    /**
     * Cloudflare's own hosts (challenges.cloudflare.com and friends) never get
     * a site's user agent: a parent-domain walk once handed every Turnstile
     * frame the external solver's Firefox string.
     */
    private fun isCloudflareOwn(host: String): Boolean {
        val h = key(host)
        return h == "cloudflare.com" || h.endsWith(".cloudflare.com")
    }

    enum class Kind { NONE, CHALLENGE, BLOCKED }

    /**
     * What a response is. Strict: a 403/503 the ORIGIN sent through Cloudflare
     * is just a 403/503, and must not trigger a solve, a dropped clearance or a
     * verdict. A challenge carries `cf-mitigated: challenge` or Cloudflare's
     * challenge page; a block is Cloudflare's "you have been blocked" page.
     */
    fun classify(response: Response): Kind {
        if (response.code !in CHALLENGE_CODES) return Kind.NONE
        if (response.header("cf-mitigated").equals("challenge", ignoreCase = true)) return Kind.CHALLENGE
        if (response.header("Server")?.lowercase()?.startsWith("cloudflare") != true) return Kind.NONE
        val head = runCatching { response.peekBody(64 * 1024).string() }.getOrDefault("")
        return when {
            BLOCK_MARKERS.any { head.contains(it, ignoreCase = true) } -> Kind.BLOCKED
            CHALLENGE_MARKERS.any { head.contains(it, ignoreCase = true) } -> Kind.CHALLENGE
            else -> Kind.NONE
        }
    }

    /** A Cloudflare challenge (strict; see [classify]). */
    fun isChallenge(response: Response): Boolean = classify(response) == Kind.CHALLENGE

    // Not the bare /cdn-cgi/challenge-platform/ path: Bot Fight Mode injects
    // its detection script (…/scripts/jsd/main.js) into ordinary pages too.
    private val CHALLENGE_MARKERS = listOf("<title>Just a moment", "_cf_chl_opt", "/cdn-cgi/challenge-platform/h/")
    private val BLOCK_MARKERS = listOf("Sorry, you have been blocked", "cf-error-details", "<title>Attention Required! | Cloudflare")

    // ── verdicts: what the built-in solver learned about a site ──────────

    enum class Verdict {
        /** Chromium is let through; plain HTTP is not. Carry requests through Chromium. */
        TRANSPORT,

        /** The challenge did not finish by itself: a person has to do it. */
        NEEDS_PERSON,

        /** Cloudflare blocks this server outright. */
        BLOCKED,
    }

    private data class Held(val verdict: Verdict, val until: Long)

    private val verdicts = ConcurrentHashMap<String, Held>()

    /** RENZO_CF_BUILTIN=0 turns the built-in solver off: back to the external solver alone. */
    val builtInEnabled: Boolean = System.getenv("RENZO_CF_BUILTIN") != "0"

    fun verdict(host: String): Verdict? {
        val h = verdicts[key(host)] ?: return null
        if (h.until > System.currentTimeMillis()) return h.verdict
        verdicts.remove(key(host), h)
        return null
    }

    fun setVerdict(host: String, verdict: Verdict) {
        val ttl = when (verdict) {
            Verdict.TRANSPORT -> 6 * 60 * 60_000L
            Verdict.NEEDS_PERSON, Verdict.BLOCKED -> 30 * 60_000L
        }
        verdicts[key(host)] = Held(verdict, System.currentTimeMillis() + ttl)
        logger.info { "Cloudflare verdict for ${key(host)}: $verdict" }
    }

    fun clearVerdict(host: String) {
        verdicts.remove(key(host))
    }

    /** Verdicts and recorded user agents, for /debug/cloudflare (no cookie values). */
    fun describe(): Map<String, String> {
        val now = System.currentTimeMillis()
        val out = sortedMapOf<String, String>()
        verdicts.forEach { (h, v) -> out[h] = "${v.verdict} for ${(v.until - now) / 60_000} min" }
        runCatching { prefs.all }.getOrNull()?.forEach { (h, raw) ->
            val s = raw?.toString() ?: return@forEach
            val ua = s.substringAfter('|')
            val tag = if (ua == CFClearance.currentUserAgent()) "Chromium UA" else "other UA: ${ua.take(60)}"
            out[h] = (out[h]?.let { "$it; " } ?: "") + "clearance UA: $tag"
        }
        return out
    }

    /**
     * The clearance [sent] with a request was answered with a challenge: it is
     * dead. Removes exactly that cookie from the jar (not a newer one another
     * request just earned) and forgets the user agent recorded for its domain.
     * Chromium's copy is left alone: the built-in solve clears its own browser
     * before it starts, and a transport page may still be riding it.
     */
    fun dropClearance(url: HttpUrl, sent: okhttp3.Cookie?) {
        if (sent == null) return
        val now = network.cookieStore.get(url).firstOrNull { it.name == sent.name && it.domain == sent.domain }
        if (now == null || now.value != sent.value) return
        runCatching { network.cookieStore.remove(now) }
        val h = key(now.domain)
        if (clearances.containsKey(h) || loadClearance(h) != null) forget(h)
        logger.info { "Dropped a dead Cloudflare clearance for ${now.domain}" }
    }

    fun isChallengeTitle(title: String?): Boolean =
        title != null && (
            title.contains("Just a moment", ignoreCase = true) ||
                title.contains("Attention Required", ignoreCase = true) ||
                title.startsWith("Checking your browser", ignoreCase = true)
            )

    /**
     * For a WebView that met a challenge: solve it with the built-in browser
     * (wait-only), then with the external solver if one is switched on. True
     * when the page should be reloaded.
     */
    suspend fun solve(url: HttpUrl): Boolean {
        if (!builtInEnabled) return extension.bridge.Settings.flareSolverrEnabled && solveExternal(url)
        when (verdict(url.host)) {
            Verdict.NEEDS_PERSON, Verdict.BLOCKED -> return false
            Verdict.TRANSPORT -> return true
            null -> {}
        }
        if (!BrowserSolver.canRunHere()) return false
        when (solveBuiltIn(url, CFClearance.clearance(url))) {
            null, BrowserSolver.Outcome.CLEARED, BrowserSolver.Outcome.PASSED -> return true
            BrowserSolver.Outcome.BLOCKED -> return false
            BrowserSolver.Outcome.NEEDS_PERSON, BrowserSolver.Outcome.FAILED -> {}
        }
        if (!extension.bridge.Settings.flareSolverrEnabled) return false
        return solveExternal(url).also { if (it) clearVerdict(url.host) }
    }

    /**
     * The one built-in solve per site: under the site's lock, and with the
     * outcome stored as a verdict before the lock is let go, so a caller that
     * waited sees it instead of solving the site again. Null when, by the time
     * the lock was ours, someone else had settled it (a verdict, or a
     * clearance other than [clearanceBefore]).
     */
    suspend fun solveBuiltIn(url: HttpUrl, clearanceBefore: String?): BrowserSolver.Outcome? =
        CFClearance.hostLock(url.host).withLock {
            val now = CFClearance.clearance(url)
            if (verdict(url.host) != null || (now != null && now != clearanceBefore)) return@withLock null
            BrowserSolver.solve(url).also { o ->
                when (o) {
                    BrowserSolver.Outcome.PASSED -> setVerdict(url.host, Verdict.TRANSPORT)
                    BrowserSolver.Outcome.BLOCKED -> setVerdict(url.host, Verdict.BLOCKED)
                    BrowserSolver.Outcome.NEEDS_PERSON -> setVerdict(url.host, Verdict.NEEDS_PERSON)
                    BrowserSolver.Outcome.CLEARED, BrowserSolver.Outcome.FAILED -> {}
                }
            }
        }

    /**
     * Solves [url]'s challenge with the external solver. Cookies land in the
     * jar and in Chromium, and the user agent they belong to is recorded for
     * this site only. True when solved — including by a concurrent caller
     * while this one waited for the site's turn.
     */
    suspend fun solveExternal(url: HttpUrl): Boolean {
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
