package extension.bridge.server

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Opens a page in a WebView (the same Chromium and cookie store the extensions
 * use, so a site login is already there) and runs a list of script steps on it.
 * Each step is evaluated every `pollMs` until it returns something other than
 * null/false/"" or its `timeoutMs` runs out; the last value is reported. That
 * lets the caller wait for a single-page site to draw, act, then wait for the
 * result, with all the site knowledge kept in the scripts.
 *
 * One page at a time: these are rare, user-triggered, and a Chromium browser
 * each — never worth risking the helper-process leak for.
 */
object SidecarPageScript {
    private val gate = Semaphore(1)
    private const val MAX_TOTAL_MS = 120_000L

    private fun JsonObject.str(k: String): String? = (this[k] as? JsonPrimitive)?.contentOrNull
    private fun JsonObject.long(k: String, d: Long): Long = str(k)?.toLongOrNull() ?: d

    fun run(req: JsonObject): String {
        val url = req.str("url") ?: throw IllegalArgumentException("url required")
        val steps = (req["steps"] as? JsonArray)?.map { it.jsonObject } ?: emptyList()
        if (!gate.tryAcquire(60, TimeUnit.SECONDS)) throw IllegalStateException("another page script is running")
        try {
            return runLocked(url, req.str("userAgent"), steps, req.str("isolate")?.toBooleanStrictOrNull() == true)
        } finally {
            gate.release()
        }
    }

    private fun runLocked(url: String, ua: String?, steps: List<JsonObject>, isolate: Boolean): String {
        val main = android.os.Handler(android.os.Looper.getMainLooper())
        val ref = AtomicReference<android.webkit.WebView?>()
        val created = CountDownLatch(1)
        main.post {
            try {
                val app = uy.kohesive.injekt.Injekt.getInstance<android.app.Application>(android.app.Application::class.java)
                val w = android.webkit.WebView(app)
                // A real window. Without a size Chromium lays the page out 1×1 px,
                // and a box sized from the window (Violet's SweetAlert "Are you
                // sure?") comes out 0 px wide, so the scripts never see it.
                w.minimumWidth = 1280
                w.minimumHeight = 900
                // Isolated: what this Chromium does on its own. Constructing the
                // WebView pushes the jar's cookies into Chromium, so the site's
                // Cloudflare cookies are dropped from Chromium (only) after that.
                if (isolate) {
                    (w.webViewProvider as? xyz.nulldev.androidcompat.webkit.KcefWebViewProvider)?.isolated = true
                    runCatching {
                        val cef = org.cef.network.CefCookieManager.getGlobalManager()
                        val host = java.net.URI(url).host
                        for (u in setOf("https://$host/", "https://${host.removePrefix("www.")}/"))
                            for (name in CF_COOKIES) cef.deleteCookies(u, name)
                    }
                }
                w.settings.javaScriptEnabled = true
                w.settings.domStorageEnabled = true
                if (!ua.isNullOrBlank()) w.settings.userAgentString = ua
                w.webViewClient = android.webkit.WebViewClient()
                w.loadUrl(url)
                ref.set(w)
            } catch (t: Throwable) {
                System.err.println("[page-script] WebView create failed: $t")
            } finally {
                created.countDown()
            }
        }
        created.await(30, TimeUnit.SECONDS)
        val t0 = System.currentTimeMillis()
        val results = mutableListOf<String?>()
        try {
            val w = ref.get() ?: throw IllegalStateException("WebView could not be created")
            for (step in steps) {
                when (step.str("action")) {
                    "clearSiteCookies" -> { results += clearSiteCookies(url); continue }
                    "syncSiteCookiesToJar" -> { results += syncSiteCookiesToJar(url); continue }
                }
                val js = step.str("js") ?: ""
                val timeout = step.long("timeoutMs", 10_000L)
                val poll = step.long("pollMs", 1_000L).coerceAtLeast(200L)
                val stepStart = System.currentTimeMillis()
                var last: String? = null
                while (true) {
                    if (System.currentTimeMillis() - t0 > MAX_TOTAL_MS) break
                    Thread.sleep(poll)
                    if (js.isEmpty()) {
                        if (System.currentTimeMillis() - stepStart >= timeout) break else continue
                    }
                    val f = CompletableFuture<String?>()
                    main.post { runCatching { w.evaluateJavascript(js) { f.complete(it) } }.onFailure { f.complete(null) } }
                    last = runCatching { f.get(10, TimeUnit.SECONDS) }.getOrNull()
                    if (!isEmpty(last) || System.currentTimeMillis() - stepStart >= timeout) break
                }
                results += last
            }
        } finally {
            main.post { runCatching { ref.get()?.destroy() } }
        }
        return buildJsonObject {
            put("seconds", (System.currentTimeMillis() - t0) / 1000)
            put("results", buildJsonArray { results.forEach { add(it) } })
            if (isolate) put("cfCookies", buildJsonArray { cloudflareCookies(url).forEach { add(it) } })
        }.toString()
    }

