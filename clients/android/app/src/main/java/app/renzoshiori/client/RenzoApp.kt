package app.renzoshiori.client

import android.app.Application
import app.renzoshiori.client.data.auth.TokenStore
import app.renzoshiori.client.data.network.NetworkModule
import app.renzoshiori.client.data.offline.OfflineRepository
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import okhttp3.OkHttpClient

/**
 * Composition root — deliberately manual (no Hilt/Koin) for an app this size:
 * one TokenStore, one NetworkModule, one Room DB, one Coil loader that
 * attaches the same Bearer header the REST client uses so page images and
 * thumbnails authenticate identically.
 */
class RenzoApp : Application(), SingletonImageLoader.Factory {
    val tokenStore: TokenStore by lazy { TokenStore(this) }
    val network: NetworkModule by lazy { NetworkModule(tokenStore) }
    val offline: OfflineRepository by lazy { OfflineRepository(this) }

    override fun onCreate() {
        super.onCreate()
        // Persist any crash so MainActivity can show the stack trace on the
        // next launch (there's no adb on the server this app talks to).
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            runCatching {
                java.io.File(filesDir, "last-crash.txt")
                    .writeText(android.util.Log.getStackTraceString(throwable))
            }
            previous?.uncaughtException(thread, throwable)
        }
    }

    override fun newImageLoader(context: PlatformContext): ImageLoader {
        val authedClient = OkHttpClient.Builder()
            .addInterceptor { chain ->
                val token = tokenStore.accessToken
                val req = chain.request().newBuilder().apply {
                    if (token != null) addHeader("Authorization", "Bearer $token")
                }.build()
                chain.proceed(req)
            }
            .build()
        return ImageLoader.Builder(context)
            .components {
                add(OkHttpNetworkFetcherFactory(callFactory = { authedClient }))
            }
            .build()
    }
}
