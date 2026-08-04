package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.auth.SessionEvents
import app.renzoshiori.client.data.auth.TokenStore
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.CookieJar
import okhttp3.HttpUrl
import okhttp3.Interceptor
import okhttp3.OkHttpClient
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
private class RefreshCookieJar : CookieJar {
    private val store = ConcurrentHashMap<String, List<Cookie>>()
    override fun saveFromResponse(url: HttpUrl, cookies: List<Cookie>) {
        store[url.host] = cookies
    }
    override fun loadForRequest(url: HttpUrl): List<Cookie> = store[url.host] ?: emptyList()
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

        // A 401 anywhere other than the auth endpoints themselves means the
        // session is over. Announce it once so the app returns to the login
        // screen; otherwise every screen just renders empty with no
        // explanation, which reads as "the app is broken".
        if (response.code == 401 && !request.url.encodedPath.startsWith("/api/auth/")) {
            SessionEvents.notifyExpired()
        }
        return response
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
    private val cookieJar = RefreshCookieJar()

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
