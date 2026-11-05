package eu.kanade.tachiyomi.network.interceptor

import android.app.Application
import android.os.Handler
import android.os.Looper
import android.util.Base64
import android.webkit.JavascriptInterface
import android.webkit.WebView
import android.webkit.WebViewClient
import extension.bridge.logging.androidCompatLogger
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import eu.kanade.tachiyomi.network.NetworkHelper
import extension.bridge.toCefCookie
import okhttp3.Cookie
import okhttp3.Headers
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import org.cef.network.CefCookieManager
import xyz.nulldev.androidcompat.webkit.KcefFidelity
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import xyz.nulldev.androidcompat.webkit.KcefWebViewProvider
import java.io.IOException
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Carries a request through the embedded Chromium instead of OkHttp.
 *
 * For sites where Cloudflare lets this Chromium straight through but
 * challenges every plain HTTP client, whatever its user agent (measured on
 * manganami.com: curl/OkHttp with Chrome's UA -> 403 "Just a moment",
 * Chromium -> the page, no challenge, no clearance issued). There is no
 * clearance to hand to OkHttp, so the request itself has to be made by the
 * browser: a `fetch()` from a blank page on the site's own origin (so it is
 * same-origin, carries the site's cookies, and leaves with Chromium's network
 * fingerprint). The same technique as WebViewFetchInterceptor, but with one
 * long-lived page per site instead of a browser per request.
 *
 * Only used for a request that OkHttp already sent through the extension's
 * own chain (so its rate limits applied) and that came back challenged.
 */
object ChromiumTransport {
    private val logger = androidCompatLogger(ChromiumTransport::class.java)
    private val main by lazy { Handler(Looper.getMainLooper()) }
    private val network by lazy { Injekt.get<NetworkHelper>() }

    private const val MAX_CONTEXTS = 2
    private const val IDLE_MS = 3 * 60_000L
    private const val TIMEOUT_S = 60L

    class Result(
        val status: Int,
        val statusText: String,
        val headers: String,
        val url: String,
        val bodyBase64: String,
        val error: String?,
    )

    /** The page's callbacks, keyed by request id. KCEF hands every argument over as a string. */
    class Bridge {
        internal val pending = ConcurrentHashMap<String, CompletableFuture<Result>>()

        @JavascriptInterface
        fun onResponse(id: String, status: String, statusText: String, headers: String, url: String, bodyBase64: String) {
            pending.remove(id)?.complete(Result(status.toIntOrNull() ?: 0, statusText, headers, url, bodyBase64, null))
        }

        @JavascriptInterface
        fun onError(id: String, error: String) {
            pending.remove(id)?.complete(Result(0, "", "", "", "", error))
        }
    }

    private class Context(val key: String, val origin: String, val webView: WebView, val bridge: Bridge) {
        @Volatile var lastUsed = System.currentTimeMillis()

        /** Pacing, standing in for rate limits on requests that skip the OkHttp chain. */
        val inFlight = Semaphore(4)
        var lastStart = 0L
    }

    private val contexts = ConcurrentHashMap<String, Context>()

    private val reaper = Executors.newSingleThreadScheduledExecutor { r ->
        Thread(r, "chromium-transport-reaper").apply { isDaemon = true }
    }.also {
        it.scheduleWithFixedDelay({
            val now = System.currentTimeMillis()
            contexts.values.filter { c -> c.bridge.pending.isEmpty() && now - c.lastUsed > IDLE_MS }.forEach(::close)
        }, 60, 60, TimeUnit.SECONDS)
    }

    private fun close(c: Context) {
        if (contexts.remove(c.key, c)) {
            c.bridge.pending.values.forEach { it.complete(Result(0, "", "", "", "", "closed")) }
            c.bridge.pending.clear()
            main.post { runCatching { c.webView.destroy() } }
        }
    }

    private const val PACE_MS = 250L

