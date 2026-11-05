package eu.kanade.tachiyomi.network.interceptor

import eu.kanade.tachiyomi.network.NetworkHelper
import eu.kanade.tachiyomi.network.POST
import eu.kanade.tachiyomi.network.awaitSuccess
import eu.kanade.tachiyomi.network.parseAs
import extension.bridge.logging.androidCompatLogger
import kotlinx.coroutines.GlobalScope
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.sync.withPermit
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.HttpUrl
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import extension.bridge.Settings
import uy.kohesive.injekt.injectLazy
import java.io.IOException
import kotlin.time.Duration.Companion.seconds
import kotlin.time.toJavaDuration

class CloudflareInterceptor : Interceptor {
    private val logger = androidCompatLogger(CloudflareInterceptor::class.java)

    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()

        // A site whose plain requests Cloudflare refuses while it lets Chromium
        // through: don't send the doomed one, carry it through Chromium.
        if (ChallengeSolver.builtInEnabled && ChallengeSolver.verdict(request.url.host) == ChallengeSolver.Verdict.TRANSPORT) {
            return try {
                viaTransport(chain, request)
            } catch (e: IOException) {
                throw e
            } catch (e: Exception) {
                throw IOException(e)
            }
        }

        val sentClearance = CFClearance.clearanceCookie(request.url)
        val originalResponse = chain.proceed(request)
        if (ChallengeSolver.classify(originalResponse) == ChallengeSolver.Kind.NONE) return originalResponse

        // Where Cloudflare answered, after any redirects (natomanga.com ->
        // manganami.com). Retries resend the request this interceptor got;
        // only the transport and the verdict use the final address.
        val finalUrl = originalResponse.request.url
        originalResponse.close()

