package xyz.nulldev.androidcompat.webkit

import android.util.Log
import eu.kanade.tachiyomi.network.NetworkHelper
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import okhttp3.Cookie
import okhttp3.HttpUrl
import org.cef.browser.CefBrowser
import org.cef.network.CefCookie
import org.cef.network.CefCookieManager
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * The parts of Android WebView behaviour extensions depend on that the
 * Chromium bridge got wrong, kept apart from the provider so each has one
 * place to read.
 */
internal object KcefFidelity {
    private const val TAG = "KcefFidelity"

    private val timers = Executors.newSingleThreadScheduledExecutor { r ->
        Thread(r, "kcef-fidelity").apply { isDaemon = true }
    }

    // ── evaluateJavascript ────────────────────────────────────────────────

    /**
     * Evaluates [script] through DevTools' Runtime.evaluate and completes
     * with the result in Android's format: the JSON encoding of the script's
     * completion value ("null" for undefined, a thrown error or a value JSON
     * cannot represent). Completes with null when DevTools is unavailable, so
     * the caller can fall back.
     *
     * DevTools rather than the cefQuery bridge because it is what Android's
     * evaluateJavascript is: evaluation of a global program that a page's
     * Content-Security-Policy cannot block. The bridge needs eval() inside the
     * page, which CSP forbids on sites like valirscans.org, where it answered
     * nothing at all.
     */
    fun evaluate(browser: CefBrowser, script: String): CompletableFuture<String?> {
        val out = CompletableFuture<String?>()
        val client = runCatching { browser.devToolsClient }.getOrNull()
        if (client == null || client.isClosed) {
            out.complete(null)
            return out
        }
        val params = buildJsonObject {
            put("expression", script)
            put("returnByValue", true)
            put("awaitPromise", false)
            put("userGesture", true)
        }.toString()
        runCatching {
            client.executeDevToolsMethod("Runtime.evaluate", params).whenComplete { res, err ->
                out.complete(if (err != null || res == null) null else toAndroidResult(res))
            }
        }.onFailure { out.complete(null) }
        // A DevTools call that never answers must not strand the callback.
        timers.schedule({ out.complete(null) }, 10, TimeUnit.SECONDS)
        return out
    }

    private fun toAndroidResult(raw: String): String {
        val o = runCatching { Json.parseToJsonElement(raw).jsonObject }.getOrNull() ?: return "null"
        if (o["exceptionDetails"] != null) return "null"
        val result = o["result"] as? JsonObject ?: return "null"
        val type = (result["type"] as? JsonPrimitive)?.contentOrNull
        val value = result["value"]
        return when {
            type == "undefined" || type == "function" || type == "symbol" -> "null"
            result["unserializableValue"] != null -> "null" // NaN, Infinity, -0, bigint: JSON.stringify gives null
            value == null || value is JsonNull -> if (type == "object" && (result["subtype"] as? JsonPrimitive)?.contentOrNull != "null") "{}" else "null"
            else -> Json.encodeToString(kotlinx.serialization.json.JsonElement.serializer(), value)
        }
    }

    // ── one cookie store ─────────────────────────────────────────────────

    private val network: NetworkHelper by lazy { Injekt.get<NetworkHelper>() }

    /**
     * Copies Chromium's cookies for [url] into the HTTP client's jar, then
     * runs [then]. On Android WebView and OkHttp share one store, so a
     * clearance or a login a page earns is immediately the client's too;
     * extensions that solve Cloudflare in a WebView and then retry over HTTP
     * depend on it. Bounded: Chromium does not call the visitor at all when
     * there are no cookies.
     */
    fun syncChromiumCookiesToJar(url: HttpUrl, then: () -> Unit) {
        val done = AtomicBoolean(false)
        val collected = mutableListOf<CefCookie>()
        val finish = {
            if (done.compareAndSet(false, true)) {
                val cookies = synchronized(collected) { collected.mapNotNull { toOkHttp(it) } }
                if (cookies.isNotEmpty()) runCatching { network.cookieStore.addAll(url, cookies) }
                then()
            }
        }
        val started = runCatching {
            CefCookieManager.getGlobalManager()?.visitUrlCookies(url.toString(), true) { c, count, total, _ ->
                synchronized(collected) { collected += c }
                if (count >= total - 1) finish()
                true
            } ?: false
        }.getOrDefault(false)
        if (!started) finish() else timers.schedule(finish, 400, TimeUnit.MILLISECONDS)
    }

    private fun toOkHttp(c: CefCookie): Cookie? = runCatching {
        val b = Cookie.Builder().name(c.name).value(c.value)
        val d = c.domain ?: return null
        if (d.startsWith(".")) b.domain(d.removePrefix(".")) else b.hostOnlyDomain(d)
        b.path(c.path?.takeIf { it.isNotEmpty() } ?: "/")
        if (c.secure) b.secure()
        if (c.httponly) b.httpOnly()
        if (c.hasExpires && c.expires != null) b.expiresAt(c.expires.time)
        b.build()
    }.getOrNull()

    // ── challenge pages ──────────────────────────────────────────────────

    /** Completes true when the main frame shows a Cloudflare challenge. */
    fun isChallengePage(browser: CefBrowser): CompletableFuture<Boolean> =
        evaluate(browser, "document.title + '\\u0001' + (typeof window._cf_chl_opt !== 'undefined')").thenApply { r ->
            val text = r?.let { runCatching { Json.parseToJsonElement(it) as? JsonPrimitive }.getOrNull()?.contentOrNull } ?: ""
            val title = text.substringBefore('\u0001')
            eu.kanade.tachiyomi.network.interceptor.ChallengeSolver.isChallengeTitle(title) || text.endsWith("\u0001true")
        }

    fun log(msg: String) = Log.w(TAG, msg)
}
