package app.renzoshiori.client.data.network

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

private val json = Json { ignoreUnknownKeys = true; encodeDefaults = false }

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

/** Attaches `Authorization: Bearer <token>` to every request, including image loads. */
private class AuthInterceptor(private val tokenStore: TokenStore) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val token = tokenStore.accessToken
        val request = chain.request().newBuilder().apply {
            if (token != null) addHeader("Authorization", "Bearer $token")
        }.build()
        return chain.proceed(request)
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
            .addInterceptor(HttpLoggingInterceptor().apply { level = HttpLoggingInterceptor.Level.BASIC })
            .build()
    }

    /** Retrofit client for the currently-connected server (throws if none set yet). */
    fun apiFor(baseUrl: String): ApiService {
        val retrofit = Retrofit.Builder()
            .baseUrl(if (baseUrl.endsWith("/")) baseUrl else "$baseUrl/")
            .client(okHttpClient)
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()
        return retrofit.create(ApiService::class.java)
    }

    /** Convenience for call sites that already have a connected TokenStore.serverUrl. */
    fun currentApi(): ApiService? = tokenStore.serverUrl?.let { apiFor(it) }
}