        return try {
            if (!ChallengeSolver.builtInEnabled) {
                return external(chain, request)
                    ?: throw IOException("Cloudflare challenge on ${finalUrl.host}; the built-in solver is off (RENZO_CF_BUILTIN=0) and the external solver didn't clear it")
            }
            // A clearance that was just answered with a challenge is dead.
            ChallengeSolver.dropClearance(finalUrl, sentClearance)
            solveAndRetry(chain, request, finalUrl)
        } catch (e: IOException) {
            throw e
        } catch (e: Exception) {
            // OkHttp's enqueue only handles IOExceptions; don't crash the app.
            throw IOException(e)
        }
    }

    private fun solveAndRetry(chain: Interceptor.Chain, request: Request, finalUrl: HttpUrl): Response {
        val host = finalUrl.host
        // Before any waiting: the solve needs the main looper and Chromium's
        // threads, so a caller on one of them must not park on the site's lock.
        if (!BrowserSolver.canRunHere()) {
            throw IOException("Cloudflare challenge on $host, met on a thread the built-in solver can't wait on; try again")
        }
        when (ChallengeSolver.verdict(host)) {
            ChallengeSolver.Verdict.TRANSPORT -> return viaTransport(chain, request.newBuilder().url(finalUrl).build())
            ChallengeSolver.Verdict.NEEDS_PERSON -> return external(chain, request) ?: throw needsPerson(finalUrl)
            ChallengeSolver.Verdict.BLOCKED -> throw blocked(finalUrl)
            null -> {}
        }

        // One solve per site at a time (see ChallengeSolver.solveBuiltIn).
        val outcome = runBlocking { ChallengeSolver.solveBuiltIn(finalUrl, CFClearance.clearance(finalUrl)) }
        return when (outcome) {
            // Someone else solved it while this one waited.
            null -> if (ChallengeSolver.verdict(host) == null) retryWithClearance(chain, request, finalUrl) else solveAndRetry(chain, request, finalUrl)
            BrowserSolver.Outcome.CLEARED -> retryWithClearance(chain, request, finalUrl)
            BrowserSolver.Outcome.PASSED -> viaTransport(chain, request.newBuilder().url(finalUrl).build())
            BrowserSolver.Outcome.BLOCKED -> throw blocked(finalUrl)
            BrowserSolver.Outcome.NEEDS_PERSON ->
                external(chain, request)?.also { ChallengeSolver.clearVerdict(host) } ?: throw needsPerson(finalUrl)
            BrowserSolver.Outcome.FAILED ->
                external(chain, request) ?: throw IOException("Cloudflare challenge on $host: the built-in solver couldn't run here; try again")
        }
    }

    /**
     * The request again with the jar's (new) clearance and the user agent it
     * belongs to. Still challenged means OkHttp itself is refused there, so
     * the site's requests go through Chromium from now on.
     */
    private fun retryWithClearance(chain: Interceptor.Chain, request: Request, finalUrl: HttpUrl): Response {
        val retry = chain.proceed(
            request.newBuilder()
                .header("User-Agent", ChallengeSolver.userAgentFor(finalUrl.host) ?: CFClearance.currentUserAgent())
                .build(),
        )
        if (ChallengeSolver.classify(retry) == ChallengeSolver.Kind.NONE) return retry
        retry.close()
        ChallengeSolver.setVerdict(finalUrl.host, ChallengeSolver.Verdict.TRANSPORT)
        return viaTransport(chain, request.newBuilder().url(finalUrl).build())
    }

    /**
     * Carries [request] through Chromium, and checks what came back: a fetch
     * can't pass a challenge, so a challenged answer means the browser's own
     * standing ran out. Solve once more in a real page, then try again.
     */
    private fun viaTransport(chain: Interceptor.Chain, request: Request, resolved: Boolean = false): Response {
        val response = ChromiumTransport.execute(request)
        return when (ChallengeSolver.classify(response)) {
            ChallengeSolver.Kind.NONE -> response
            ChallengeSolver.Kind.BLOCKED -> {
                response.close()
                ChallengeSolver.setVerdict(request.url.host, ChallengeSolver.Verdict.BLOCKED)
                throw blocked(request.url)
            }
            ChallengeSolver.Kind.CHALLENGE -> {
                response.close()
                if (resolved) {
                    ChallengeSolver.setVerdict(request.url.host, ChallengeSolver.Verdict.NEEDS_PERSON)
                    throw needsPerson(request.url)
                }
                ChallengeSolver.clearVerdict(request.url.host)
                when (runBlocking { ChallengeSolver.solveBuiltIn(request.url, CFClearance.clearance(request.url)) }) {
                    null, BrowserSolver.Outcome.CLEARED, BrowserSolver.Outcome.PASSED -> {
                        ChallengeSolver.setVerdict(request.url.host, ChallengeSolver.Verdict.TRANSPORT)
                        viaTransport(chain, request, resolved = true)
                    }
                    BrowserSolver.Outcome.BLOCKED -> throw blocked(request.url)
                    BrowserSolver.Outcome.NEEDS_PERSON -> throw needsPerson(request.url)
                    BrowserSolver.Outcome.FAILED -> throw IOException("Cloudflare challenge on ${request.url.host}: the built-in solver couldn't run here; try again")
                }
            }
        }
    }

    private fun needsPerson(url: HttpUrl) =
        IOException("Cloudflare on ${url.host} wants a person to pass its check (it did not finish by itself in the built-in browser); retrying in 30 min")

    private fun blocked(url: HttpUrl) =
        IOException("Cloudflare blocks this server on ${url.host} (\"you have been blocked\"), in a real browser too; retrying in 30 min")

    /** The external FlareSolverr backend, only when it is switched on. Null when it didn't help. */
    private fun external(chain: Interceptor.Chain, originalRequest: Request): Response? {
        if (!Settings.flareSolverrEnabled || CFClearance.externalDown()) return null
        val retry: Request = try {
            val flareResponseFallback = Settings.flareSolverrAsResponseFallback
            val clearanceBefore = CFClearance.clearance(originalRequest.url)
            val flareResponse =
                runBlocking {
                    CFClearance.resolveWithFlareSolver(originalRequest, !flareResponseFallback, clearanceBefore)
                }
            if (flareResponse == null) {
                // Another request for this site solved the challenge while
                // this one waited its turn: retry with its clearance and the
                // user agent it was issued to.
                originalRequest.newBuilder()
                    .header("User-Agent", ChallengeSolver.userAgentFor(originalRequest.url.host) ?: CFClearance.currentUserAgent())
                    .build()
            } else {
                if (flareResponse.message.contains("not detected", ignoreCase = true) &&
                    flareResponseFallback &&
                    flareResponse.solution.status in 200..299 &&
                    flareResponse.solution.response != null &&
                    !flareResponse.solution.response.contains(CHROME_IMAGE_TEMPLATE_REGEX)
                ) {
                    logger.debug { "Falling back to FlareSolverr response" }
                    CFClearance.storeSolution(flareResponse)
                    return Response.Builder()
                        .request(originalRequest)
                        .protocol(okhttp3.Protocol.HTTP_1_1)
                        .code(flareResponse.solution.status)
                        .message("OK")
                        .body(flareResponse.solution.response.toResponseBody())
                        .build()
                }
                if (flareResponse.solution.status !in 200..299) return null
                CFClearance.requestWithFlareSolverr(flareResponse, originalRequest)
            }
        } catch (e: Exception) {
            // Unreachable, a 500 "error solving", a parse error: no help here.
            if (e is java.net.ConnectException || e.message?.contains("Failed to connect", ignoreCase = true) == true) {
                CFClearance.markExternalDown()
            } else {
                logger.warn { "External Cloudflare solver failed for ${originalRequest.url.host}: ${e.message?.take(200)}" }
            }
            return null
        }
        // The site's own answer, outside the try: its errors are its own.
        val response = chain.proceed(retry)
        if (ChallengeSolver.classify(response) == ChallengeSolver.Kind.NONE) return response
        response.close()
        return null
    }

    companion object {
        private val CHROME_IMAGE_TEMPLATE_REGEX = Regex("""<title>(.*?) \(\d+×\d+\)</title>""")
    }
}

