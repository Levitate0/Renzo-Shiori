package extension.bridge.server

import eu.kanade.tachiyomi.source.CatalogueSource
import eu.kanade.tachiyomi.source.online.HttpSource
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.add
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import java.net.URLClassLoader
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import java.util.jar.JarFile

/**
 * Read-only diagnostics for the network side of extensions: which sites sit
 * behind a Cloudflare challenge, which extensions drive a WebView, what user
 * agent each source froze into its headers, and how its client is built.
 * Nothing here changes a source's state; the probe uses its own plain client.
 */
object SidecarProbe {
    private fun JsonObject.str(k: String): String? = (this[k] as? JsonPrimitive)?.contentOrNull

    private val webViewJars = ConcurrentHashMap<String, Boolean>()

    /** True when any class in the source's jar references android.webkit.WebView. */
    private fun usesWebView(s: CatalogueSource): Boolean? {
        val jar = (s.javaClass.classLoader as? URLClassLoader)?.urLs?.firstOrNull()?.path ?: return null
        return webViewJars.computeIfAbsent(jar) {
            runCatching {
                val needle = "android/webkit/WebView".toByteArray()
                JarFile(jar).use { jf ->
                    jf.entries().asSequence().filter { it.name.endsWith(".class") }.any { e ->
                        val bytes = jf.getInputStream(e).use { it.readBytes() }
                        indexOf(bytes, needle) >= 0
                    }
                }
            }.getOrDefault(false)
        }
    }

    private fun indexOf(hay: ByteArray, needle: ByteArray): Int {
        outer@ for (i in 0..hay.size - needle.size) {
            for (j in needle.indices) if (hay[i + j] != needle[j]) continue@outer
            return i
        }
        return -1
    }