    fun execute(request: Request): Response {
        if (!BrowserSolver.canRunHere()) throw IOException("Cloudflare: ${request.url.host} needs the browser, which can't be used from this thread")
        val url = request.url
        val origin = url.scheme + "://" + url.host + (if (url.port != HttpUrl.defaultPort(url.scheme)) ":${url.port}" else "")
        // The request's own user agent when it is a Chrome one (a mobile
        // Chrome UA changes the HTML some sources parse); otherwise this
        // Chromium's, never another browser's string on Chromium's network
        // fingerprint.
        val ua = request.header("User-Agent")?.takeIf { it.contains("Chrome/") } ?: CFClearance.currentUserAgent()
        val ctx = context(origin, ua)
        ctx.lastUsed = System.currentTimeMillis()
        if (!ctx.inFlight.tryAcquire(TIMEOUT_S, TimeUnit.SECONDS)) throw IOException("Cloudflare: the browser is busy with ${url.host}; try again")
        try {
            synchronized(ctx) {
                val wait = ctx.lastStart + PACE_MS - System.currentTimeMillis()
                if (wait > 0) Thread.sleep(wait)
                ctx.lastStart = System.currentTimeMillis()
            }
            pushCookies(url, request.header("Cookie"))

            val id = UUID.randomUUID().toString()
            val future = CompletableFuture<Result>()
            ctx.bridge.pending[id] = future
            val js = script(id, request)
            main.post { runCatching { ctx.webView.evaluateJavascript(js, null) }.onFailure { ctx.bridge.onError(id, "evaluate failed: ${it.message}") } }

            val r = try {
                future.get(TIMEOUT_S, TimeUnit.SECONDS)
            } catch (e: Exception) {
                ctx.bridge.pending.remove(id)
                // A page that stops answering (its renderer swept or crashed) is retired.
                close(ctx)
                throw IOException("Cloudflare: the browser request to ${url.host} timed out", e)
            }
            if (r.error != null) {
                if (r.error == "closed" || r.error.startsWith("evaluate failed")) close(ctx)
                throw IOException("Cloudflare: the browser request to ${url.host} failed: ${r.error.take(200)}")
            }
            syncCookiesBack(url)

            val headers = Headers.Builder().apply {
                runCatching { Json.parseToJsonElement(r.headers).jsonObject }.getOrNull()?.forEach { (k, v) ->
                    // fetch() has already decoded the body.
                    if (!k.equals("content-encoding", true) && !k.equals("content-length", true) && !k.equals("transfer-encoding", true)) {
                        (v as? JsonPrimitive)?.contentOrNull?.let { runCatching { addUnsafeNonAscii(k, it) } }
                    }
                }
            }.build()
            val body = if (r.bodyBase64.isEmpty()) ByteArray(0) else Base64.decode(r.bodyBase64, Base64.NO_WRAP)
            val finalUrl = r.url.toHttpUrlOrNull()
            ctx.lastUsed = System.currentTimeMillis()
            return Response.Builder()
                .request(if (finalUrl != null && finalUrl != url) request.newBuilder().url(finalUrl).build() else request)
                .protocol(Protocol.HTTP_1_1)
                .code(r.status)
                .message(r.statusText)
                .headers(headers)
                .body(body.toResponseBody(headers["Content-Type"]?.toMediaTypeOrNull()))
                .build()
        } finally {
            ctx.inFlight.release()
        }
    }

    /**
     * The jar's cookies for [url] into Chromium (a login made over OkHttp), and
     * an explicit Cookie header the source set (fetch can't send one). Never
     * Cloudflare's own: the jar's copy may be the dead one, Chromium's the live.
     */
    private fun pushCookies(url: HttpUrl, explicit: String?) {
        runCatching {
            val cef = CefCookieManager.getGlobalManager() ?: return
            for (c in network.cookieStore.get(url)) {
                if (c.name in CF_COOKIES) continue
                cef.setCookie("https://" + c.domain, c.toCefCookie())
            }
            explicit?.split(';')?.map { it.trim() }?.filter { '=' in it }?.forEach { pair ->
                val name = pair.substringBefore('=').trim()
                if (name.isEmpty() || name in CF_COOKIES) return@forEach
                val cookie = Cookie.Builder().name(name).value(pair.substringAfter('=')).hostOnlyDomain(url.host).path("/").build()
                cef.setCookie("https://" + url.host, cookie.toCefCookie())
            }
        }
    }

    private val CF_COOKIES = setOf("cf_clearance", "__cf_bm")

    /** Set-Cookie on a fetched response only reaches Chromium; copy it back so the jar stays the one store. */
    private fun syncCookiesBack(url: HttpUrl) {
        val done = CountDownLatch(1)
        runCatching { KcefFidelity.syncChromiumCookiesToJar(url) { done.countDown() } }
            .onFailure { done.countDown() }
        done.await(3, TimeUnit.SECONDS)
    }

