package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.auth.SessionEvents
import app.renzoshiori.client.data.auth.TokenStore
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.CookieJar
import okhttp3.HttpUrl
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.logging.HttpLoggingInterceptor
import okhttp3.MediaType.Companion.toMediaType
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import java.util.concurrent.ConcurrentHashMap

/**
 * `coerceInputValues` is load-bearing, not tidiness: plenty of backend DTOs
 * declare non-nullable C# reference types with no initializer (ExtensionDto's
 * `package`/`thumbnailUrl`, ExtensionRepositoryDto's `name`/`id`, …), so the
 * wire can legitimately carry `null` where the Kotlin field is a non-null
 * `String` with a default. Without coercion kotlinx throws and the whole
 * screen fails to load; with it, null falls back to the declared default.
 */
private val json = Json {
    ignoreUnknownKeys = true
    encodeDefaults = false
    coerceInputValues = true
    explicitNulls = false
}

/**
 * In-memory cookie jar scoped to exactly the one cookie the backend actually
 * needs from a native client: the httpOnly `refresh_token` set by
 * POST /api/auth/login (rememberMe=true), read back by POST /api/auth/refresh.
 * Everything else is stateless Bearer auth via [AuthInterceptor] — no full
 * cookie-jar/session model needed.
 */
private class RefreshCookieJar(private val tokenStore: TokenStore) : CookieJar {
    private val store = ConcurrentHashMap<String, List<Cookie>>()

    override fun saveFromResponse(url: HttpUrl, cookies: List<Cookie>) {
        store[url.host] = cookies
        // Persist the remember-me cookie so it survives the process. Kept in the
        // encrypted store, not here, because this jar dies with the app.
        cookies.firstOrNull { it.name == REFRESH_COOKIE }?.let { tokenStore.refreshCookie = it.value }
    }

    override fun loadForRequest(url: HttpUrl): List<Cookie> {
        val live = store[url.host]
        if (!live.isNullOrEmpty()) return live
        // First request after a cold start: rebuild the refresh cookie from the
        // encrypted store, otherwise "Remember me" only lasted as long as the
        // process did.
        val saved = tokenStore.refreshCookie ?: return emptyList()
        val cookie = Cookie.Builder()
            .name(REFRESH_COOKIE)
            .value(saved)
            .domain(url.host)
            .path("/")
            .httpOnly()
            .build()
        return listOf(cookie)
    }

    private companion object {
        const val REFRESH_COOKIE = "refresh_token"
    }
}

/**
 * Attaches `Authorization: Bearer <token>` to every request, including image
 * loads. When the server has authentication disabled there is no token at
 * all: the backend then resolves the caller from an `X-Renzo-User` header
 * (AuthMiddleware), which is exactly what the web apiClient sends in profile
 * mode — without it the app would talk to the server as an anonymous guest
 * and no per-user data (library, progress, sources) would resolve.
 */
private class AuthInterceptor(private val tokenStore: TokenStore) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val token = tokenStore.accessToken
        val request = chain.request().newBuilder().apply {
            if (token != null) {
                addHeader("Authorization", "Bearer $token")
            } else {
                tokenStore.lastUsername?.takeIf { it.isNotBlank() }
                    ?.let { addHeader("X-Renzo-User", it) }
            }
        }.build()
        val response = chain.proceed(request)

        if (response.code != 401 || request.url.encodedPath.startsWith("/api/auth/"))
            return response

        // The access token is short-lived (the server's sessionExpirationHours,
        // 24 by default) REGARDLESS of "Remember me" — remember-me is carried by
        // the long-lived refresh cookie, and the client is supposed to trade it
        // for a fresh access token. Without this the user was signed out after a
        // day no matter what they ticked.
        val renewed = renewAccessToken(chain, request)
        if (renewed != null) {
            response.close()
            return chain.proceed(
                request.newBuilder().header("Authorization", "Bearer $renewed").build(),
            )
        }

        // No refresh possible: the session really is over. Announce it once so
        // the app returns to the login screen instead of every screen quietly
        // rendering empty.
        SessionEvents.notifyExpired()
        return response
    }

    /**
     * Trades the refresh cookie for a new access token. Single-flight: a burst
     * of concurrent 401s must not fire parallel refreshes, because the endpoint
     * ROTATES the refresh token and the losers would invalidate the winner.
     * Returns null when there is nothing to refresh with, or the server says no.
     */
    private fun renewAccessToken(chain: Interceptor.Chain, original: Request): String? {
        synchronized(refreshLock) {
            // Someone else refreshed while this thread waited on the lock.
            val current = tokenStore.accessToken
            if (current != null && current != original.header("Authorization")?.removePrefix("Bearer ")) {
                return current
            }
            if (tokenStore.refreshCookie == null) return null

            val refreshUrl = original.url.newBuilder()
                .encodedPath("/api/auth/refresh")
                .query(null)
                .build()
            val request = Request.Builder()
                .url(refreshUrl)
                .post(ByteArray(0).toRequestBody(null, 0, 0))
                .build()

            val response = runCatching { chain.proceed(request) }.getOrNull() ?: return null
            response.use {
                if (!it.isSuccessful) {
                    // A refused refresh means the cookie is spent — drop it so we
                    // don't retry on every subsequent call.
                    tokenStore.refreshCookie = null
                    return null
                }
                val body = runCatching { it.body?.string() }.getOrNull() ?: return null
                val token = TOKEN_REGEX.find(body)?.groupValues?.getOrNull(1) ?: return null
                tokenStore.accessToken = token
                return token
            }
        }
    }

    private companion object {
        val refreshLock = Any()

        /** The refresh response is `{ "token": "...", "user": { ... } }`. */
        val TOKEN_REGEX = Regex("\"token\"\\s*:\\s*\"([^\"]+)\"")
    }
}

