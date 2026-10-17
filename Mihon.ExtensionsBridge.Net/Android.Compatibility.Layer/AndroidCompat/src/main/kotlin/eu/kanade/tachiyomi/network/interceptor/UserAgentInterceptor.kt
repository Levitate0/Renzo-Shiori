package eu.kanade.tachiyomi.network.interceptor

import okhttp3.Interceptor
import okhttp3.Response

class UserAgentInterceptor(
    private val userAgentProvider: () -> String,
) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val originalRequest = chain.request()

        // A site we hold a Cloudflare clearance for gets the user agent that
        // clearance was issued to, whatever the source froze into its headers:
        // Cloudflare rejects the clearance under any other one. Every client
        // has this interceptor (keiyoushi extensions assert it), including the
        // ones that strip CloudflareInterceptor to run their own WebView solve.
        ChallengeSolver.userAgentFor(originalRequest.url.host)?.let { ua ->
            if (originalRequest.header("User-Agent") != ua) {
                return chain.proceed(originalRequest.newBuilder().header("User-Agent", ua).build())
            }
        }

        return if (originalRequest.header("User-Agent").isNullOrEmpty()) {
            val newRequest =
                originalRequest
                    .newBuilder()
                    .removeHeader("User-Agent")
                    .addHeader("User-Agent", userAgentProvider())
                    .build()
            chain.proceed(newRequest)
        } else {
            chain.proceed(originalRequest)
        }
    }
}