    /** A blank page on [origin], opened once and reused; it costs no request to the site. */
    private fun context(origin: String, ua: String): Context {
        val key = "$origin|$ua"
        contexts[key]?.let { return it }
        synchronized(this) {
            contexts[key]?.let { return it }
            // Make room, but never by closing a page with requests in flight.
            val deadline = System.currentTimeMillis() + 30_000L
            while (contexts.size >= MAX_CONTEXTS) {
                val idle = contexts.values.filter { it.bridge.pending.isEmpty() }.minByOrNull { it.lastUsed }
                if (idle != null) {
                    close(idle)
                } else {
                    if (System.currentTimeMillis() > deadline) throw IOException("Cloudflare: the browser pages are all busy; try again")
                    Thread.sleep(250)
                }
            }
            val bridge = Bridge()
            val ready = CountDownLatch(1)
            val created = AtomicReference<WebView?>()
            // Set when the wait below gives up, so a creation that runs late
            // doesn't start a browser nothing will ever close.
            val abandoned = java.util.concurrent.atomic.AtomicBoolean(false)
            main.post {
                if (abandoned.get()) return@post
                try {
                    val w = WebView(Injekt.get<Application>())
                    (w.webViewProvider as? KcefWebViewProvider)?.apply {
                        isolated = true
                        exemptFromOverflow = true
                    }
                    w.settings.javaScriptEnabled = true
                    w.settings.domStorageEnabled = true
                    w.settings.userAgentString = ua
                    w.addJavascriptInterface(bridge, "__rzTransport")
                    w.webViewClient = object : WebViewClient() {
                        override fun onPageFinished(view: WebView, url: String) = ready.countDown()
                    }
                    created.set(w)
                    // KCEF needs an encoding here (Android would default it).
                    w.loadDataWithBaseURL("$origin/", "<!doctype html><html><head></head><body></body></html>", "text/html", "utf-8", null)
                } catch (t: Throwable) {
                    logger.warn(t) { "Could not open a browser page for $origin" }
                    created.getAndSet(null)?.let { runCatching { it.destroy() } }
                    ready.countDown()
                }
            }
            val w = if (ready.await(30, TimeUnit.SECONDS)) created.get() else null
            if (w == null) {
                abandoned.set(true)
                created.get()?.let { main.post { runCatching { it.destroy() } } }
                throw IOException("Cloudflare: could not open a browser page for $origin")
            }
            logger.info { "Carrying Cloudflare-challenged requests for $origin through Chromium" }
            return Context(key, origin, w, bridge).also { contexts[key] = it }
        }
    }

    private val SKIP_HEADERS = setOf(
        "host", "connection", "content-length", "cookie", "cookie2", "user-agent", "accept-encoding",
        "origin", "referer", "te", "trailer", "transfer-encoding", "upgrade", "via", "keep-alive", "expect",
    )

    private fun script(id: String, request: Request): String {
        val headers = buildJsonObject {
            for ((k, v) in request.headers) {
                val lk = k.lowercase()
                if (lk in SKIP_HEADERS || lk.startsWith("sec-") || lk.startsWith("proxy-")) continue
                put(k, v)
            }
            request.body?.contentType()?.let { put("Content-Type", it.toString()) }
        }
        if (request.header("Cookie") != null) {
            logger.debug { "Cookie header on a Chromium-carried request to ${request.url.host} is replaced by the browser's own cookies" }
        }
        val body = request.body?.let { b -> Buffer().also { b.writeTo(it) }.readByteArray() }
        val init = buildJsonObject {
            put("method", request.method)
            put("headers", headers)
            put("credentials", "include")
            put("cache", "no-store")
            put("redirect", "follow")
            request.header("Referer")?.let {
                put("referrer", it)
                put("referrerPolicy", "unsafe-url")
            }
        }
        val idJs = Json.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(id))
        return """
            (function(){
              var id = $idJs, init = $init;
              var b64 = ${Json.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(body?.let { Base64.encodeToString(it, Base64.NO_WRAP) } ?: ""))};
              if (b64) { var s = atob(b64), u = new Uint8Array(s.length); for (var i = 0; i < s.length; i++) u[i] = s.charCodeAt(i); init.body = u; }
              fetch(${Json.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(request.url.toString()))}, init).then(function(r){
                return r.arrayBuffer().then(function(buf){
                  var bytes = new Uint8Array(buf), bin = '';
                  for (var i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
                  var h = {}; r.headers.forEach(function(v, k){ h[k] = v; });
                  return window.__rzTransport.onResponse(id, String(r.status), r.statusText || '', JSON.stringify(h), r.url || '', btoa(bin));
                });
              }).catch(function(e){ window.__rzTransport.onError(id, String(e)); });
              return true;
            })()
        """.trimIndent()
    }

    /** Open pages, for the debug endpoint. */
    fun status(): JsonObject = buildJsonObject {
        val now = System.currentTimeMillis()
        contexts.values.forEach { put(it.key.substringBefore('|'), "idle ${(now - it.lastUsed) / 1000}s, ${it.bridge.pending.size} in flight") }
    }
}
