package eu.kanade.tachiyomi.network.interceptor

import android.app.Application
import android.os.Handler
import android.os.Looper
import android.webkit.WebView
import android.webkit.WebViewClient
import extension.bridge.logging.androidCompatLogger
import okhttp3.HttpUrl
import org.cef.network.CefCookieManager
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import xyz.nulldev.androidcompat.webkit.KcefFidelity
import xyz.nulldev.androidcompat.webkit.KcefWebViewProvider
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Shiori's own Cloudflare solver: opens the challenged page in the embedded
 * Chromium, at a real window size, and WAITS. A browser that Cloudflare
 * trusts finishes the challenge by itself and is sent on to the page; then
 * whatever Cloudflare issued (cf_clearance) is copied into the HTTP jar and
 * the site's user agent is recorded as this Chromium's own.
 *
 * Wait-only, by the owner's standing rule: no input events, nothing clicks,
 * types or touches a Turnstile/captcha widget, nothing reads its token, and
 * no page is handed to a person. A challenge that needs one ends the solve
 * as [Outcome.NEEDS_PERSON].
 *
 * Measured 2026-11-05 with an isolated browser (no stored clearance, no
 * borrowed user agent, no external solver): Chromium was let straight through
 * on natomanga/manganami while plain HTTP clients got "Just a moment". Those
 * sites issue no clearance at all, so the solve reports [Outcome.PASSED] and
 * the caller carries the request through Chromium (ChromiumTransport).
 */
object BrowserSolver {
    private val logger = androidCompatLogger(BrowserSolver::class.java)

    enum class Outcome {
        /** The challenge finished and Cloudflare issued a clearance (now in the jar). */
        CLEARED,

        /** Chromium got the page without being asked to prove anything: no clearance exists. */
        PASSED,

        /** Still a challenge when the wait ran out: a person has to do it. */
        NEEDS_PERSON,

        /** A Cloudflare block page ("Attention Required!", "Sorry, you have been blocked"). */
        BLOCKED,

        /** The browser could not run the solve (busy, wrong thread, crashed). */
        FAILED,
    }

    /** One solving browser at a time: each is a Chromium renderer, and the container's pid cap is load-bearing. */
    private val slot = Semaphore(1)

    private const val WAIT_MS = 75_000L
    private const val POLL_MS = 1_500L
    private val CF_COOKIES = listOf("cf_clearance", "__cf_bm")

    private val main by lazy { Handler(Looper.getMainLooper()) }

    /**
     * Solves [url]'s challenge in Chromium. Blocks the calling thread for up to
     * [WAIT_MS]; never call it on the main looper or a Chromium thread (it waits
     * for both), which [canRunHere] checks.
     */
    fun solve(url: HttpUrl): Outcome {
        if (!canRunHere()) return Outcome.FAILED
        if (!slot.tryAcquire(WAIT_MS, TimeUnit.MILLISECONDS)) return Outcome.FAILED
        val ref = AtomicReference<WebView?>()
        // Only a clearance that changed during this solve was earned by it.
        val before = CFClearance.clearance(url)
        try {
            val created = CountDownLatch(1)
            val abandoned = java.util.concurrent.atomic.AtomicBoolean(false)
            main.post {
                if (abandoned.get()) return@post
                try {
                    val w = WebView(Injekt.get<Application>())
                    w.minimumWidth = 1280
                    w.minimumHeight = 900
                    (w.webViewProvider as? KcefWebViewProvider)?.apply {
                        isolated = true
                        exemptFromOverflow = true
                    }
                    w.settings.javaScriptEnabled = true
                    w.settings.domStorageEnabled = true
                    w.webViewClient = WebViewClient()
                    ref.set(w)
                } catch (t: Throwable) {
                    logger.warn(t) { "Could not open a browser to solve ${url.host}" }
                } finally {
                    created.countDown()
                }
            }
            if (!created.await(30, TimeUnit.SECONDS)) abandoned.set(true)
            val w = ref.get() ?: return Outcome.FAILED

            // A clearance that got us challenged is dead. Constructing the
            // WebView pushed the jar's cookies into Chromium, so drop the dead
            // ones after that, give the push a moment to land, and drop again.
            dropChromiumClearance(url)
            Thread.sleep(300)
            dropChromiumClearance(url)
            main.post { runCatching { w.loadUrl(url.toString()) } }

            val t0 = System.currentTimeMillis()
            var state = State.LOADING
            while (System.currentTimeMillis() - t0 < WAIT_MS) {
                Thread.sleep(POLL_MS)
                state = read(w)
                if (state == State.PAGE || state == State.BLOCKED) break
            }
            val outcome = when (state) {
                State.BLOCKED -> Outcome.BLOCKED
                State.PAGE -> if (harvest(url, before)) Outcome.CLEARED else Outcome.PASSED
                else -> Outcome.NEEDS_PERSON
            }
            logger.info { "Built-in Cloudflare solve for ${url.host}: $outcome in ${(System.currentTimeMillis() - t0) / 1000}s (${url.encodedPath.take(60)}, thread ${Thread.currentThread().name})" }
            return outcome
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
            return Outcome.FAILED
        } finally {
            ref.get()?.let { w -> main.post { runCatching { w.destroy() } } }
            slot.release()
        }
    }