    private val CF_COOKIES = listOf("cf_clearance", "__cf_bm")

    /**
     * Drops Chromium's cookies for the page's site (its domain and every
     * subdomain, e.g. an api. host), keeping Cloudflare's. For a sign-in in
     * the page: constructing the WebView pushes the jar's cookies (the server's
     * own login) into Chromium, so the page signed in on top of a second
     * session. When the two copies differ in scope (EZmanga sets
     * Domain=vapi.ezmanga.org; the jar holds a host-only copy), the site's
     * Set-Cookie can't replace the injected one, and a site that keeps one
     * session per account ends the server's at that sign-in: Chromium went on
     * sending the dead copy first (login accepted, its very next "who am I"
     * 401). Seen 2026-11-17 as accessToken@vapi… and accessToken@.vapi…. Signing in
     * from a clean slate leaves the page holding only the session it made.
     * Chromium only: the jar is untouched, and gets the new session back
     * through the usual Chromium-to-jar copy. Returns what was dropped, as
     * name@domain+path, never values.
     */
    private fun clearSiteCookies(url: String): String {
        val cef = org.cef.network.CefCookieManager.getGlobalManager() ?: return "no cookie manager"
        val list = siteCookies(url) ?: return "no host"
        for (c in list)
            runCatching { cef.deleteCookies("https://" + (c.domain ?: "").removePrefix(".") + (c.path ?: "/"), c.name) }
        return describe(list)
    }

    /**
     * Copies Chromium's cookies for the page's whole site into the HTTP jar,
     * after a sign-in in the page. The per-load copy only covers the page's
     * own host, and a site's session often lives on another one (EZmanga's on
     * vapi.ezmanga.org), so the server went on reading with the session the
     * sign-in had just ended.
     */
    private fun syncSiteCookiesToJar(url: String): String {
        val list = siteCookies(url) ?: return "no host"
        val store = runCatching {
            uy.kohesive.injekt.Injekt.getInstance<eu.kanade.tachiyomi.network.NetworkHelper>(
                eu.kanade.tachiyomi.network.NetworkHelper::class.java).cookieStore
        }.getOrNull() ?: return "no jar"
        for (c in list) {
            val ok = xyz.nulldev.androidcompat.webkit.KcefFidelity.toOkHttp(c) ?: continue
            val host = (c.domain ?: "").removePrefix(".")
            val u = "https://$host/".toHttpUrlOrNull() ?: continue
            runCatching { store.addAll(u, listOf(ok)) }
        }
        return describe(list)
    }

    /** Chromium's cookies for the page's site and its subdomains, Cloudflare's left out. */
    private fun siteCookies(url: String): List<org.cef.network.CefCookie>? {
        val host = runCatching { java.net.URI(url).host }.getOrNull() ?: return null
        val labels = host.removePrefix("www.").split('.')
        // example.org -> example.org; a.b.co.uk -> b.co.uk (short second level).
        val keep = if (labels.size >= 3 && labels[labels.size - 2].length <= 3 && labels.last().length == 2) 3 else 2
        val site = labels.takeLast(keep).joinToString(".")
        val cef = org.cef.network.CefCookieManager.getGlobalManager() ?: return emptyList()
        val found = java.util.Collections.synchronizedList(mutableListOf<org.cef.network.CefCookie>())
        val done = CountDownLatch(1)
        val started = runCatching {
            cef.visitAllCookies { c, count, total, _ ->
                val d = (c.domain ?: "").removePrefix(".")
                if ((d == site || d.endsWith(".$site")) && c.name !in CF_COOKIES && !c.name.startsWith("cf_")) found += c
                if (count >= total - 1) done.countDown()
                true
            }
        }.getOrDefault(false)
        if (started) done.await(2, TimeUnit.SECONDS)
        return synchronized(found) { found.toList() }
    }

    /** name@domain+path for each cookie, never values. */
    private fun describe(list: List<org.cef.network.CefCookie>): String =
        if (list.isEmpty()) "none" else list.joinToString(", ") { "${it.name}@${it.domain}${it.path ?: "/"}" }

    /** Names and expiry (never values) of Chromium's Cloudflare cookies for [url]. */
    private fun cloudflareCookies(url: String): List<String> {
        val out = java.util.Collections.synchronizedList(mutableListOf<String>())
        val done = CountDownLatch(1)
        val started = runCatching {
            org.cef.network.CefCookieManager.getGlobalManager()?.visitUrlCookies(url, true) { c, count, total, _ ->
                if (c.name in CF_COOKIES || c.name.startsWith("cf_")) out += "${c.name}@${c.domain} exp=${c.expires}"
                if (count >= total - 1) done.countDown()
                true
            } ?: false
        }.getOrDefault(false)
        if (started) done.await(2, TimeUnit.SECONDS)
        return out.toList()
    }

    private fun isEmpty(v: String?): Boolean =
        v == null || v == "null" || v == "false" || v == "\"\"" || v == "undefined" || v.isBlank()
}