/*
 * This class is ported from https://github.com/vvanglro/cf-clearance
 * The original code is licensed under Apache 2.0
*/
object CFClearance {
    private val logger = androidCompatLogger(CFClearance::class.java)
    private val network: NetworkHelper by injectLazy()
    private val client by lazy {
        val timeout = Settings.flareSolverrTimeout.seconds
        network.client
            .newBuilder()
            .callTimeout(timeout.plus(10.seconds).toJavaDuration())
            .readTimeout(timeout.plus(5.seconds).toJavaDuration())
            .build()
    }
    private val json: Json by injectLazy()
    private val jsonMediaType = "application/json".toMediaType()

    // One solve per site at a time, and at most two sites at once. This was a
    // single global mutex: every challenged request on every site queued
    // behind one ~60 s solve, each holding a sidecar worker in runBlocking
    // while it waited, until all of them were blocked and nothing completed.
    // That is what the watchdog kept killing as "unresponsive".
    private val hostLocks = java.util.concurrent.ConcurrentHashMap<String, Mutex>()
    private val solveSlots = kotlinx.coroutines.sync.Semaphore(2)

    /** The cf_clearance cookie the jar would send to [url] right now, if any. */
    fun clearance(url: HttpUrl): String? = clearanceCookie(url)?.value

    fun clearanceCookie(url: HttpUrl): Cookie? =
        network.cookieStore.get(url).firstOrNull { it.name in COOKIE_NAMES }

    fun hostLock(host: String): Mutex = hostLocks.computeIfAbsent(host) { Mutex() }

    // The external solver crash-looped for weeks (trawl: 602 restarts); every
    // challenged request then waited out a connect failure. Skip it for two
    // minutes after one.
    @Volatile
    private var externalDownUntil = 0L

    fun externalDown(): Boolean = System.currentTimeMillis() < externalDownUntil

    fun markExternalDown() {
        externalDownUntil = System.currentTimeMillis() + 2 * 60_000L
        logger.warn { "External Cloudflare solver unreachable at ${Settings.flareSolverrUrl}; skipping it for 2 min" }
    }

    fun currentUserAgent(): String = network.defaultUserAgentProvider()

    private val COOKIE_NAMES = setOf("cf_clearance")

    @Serializable
    data class FlareSolverCookie(
        val name: String,
        val value: String,
    )

    @Serializable
    data class FlareSolverRequest(
        val cmd: String,
        val url: String,
        val maxTimeout: Int? = null,
        val session: String? = null,
        @SerialName("session_ttl_minutes")
        val sessionTtlMinutes: Int? = null,
        val cookies: List<FlareSolverCookie>? = null,
        val returnOnlyCookies: Boolean? = null,
        val proxy: String? = null,
        val postData: String? = null, // only used with cmd 'request.post'
    )

    @Serializable
    data class FlareSolverSolutionCookie(
        val name: String,
        val value: String,
        val domain: String,
        val path: String? = null,
        val expires: Double? = null,
        @SerialName("expiry")
        val expiry: Double? = null,
        val size: Int? = null,
        val httpOnly: Boolean? = null,
        val secure: Boolean? = null,
        val session: Boolean? = null,
        val sameSite: String? = null,
    )

    // Fields are optional-with-defaults so BOTH FlareSolverr v1 backends (full schema incl.
    // timestamps/version/userAgent) and leaner v2-compatible backends (e.g. trawl, byparr — which may
    // omit any of them) deserialize without a MissingFieldException surfacing as a bypass failure.
    @Serializable
    data class FlareSolverSolution(
        val url: String = "",
        val status: Int = 0,
        val headers: Map<String, String>? = null,
        val response: String? = null,
        val cookies: List<FlareSolverSolutionCookie> = emptyList(),
        val userAgent: String = "",
    )

    @Serializable
    data class FlareSolverResponse(
        val solution: FlareSolverSolution = FlareSolverSolution(),
        val status: String = "",
        val message: String = "",
        val startTimestamp: Long = 0,
        val endTimestamp: Long = 0,
        val version: String = "",
    )