    private val raw by lazy {
        okhttp3.OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS)
            .callTimeout(25, TimeUnit.SECONDS)
            .build()
    }

    fun probe(all: List<CatalogueSource>, req: JsonObject): String {
        val only = req.str("id")?.toLongOrNull()
        val fetch = req.str("fetch")?.toBooleanStrictOrNull() ?: true
        val list = all.filter { only == null || it.id == only }.distinctBy { it.id }
        val pool = Executors.newFixedThreadPool(8)
        try {
            val futures = list.map { s -> pool.submit<JsonObject> { probeOne(s, fetch) } }
            return buildJsonArray {
                for (f in futures) add(runCatching { f.get(60, TimeUnit.SECONDS) }
                    .getOrElse { buildJsonObject { put("error", it.toString().take(200)) } })
            }.toString()
        } finally {
            pool.shutdownNow()
        }
    }

    private fun probeOne(s: CatalogueSource, fetch: Boolean): JsonObject = buildJsonObject {
        put("id", s.id.toString())
        put("name", s.name)
        put("lang", s.lang)
        put("class", s.javaClass.name)
        usesWebView(s)?.let { put("usesWebView", it) }
        if (s !is HttpSource) return@buildJsonObject
        put("baseUrl", s.baseUrl)
        val ua = runCatching { s.headers["User-Agent"] }.getOrNull()
        ua?.let { put("userAgent", it) }
        runCatching { s.client }.getOrNull()?.let { c ->
            put("interceptors", buildJsonArray { c.interceptors.forEach { add(it.javaClass.name) } })
            put("networkInterceptors", buildJsonArray { c.networkInterceptors.forEach { add(it.javaClass.name) } })
        }
        if (!fetch || !s.baseUrl.startsWith("http")) return@buildJsonObject
        val t0 = System.currentTimeMillis()
        runCatching {
            val rq = okhttp3.Request.Builder().url(s.baseUrl)
                .header("User-Agent", ua ?: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
                .build()
            raw.newCall(rq).execute().use { r ->
                put("status", r.code)
                put("ms", System.currentTimeMillis() - t0)
                put("server", r.header("Server") ?: "")
                r.header("cf-mitigated")?.let { put("cfMitigated", it) }
                put("finalUrl", r.request.url.toString())
                val body = runCatching { r.peekBody(256 * 1024).string() }.getOrDefault("")
                Regex("<title[^>]*>([^<]{0,100})", RegexOption.IGNORE_CASE).find(body)?.groupValues?.get(1)?.trim()?.let { put("title", it) }
                put("challenge", r.header("cf-mitigated") == "challenge" || body.contains("<title>Just a moment") ||
                    body.contains("cf_chl_opt") || body.contains("challenges.cloudflare.com/turnstile"))
            }
        }.onFailure { put("fetchError", it.toString().take(200)) }
    }

    /** GET [url] through the SOURCE's own client (its interceptors, cookies and headers). */
    fun fetchVia(s: CatalogueSource, req: JsonObject): String {
        val url = req.str("url") ?: throw IllegalArgumentException("url required")
        if (s !is HttpSource) throw IllegalArgumentException("not an HTTP source")
        val t0 = System.currentTimeMillis()
        val rq = okhttp3.Request.Builder().url(url).headers(s.headers).build()
        return s.client.newCall(rq).execute().use { r ->
            val body = runCatching { r.peekBody(256 * 1024).string() }.getOrDefault("")
            buildJsonObject {
                put("status", r.code)
                put("ms", System.currentTimeMillis() - t0)
                put("server", r.header("Server") ?: "")
                r.header("cf-mitigated")?.let { put("cfMitigated", it) }
                put("sentUserAgent", r.request.header("User-Agent") ?: "")
                put("sentCookies", (r.request.header("Cookie") ?: "").split(";").map { it.substringBefore("=").trim() }.joinToString(","))
                Regex("<title[^>]*>([^<]{0,100})", RegexOption.IGNORE_CASE).find(body)?.groupValues?.get(1)?.trim()?.let { put("title", it) }
                put("bytes", body.length)
            }.toString()
        }
    }

    /**
     * Opens [url] in a fresh WebView exactly as an extension would, samples
     * title/location every 1.5 s until it is no longer a Cloudflare page (or
     * the wait ends), then reports the Chromium cookies for the URL.
     */
    fun webView(req: JsonObject): String {
        val url = req.str("url") ?: throw IllegalArgumentException("url required")
        val waitMs = req.str("waitMs")?.toLongOrNull() ?: 30_000L
        val ua = req.str("userAgent")
        if (req.str("clearCookies")?.toBooleanStrictOrNull() == true) {
            // Start without any clearance: is the challenge passed by this
            // Chromium itself, or was it only ever riding a stored cookie?
            runCatching {
                val host = java.net.URI(url).host
                val cef = org.cef.network.CefCookieManager.getGlobalManager()
                for (u in listOf("https://$host/", "https://${host.removePrefix("www.")}/")) {
                    cef.deleteCookies(u, "cf_clearance")
                    cef.deleteCookies(u, "__cf_bm")
                }
            }
        }
        val main = android.os.Handler(android.os.Looper.getMainLooper())
        val ref = AtomicReference<android.webkit.WebView?>()
        val created = CountDownLatch(1)
        main.post {
            try {
                val app = uy.kohesive.injekt.Injekt.getInstance<android.app.Application>(android.app.Application::class.java)
                val w = android.webkit.WebView(app)
                w.settings.javaScriptEnabled = true
                w.settings.domStorageEnabled = true
                if (!ua.isNullOrBlank()) w.settings.userAgentString = ua
                w.webViewClient = android.webkit.WebViewClient()
                w.loadUrl(url)
                ref.set(w)
            } catch (t: Throwable) {
                System.err.println("[probe] WebView create failed: $t")
            } finally {
                created.countDown()
            }
        }
        created.await(30, TimeUnit.SECONDS)
        val samples = mutableListOf<String>()
        var extraResult: String? = null
        var passed = false
        var last: JsonObject? = null
        val t0 = System.currentTimeMillis()
        try {
            while (System.currentTimeMillis() - t0 < waitMs) {
                Thread.sleep(1500)
                val w = ref.get() ?: break
                val f = CompletableFuture<String>()
                main.post {
                    w.evaluateJavascript(
                        "(function(){var d=document.documentElement;return JSON.stringify({t:document.title,u:location.href,n:d?d.outerHTML.length:0,ua:navigator.userAgent,wd:navigator.webdriver===true})})()",
                    ) { f.complete(it) }
                }
                val rawResult = runCatching { f.get(5, TimeUnit.SECONDS) }.getOrNull() ?: continue
                val inner = runCatching { Json.parseToJsonElement(rawResult).jsonPrimitive.content }.getOrNull() ?: continue
                val o = runCatching { Json.parseToJsonElement(inner).jsonObject }.getOrNull() ?: continue
                last = o
                val title = o["t"]?.jsonPrimitive?.content ?: ""
                samples += "${(System.currentTimeMillis() - t0) / 1000}s ${title.take(60)} (${o["n"]})"
                if (title.isNotEmpty() && !title.contains("Just a moment", true) && !title.contains("Attention Required", true) &&
                    (o["n"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0) > 3000) {
                    passed = true
                    break
                }
            }
        } finally {
            // Optional extra script, evaluated on the final page.
            req.str("js")?.let { js ->
                val w = ref.get()
                if (w != null) {
                    val f = CompletableFuture<String>()
                    main.post { w.evaluateJavascript(js) { f.complete(it) } }
                    extraResult = runCatching { f.get(10, TimeUnit.SECONDS) }.getOrNull()
                }
            }
            main.post { runCatching { ref.get()?.destroy() } }
        }
        val cookies = mutableListOf<String>()
        runCatching {
            val done = CountDownLatch(1)
            val ok = org.cef.network.CefCookieManager.getGlobalManager().visitUrlCookies(url, true) { c, count, total, _ ->
                cookies += c.name
                if (count >= total - 1) done.countDown()
                true
            }
            if (ok) done.await(3, TimeUnit.SECONDS)
        }
        return buildJsonObject {
            put("passed", passed)
            put("seconds", (System.currentTimeMillis() - t0) / 1000)
            last?.let { put("last", it) }
            put("samples", buildJsonArray { samples.forEach { add(it) } })
            put("cookies", buildJsonArray { cookies.distinct().forEach { add(it) } })
            extraResult?.let { put("js", it) }
        }.toString()
    }
}