/**
 * Per-request timeout override. A few endpoints do all their work inline and
 * can run for minutes — `POST /api/scrobbler/matches/auto` walks the whole
 * library against the tracker's API before it answers — so they carry
 * `X-Renzo-Timeout: <seconds>` and this strips the header back off before the
 * request leaves. Without it those calls die on the default read timeout and
 * surface as a generic failure even though the server is working fine.
 */
const val TIMEOUT_HEADER = "X-Renzo-Timeout"

private class PerRequestTimeoutInterceptor : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        val seconds = request.header(TIMEOUT_HEADER)?.toIntOrNull()
            ?: return chain.proceed(request)
        val stripped = request.newBuilder().removeHeader(TIMEOUT_HEADER).build()
        return chain
            .withReadTimeout(seconds, java.util.concurrent.TimeUnit.SECONDS)
            .withWriteTimeout(seconds, java.util.concurrent.TimeUnit.SECONDS)
            .proceed(stripped)
    }
}

/**
 * Rebuilds the Retrofit/OkHttp stack whenever the connected server changes —
 * the base URL isn't known at compile time, it's whatever the user entered on
 * the Connect screen (see ConnectScreen / SystemInfoApi.isRenzoServer).
 */
class NetworkModule(private val tokenStore: TokenStore) {
    private val cookieJar = RefreshCookieJar(tokenStore)

    private val okHttpClient: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .cookieJar(cookieJar)
            .addInterceptor(AuthInterceptor(tokenStore))
            .addInterceptor(PerRequestTimeoutInterceptor())
            .addInterceptor(HttpLoggingInterceptor().apply { level = HttpLoggingInterceptor.Level.BASIC })
            // OkHttp defaults to a 10s read timeout, which several endpoints
            // legitimately exceed: /api/provider/list walks the online
            // extension repositories, scans and imports do real work, and
            // stream/page pulls an image from the source site.
            .connectTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
            .readTimeout(90, java.util.concurrent.TimeUnit.SECONDS)
            .writeTimeout(90, java.util.concurrent.TimeUnit.SECONDS)
            .build()
    }

    fun retrofitFor(baseUrl: String): Retrofit = Retrofit.Builder()
        .baseUrl(if (baseUrl.endsWith("/")) baseUrl else "$baseUrl/")
        .client(okHttpClient)
        .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
        .build()

    /** Retrofit client for the currently-connected server (throws if none set yet). */
    fun apiFor(baseUrl: String): ApiService = retrofitFor(baseUrl).create(ApiService::class.java)

    /** Convenience for call sites that already have a connected TokenStore.serverUrl. */
    fun currentApi(): ApiService? = tokenStore.serverUrl?.let { apiFor(it) }

    /**
     * Domain-specific Retrofit interfaces (StatusApi, SourcesApi, AccountApi…)
     * live in their own files so they can grow independently of [ApiService].
     */
    inline fun <reified T> currentServiceOf(): T? =
        serverUrl?.let { retrofitFor(it).create(T::class.java) }

    val serverUrl: String? get() = tokenStore.serverUrl
}