    /** Not on the main looper or a Chromium thread: the solve waits on both. */
    fun canRunHere(): Boolean {
        if (Looper.myLooper() == Looper.getMainLooper()) return false
        val name = Thread.currentThread().name
        return !name.startsWith("cef") && !name.contains("AWT-EventQueue")
    }

    private enum class State { LOADING, CHALLENGE, BLOCKED, PAGE }

    private const val PROBE_JS =
        "(function(){var t=document.title||'',b=document.body?document.body.innerText:'';" +
            "if(location.protocol.indexOf('http')!==0||document.readyState==='loading'||!document.body)return 'loading';" +
            "if(/attention required|access denied/i.test(t)&&/blocked|error 10\\d\\d|ray id/i.test(b))return 'blocked';" +
            "if(/sorry, you have been blocked/i.test(b))return 'blocked';" +
            "if(/just a moment|un instant|checking your browser|verify you are human|security check/i.test(t)" +
            "||typeof window._cf_chl_opt!=='undefined'" +
            "||document.querySelector(\"#challenge-form,#challenge-stage,script[src*='/cdn-cgi/challenge-platform/h/']\"))return 'challenge';" +
            "return 'page';})()"

    private fun read(w: WebView): State {
        val f = CompletableFuture<String?>()
        main.post { runCatching { w.evaluateJavascript(PROBE_JS) { f.complete(it) } }.onFailure { f.complete(null) } }
        val r = runCatching { f.get(10, TimeUnit.SECONDS) }.getOrNull()?.trim('"') ?: return State.LOADING
        return when (r) {
            "page" -> State.PAGE
            "blocked" -> State.BLOCKED
            "challenge" -> State.CHALLENGE
            else -> State.LOADING
        }
    }

    /**
     * Copies Chromium's cookies for [url] into the jar. True when a clearance
     * came with them, recorded as belonging to this Chromium's user agent.
     */
    private fun harvest(url: HttpUrl, before: String?): Boolean {
        val done = CountDownLatch(1)
        KcefFidelity.syncChromiumCookiesToJar(url) { done.countDown() }
        done.await(5, TimeUnit.SECONDS)
        val clearance = CFClearance.clearanceCookie(url) ?: return false
        if (clearance.value == before) return false
        val expires = if (clearance.persistent) clearance.expiresAt else System.currentTimeMillis() + 30 * 60_000L
        ChallengeSolver.record(url.host, CFClearance.currentUserAgent(), expires)
        return true
    }

    private fun dropChromiumClearance(url: HttpUrl) {
        runCatching {
            val cef = CefCookieManager.getGlobalManager() ?: return
            for (h in setOf(url.host, url.host.removePrefix("www."))) {
                for (name in CF_COOKIES) cef.deleteCookies("https://$h/", name)
            }
        }
    }
}
