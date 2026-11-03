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
            return runLocked(url, req.str("userAgent"), steps)
        } finally {
            gate.release()
        }
    }

    private fun runLocked(url: String, ua: String?, steps: List<JsonObject>): String {
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
        }.toString()
    }

    private fun isEmpty(v: String?): Boolean =
        v == null || v == "null" || v == "false" || v == "\"\"" || v == "undefined" || v.isBlank()
}
