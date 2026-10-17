package eu.kanade.tachiyomi.network

/*
 * Copyright (C) Contributors to the Suwayomi project
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

import android.content.Context
import eu.kanade.tachiyomi.network.interceptor.CloudflareInterceptor
import eu.kanade.tachiyomi.network.interceptor.IgnoreGzipInterceptor
import eu.kanade.tachiyomi.network.interceptor.UncaughtExceptionInterceptor
import eu.kanade.tachiyomi.network.interceptor.UserAgentInterceptor
import kotlinx.coroutines.DelicateCoroutinesApi
import kotlinx.coroutines.GlobalScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import okhttp3.Cache
import okhttp3.OkHttpClient
import okhttp3.brotli.BrotliInterceptor
import okhttp3.logging.HttpLoggingInterceptor
import extension.bridge.logging.androidCompatLogger
import java.net.CookieHandler
import java.net.CookieManager
import java.net.CookiePolicy
import java.nio.file.Files
import java.util.concurrent.TimeUnit

class NetworkHelper(
    context: Context,
) {
    //    private val preferences: PreferencesHelper by injectLazy()

//    private val cacheDir = File(context.cacheDir, "network_cache")

//    private val cacheSize = 5L * 1024 * 1024 // 5 MiB

    // Tachidesk -->
    val cookieStore = PersistentCookieStore(context)

    init {
        CookieHandler.setDefault(
            CookieManager(cookieStore, CookiePolicy.ACCEPT_ALL),
        )
    }
    // Tachidesk <--

    // The default user agent is the one the embedded Chromium really is, read
    // from its install: a WebView presenting one browser while its engine is
    // another is exactly what Cloudflare's checks look for, and Chrome/120 was
    // nearly three years out of date. Sources freeze this into their headers on
    // first use, so it is settled here, before any of them loads.
    private val userAgent = MutableStateFlow(chromiumUserAgent())

    private fun chromiumUserAgent(): String {
        val major = runCatching {
            val meta = java.io.File(System.getProperty("jcef.dir") ?: "", "build_meta.json")
            Regex("chromium-(\\d+)\\.").find(meta.readText())?.groupValues?.get(1)
        }.getOrNull() ?: "141"
        return "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/$major.0.0.0 Safari/537.36"
    }
    val userAgentFlow = userAgent.asStateFlow()

    fun defaultUserAgentProvider(): String = userAgent.value

    /** Adopt a Cloudflare solver's user agent (its clearance is bound to it). */
    fun setUserAgent(ua: String) {
        if (ua.isNotBlank()) userAgent.value = ua
    }

    init {
        @OptIn(DelicateCoroutinesApi::class)
        userAgent
            .drop(1)
            .launchIn(GlobalScope)
    }

    private val baseClientBuilder: OkHttpClient.Builder
        get() {
            val builder =
                OkHttpClient
                    .Builder()
                    .cookieJar(PersistentCookieJar(cookieStore))
                    .connectTimeout(30, TimeUnit.SECONDS)
                    .readTimeout(30, TimeUnit.SECONDS)
                    .callTimeout(2, TimeUnit.MINUTES)
                    .cache(
                        Cache(
                            directory = Files.createTempDirectory("tachidesk_network_cache").toFile(),
                            maxSize = 5L * 1024 * 1024, // 5 MiB
                        ),
                    ).addInterceptor(UncaughtExceptionInterceptor())
                    .addInterceptor(UserAgentInterceptor(::defaultUserAgentProvider))
                    // NB: no IgnoreGzipInterceptor / BrotliInterceptor on the default client. okhttp
                    // requests+decodes gzip transparently and doesn't ask for brotli unless told, so
                    // older sources are unaffected; newer keiyoushi extensions ship their own
                    // CompressionInterceptor and ASSERT both are absent from the default client
                    // ("IgnoreGzipInterceptor/BrotliInterceptor must not be present in default client").
                    // Matches current upstream Suwayomi/Mihon, which run the same 1.6 extensions.

            // if (preferences.verboseLogging().get()) {
            val httpLoggingInterceptor =
                HttpLoggingInterceptor(
                    object : HttpLoggingInterceptor.Logger {
                        val logger = androidCompatLogger(NetworkHelper::class.java)

                        override fun log(message: String) {
                            logger.debug { message }
                        }
                    },
                ).apply {
                    level = HttpLoggingInterceptor.Level.BASIC
                }
            builder.addNetworkInterceptor(httpLoggingInterceptor)
            // }

            builder.addInterceptor(CloudflareInterceptor())

            // when (preferences.dohProvider().get()) {
            //     PREF_DOH_CLOUDFLARE -> builder.dohCloudflare()
            //     PREF_DOH_GOOGLE -> builder.dohGoogle()
            //     PREF_DOH_ADGUARD -> builder.dohAdGuard()
            //     PREF_DOH_QUAD9 -> builder.dohQuad9()
            //     PREF_DOH_ALIDNS -> builder.dohAliDNS()
            //     PREF_DOH_DNSPOD -> builder.dohDNSPod()
            //     PREF_DOH_360 -> builder.doh360()
            //     PREF_DOH_QUAD101 -> builder.dohQuad101()
            //     PREF_DOH_MULLVAD -> builder.dohMullvad()
            //     PREF_DOH_CONTROLD -> builder.dohControlD()
            //     PREF_DOH_NJALLA -> builder.dohNajalla()
            //     PREF_DOH_SHECAN -> builder.dohShecan()
            // }

            return builder
        }

//    val client by lazy { baseClientBuilder.cache(Cache(cacheDir, cacheSize)).build() }
    val client by lazy { baseClientBuilder.build() }

    val cloudflareClient by lazy { client }
}