    /**
     * Solves the challenge for [originalRequest]'s site, or returns null when a
     * clearance different from [clearanceBefore] appeared while waiting for the
     * site's lock, meaning someone else just solved it.
     */
    suspend fun resolveWithFlareSolver(
        originalRequest: Request,
        onlyCookies: Boolean,
        clearanceBefore: String? = null,
    ): FlareSolverResponse? {
        val timeout = Settings.flareSolverrTimeout.seconds
        val host = originalRequest.url.host

        return with(json) {
            hostLocks.computeIfAbsent(host) { Mutex() }.withLock {
                val now = clearance(originalRequest.url)
                if (now != null && now != clearanceBefore) return@withLock null
                solveSlots.withPermit {
                client
                    .newCall(
                        POST(
                            url = Settings.flareSolverrUrl.removeSuffix("/") + "/v1",
                            body =
                                Json
                                    .encodeToString(
                                        FlareSolverRequest(
                                            "request.get",
                                            originalRequest.url.toString(),
                                            session = Settings.flareSolverrSessionName,
                                            sessionTtlMinutes = Settings.flareSolverrSessionTtl,
                                            cookies =
                                                network.cookieStore.get(originalRequest.url).map {
                                                    FlareSolverCookie(it.name, it.value)
                                                },
                                            returnOnlyCookies = onlyCookies,
                                            maxTimeout = timeout.inWholeMilliseconds.toInt(),
                                        ),
                                    ).toRequestBody(jsonMediaType),
                        ),
                    ).awaitSuccess()
                    // Parse with the lenient json (ignoreUnknownKeys) — NOT parseAs's strict Json.Default
                    // — so extra fields from any FlareSolverr v1/v2 backend don't fail the whole solve.
                    .parseAs<FlareSolverResponse>(json)
                }
            }
        }
    }

    /**
     * Puts a solution's cookies into the jar and Chromium, and records the user
     * agent they were issued to for their site — never globally: that is what
     * scrambled every later source's frozen user agent (see ChallengeSolver).
     */
    fun storeSolution(flareSolverResponse: FlareSolverResponse) {
        val ua = flareSolverResponse.solution.userAgent
        val cookies =
            flareSolverResponse.solution.cookies
                .map { cookie ->
                    val expirationSeconds = cookie.expires ?: cookie.expiry
                    Cookie
                        .Builder()
                        .name(cookie.name)
                        .value(cookie.value)
                        .domain(cookie.domain.removePrefix("."))
                        .also {
                            if (cookie.httpOnly != null && cookie.httpOnly) it.httpOnly()
                            if (cookie.secure != null && cookie.secure) it.secure()
                            if (!cookie.path.isNullOrEmpty()) it.path(cookie.path)
                            // We need to convert the expires time to milliseconds for the persistent cookie store
                            if (expirationSeconds != null && expirationSeconds > 0) {
                                it.expiresAt((expirationSeconds * 1000).toLong())
                            }
                            if (!cookie.domain.startsWith('.')) it.hostOnlyDomain(cookie.domain.removePrefix("."))
                        }.build()
                }
        for ((domain, forDomain) in cookies.groupBy { it.domain }) {
            val url = HttpUrl.Builder().scheme("https").host(domain.removePrefix(".")).build()
            network.cookieStore.addAll(url, forDomain)
            val clearance = forDomain.firstOrNull { it.name in COOKIE_NAMES }
            if (clearance != null) {
                val expires = if (clearance.persistent) clearance.expiresAt else System.currentTimeMillis() + 30 * 60_000L
                ChallengeSolver.record(domain, ua, expires)
            }
            ChallengeSolver.pushToChromium(url)
        }
        logger.debug { "New cookies\n${cookies.joinToString("; ")}" }
    }

    fun requestWithFlareSolverr(
        flareSolverResponse: FlareSolverResponse,
        originalRequest: Request,
    ): Request {
        if (flareSolverResponse.solution.status in 200..299) {
            storeSolution(flareSolverResponse)
            val finalCookies =
                network.cookieStore.get(originalRequest.url).joinToString("; ", postfix = "; ") {
                    "${it.name}=${it.value}"
                }
            logger.debug { "Final cookies\n$finalCookies" }
            return originalRequest
                .newBuilder()
                .header("Cookie", finalCookies)
                .header("User-Agent", flareSolverResponse.solution.userAgent)
                .build()
        } else {
            logger.debug { "Cloudflare challenge failed to resolve" }
            throw CloudflareBypassException()
        }
    }

    private class CloudflareBypassException : Exception()
}
