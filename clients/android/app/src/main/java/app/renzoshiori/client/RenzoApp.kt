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
        app.renzoshiori.client.ui.util.AdultFilter.init(this)
        // Persist any crash so MainActivity can show the stack trace on the
        // next launch (there's no adb on the server this app talks to).
        // The process MUST die afterwards: handing off to a missing default
        // handler leaves the UI thread dead with the window still on screen,
        // which looks like the app "glitching to a black screen".
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            runCatching {
                java.io.File(filesDir, "last-crash.txt")
                    .writeText(android.util.Log.getStackTraceString(throwable))
            }
            runCatching { previous?.uncaughtException(thread, throwable) }
            android.os.Process.killProcess(android.os.Process.myPid())
            kotlin.system.exitProcess(10)
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
                // Screenshot build only: swap every image for bundled art (see
                // PlaceholderArtInterceptor). Compiled out of real builds.
                if (BuildConfig.PLACEHOLDER_ART) add(PlaceholderArtInterceptor())
            }
            .build()
    }
}

/**
 * Replaces every image the app loads — covers, thumbnails, reader pages,
 * avatars — with bundled placeholder art, so Play Store screenshots contain no
 * third-party artwork. Sitting in the Coil pipeline means it catches every
 * image regardless of which screen requested it or what the model type was.
 *
 * The choice is a stable hash of the original request, so the same series keeps
 * the same cover across screens and the library grid looks varied rather than
 * one tile repeated.
 */
private class PlaceholderArtInterceptor : coil3.intercept.Interceptor {
    private val covers = intArrayOf(
        R.drawable.ph_cover_1, R.drawable.ph_cover_2, R.drawable.ph_cover_3,
        R.drawable.ph_cover_4, R.drawable.ph_cover_5, R.drawable.ph_cover_6,
    )
    private val pages = intArrayOf(R.drawable.ph_page_1, R.drawable.ph_page_2, R.drawable.ph_page_3)

    override suspend fun intercept(chain: coil3.intercept.Interceptor.Chain): coil3.request.ImageResult {
        val original = chain.request.data.toString()
        // Reader pages get page-shaped art; everything else gets a cover.
        val isPage = original.contains("/reader/page") || original.contains("/reader/stream/page")
        val pool = if (isPage) pages else covers
        val pick = pool[(original.hashCode().let { if (it == Int.MIN_VALUE) 0 else kotlin.math.abs(it) }) % pool.size]
        return chain.withRequest(
            coil3.request.ImageRequest.Builder(chain.request).data(pick).build(),
        ).proceed()
    }
}
