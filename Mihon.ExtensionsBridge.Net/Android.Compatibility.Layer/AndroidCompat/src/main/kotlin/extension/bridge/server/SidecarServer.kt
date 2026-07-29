package extension.bridge.server

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import eu.kanade.tachiyomi.source.CatalogueSource
import eu.kanade.tachiyomi.source.ConfigurableSource
import eu.kanade.tachiyomi.source.Source
import eu.kanade.tachiyomi.source.SourceFactory
import eu.kanade.tachiyomi.source.model.FilterList
import eu.kanade.tachiyomi.source.model.Page
import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga
import eu.kanade.tachiyomi.source.online.HttpSource
import extension.bridge.applicationSetup
import extension.bridge.loadExtensionSources
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import java.io.InputStream
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

/**
 * A minimal, self-contained JVM sidecar that hosts Mihon extensions on a real JVM (no IKVM) and
 * exposes source operations over local HTTP. It runs THIS repo's AndroidCompat unchanged, so every
 * custom Kotlin-side fix (interceptors, WebView/Cloudflare handling, network tweaks) carries over.
 *
 * Only JDK built-ins (com.sun.net.httpserver) + kotlinx.serialization (already a dependency) are used.
 * The .NET backend is the sole client and talks to it on 127.0.0.1 only.
 *
 * Endpoints (all POST unless noted; JSON in/out, except /image which returns raw bytes):
 *   /setup              {dataRoot, tempRoot}
 *   /sources/load       {jarPath, className}                 -> [SourceMeta]
 *   /source/popular     {id, page}                           -> MangasPage
 *   /source/latest      {id, page}                           -> MangasPage
 *   /source/search      {id, page, query}                    -> MangasPage
 *   /source/details     {id, manga}                          -> Manga (+realUrl)
 *   /source/chapters    {id, manga}                          -> [Chapter]
 *   /source/pages       {id, chapter}                        -> [Page]
 *   /source/image       {id, page}                           -> raw image bytes (Content-Type set)
 *   /source/preferences {id}                                 -> [Preference]
 *   /source/preference  {id, position, value}
 *   /source/unload      {jarPath}
 *   /health (GET)
 */
object SidecarServer {
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val sources = ConcurrentHashMap<Long, CatalogueSource>()
    @Volatile private var initialized = false

    @JvmStatic
    fun main(args: Array<String>) {
        val port = (System.getenv("RENZO_SIDECAR_PORT") ?: "9834").toInt()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", port), 0)
        server.executor = Executors.newFixedThreadPool(
            (System.getenv("RENZO_SIDECAR_THREADS") ?: "16").toInt(),
        )

        server.createContext("/health") { ex -> respond(ex, 200, """{"ok":true,"initialized":$initialized}""") }
        server.createContext("/setup") { ex -> guarded(ex) { setup(readObj(ex)) } }
        server.createContext("/config") { ex -> guarded(ex) { config(readObj(ex)) } }
        server.createContext("/cookies/inject") { ex -> guarded(ex) { cookiesInject(readObj(ex)) } }
        server.createContext("/cookies/snapshot") { ex -> guarded(ex) { cookiesSnapshot(readObj(ex)) } }
        server.createContext("/cookies/clear") { ex -> guarded(ex) { cookiesClear(readObj(ex)) } }
        server.createContext("/convert") { ex -> guarded(ex) { convert(readObj(ex)) } }
        server.createContext("/sources/load") { ex -> guarded(ex) { loadSources(readObj(ex)) } }
        server.createContext("/source/popular") { ex -> guarded(ex) { popular(readObj(ex)) } }
        server.createContext("/source/latest") { ex -> guarded(ex) { latest(readObj(ex)) } }
        server.createContext("/source/search") { ex -> guarded(ex) { search(readObj(ex)) } }
        server.createContext("/source/details") { ex -> guarded(ex) { details(readObj(ex)) } }
        server.createContext("/source/chapters") { ex -> guarded(ex) { chapters(readObj(ex)) } }
        server.createContext("/source/pages") { ex -> guarded(ex) { pages(readObj(ex)) } }
        server.createContext("/source/image") { ex -> image(ex) }
        server.createContext("/source/preferences") { ex -> guarded(ex) { preferences(readObj(ex)) } }
        server.createContext("/source/preference") { ex -> guarded(ex) { setPreference(readObj(ex)) } }
        server.createContext("/source/unload") { ex -> guarded(ex) { unload(readObj(ex)) } }

        server.start()
        System.err.println("[sidecar] listening on 127.0.0.1:$port")
        Thread.currentThread().join()
    }

    // ---- endpoint impls (return a JSON string) ----

    private fun setup(req: JsonObject): String {
        if (!initialized) {
            val dataRoot = req["dataRoot"]!!.jsonPrimitive.content
            val tempRoot = req["tempRoot"]!!.jsonPrimitive.content
            applicationSetup(dataRoot, tempRoot) { level, tag, message, throwable ->
                System.err.println("[compat/$level] $tag: $message")
                if (throwable != null) System.err.println(throwable)
            }
            initialized = true
        }
        return """{"ok":true}"""
    }

    // Applies the app's network settings (FlareSolverr/Cloudflare, SOCKS proxy) to the engine,
    // mirroring what BridgeManager.SetPreferencesAsync does for the IKVM path. Without this the
    // CloudflareInterceptor sees flareSolverrEnabled=false and throws "Cloudflare bypass ... disabled".
    private fun config(req: JsonObject): String {
        val cur = extension.bridge.getSettings()
        fun b(k: String, d: Boolean) = (req[k] as? JsonPrimitive)?.let { it.content.toBooleanStrictOrNull() } ?: d
        fun i(k: String, d: Int) = (req[k] as? JsonPrimitive)?.content?.toIntOrNull() ?: d
        fun s(k: String, d: String) = (req[k] as? JsonPrimitive)?.takeIf { it.isString }?.content ?: d
        val settings = extension.bridge.SettingsConfig.Settings(
            socksProxyEnabled = b("socksProxyEnabled", cur.socksProxyEnabled),
            socksProxyVersion = i("socksProxyVersion", cur.socksProxyVersion),
            socksProxyHost = s("socksProxyHost", cur.socksProxyHost),
            socksProxyPort = s("socksProxyPort", cur.socksProxyPort),
            socksProxyUsername = s("socksProxyUsername", cur.socksProxyUsername),
            socksProxyPassword = s("socksProxyPassword", cur.socksProxyPassword),
            flareSolverrEnabled = b("flareSolverrEnabled", cur.flareSolverrEnabled),
            flareSolverrUrl = s("flareSolverrUrl", cur.flareSolverrUrl),
            flareSolverrTimeout = i("flareSolverrTimeout", cur.flareSolverrTimeout),
            flareSolverrSessionName = s("flareSolverrSessionName", cur.flareSolverrSessionName),
            flareSolverrSessionTtl = i("flareSolverrSessionTtl", cur.flareSolverrSessionTtl),
            flareSolverrAsResponseFallback = b("flareSolverrAsResponseFallback", cur.flareSolverrAsResponseFallback),
            interceptorOverrides = cur.interceptorOverrides,
        )
        extension.bridge.setSettings(settings)
        return """{"ok":true}"""
    }

    // The engine registers its PersistentCookieStore as the JVM default cookie handler
    // (CookieHandler.setDefault). Site-login cookies harvested by the app (Violet Scans and other
    // coin sites) are injected here so the sources' OkHttp clients send them and serve owned chapters.
    private fun cookieStore(): java.net.CookieStore? =
        (java.net.CookieHandler.getDefault() as? java.net.CookieManager)?.cookieStore

    private fun cookiesInject(req: JsonObject): String {
        val store = cookieStore() ?: return """{"added":0}"""
        val arr = req["cookies"] as? JsonArray ?: JsonArray(emptyList())
        var added = 0
        for (el in arr) {
            val o = el as? JsonObject ?: continue
            fun str(k: String) = (o[k] as? JsonPrimitive)?.content ?: ""
            val name = str("name"); val value = str("value")
            val domain = str("domain"); if (name.isEmpty() || domain.isEmpty()) continue
            val path = str("path").ifEmpty { "/" }
            val secure = (o["secure"] as? JsonPrimitive)?.content?.toBooleanStrictOrNull() ?: true
            try {
                val cookie = java.net.HttpCookie(name, value)
                cookie.domain = domain
                cookie.path = path
                cookie.secure = secure
                cookie.version = 0
                val host = domain.trimStart('.')
                store.add(java.net.URI("https://$host/"), cookie)
                added++
            } catch (_: Throwable) { /* skip bad cookie */ }
        }
        return """{"added":$added}"""
    }

    private fun cookiesSnapshot(req: JsonObject): String {
        val store = cookieStore()
        val host = ((req["host"] as? JsonPrimitive)?.content ?: "").trimStart('.')
        return buildJsonObject {
            put("cookies", buildJsonArray {
                if (store != null && host.isNotEmpty()) {
                    val cookies = store.get(java.net.URI("https://$host/"))
                    for (c in cookies) {
                        add(buildJsonObject {
                            put("name", c.name)
                            put("value", c.value)
                            put("domain", if (c.domain.isNullOrEmpty()) host else c.domain)
                            put("path", if (c.path.isNullOrEmpty()) "/" else c.path)
                            put("secure", c.secure)
                        })
                    }
                }
            })
        }.toString()
    }

    private fun cookiesClear(req: JsonObject): String {
        val store = cookieStore() ?: return """{"ok":true}"""
        val host = ((req["host"] as? JsonPrimitive)?.content ?: "").trimStart('.')
        if (host.isNotEmpty()) {
            try {
                for (c in store.cookies.toList()) {
                    val d = (c.domain ?: "").trimStart('.')
                    if (d.equals(host, true) || host.endsWith(".$d", true)) {
                        try { store.remove(java.net.URI("https://$d/"), c) } catch (_: Throwable) {}
                    }
                }
            } catch (_: Throwable) { /* best-effort */ }
        }
        return """{"ok":true}"""
    }

    private fun convert(req: JsonObject): String {
        val apkPath = req["apkPath"]!!.jsonPrimitive.content
        val jarPath = req["jarPath"]!!.jsonPrimitive.content
        SidecarConvert.convert(apkPath, jarPath)
        return buildJsonObject { put("ok", true); put("jarPath", jarPath) }.toString()
    }

    private fun loadSources(req: JsonObject): String {
        val jarPath = req["jarPath"]!!.jsonPrimitive.content
        val className = req["className"]!!.jsonPrimitive.content
        val instance = loadExtensionSources(jarPath, className)
        val list: List<Source> = when (instance) {
            is SourceFactory -> instance.createSources()
            is Source -> listOf(instance)
            else -> throw IllegalStateException("Unknown source class type: ${instance.javaClass}")
        }
        val metas = buildJsonArray {
            for (s in list) {
                if (s is CatalogueSource) {
                    sources[s.id] = s
                    add(sourceMeta(s))
                }
            }
        }
        return metas.toString()
    }

    private fun src(req: JsonObject): CatalogueSource {
        val id = req["id"]!!.jsonPrimitive.content.toLong()
        return sources[id] ?: throw IllegalStateException("Source $id not loaded")
    }

    private fun popular(req: JsonObject): String {
        val s = src(req); val page = req["page"]!!.jsonPrimitive.content.toInt()
        return runBlocking { s.getPopularManga(page) }.let(::mangasPage)
    }

    private fun latest(req: JsonObject): String {
        val s = src(req); val page = req["page"]!!.jsonPrimitive.content.toInt()
        return runBlocking { s.getLatestUpdates(page) }.let(::mangasPage)
    }

    private fun search(req: JsonObject): String {
        val s = src(req)
        val page = req["page"]!!.jsonPrimitive.content.toInt()
        val query = req["query"]?.jsonPrimitive?.content ?: ""
        val filters: FilterList = try { s.getFilterList() } catch (_: Throwable) { FilterList() }
        return runBlocking { s.getSearchManga(page, query, filters) }.let(::mangasPage)
    }

    // Sources build requests as GET(baseUrl + entry.url), i.e. they expect a source-RELATIVE url.
    // The app, however, stores urls absolute (Models.Chapter.Url = getChapterUrl, and some series'
    // manga urls too). Feeding an absolute url straight through yields GET(baseUrl + "https://host/…")
    // = a doubled host ("violetscans.orghttps://…: Name or service not known"). Strip the source's own
    // baseUrl so both absolute (stored) and relative (fresh) inputs converge to the relative form the
    // source wants. No-op when the url is already relative or from a different host.
    private fun relBase(s: eu.kanade.tachiyomi.source.Source, url: String): String {
        if (s is HttpSource) {
            val b = s.baseUrl
            if (b.isNotEmpty() && url.startsWith(b)) return url.substring(b.length)
        }
        return url
    }

    private fun details(req: JsonObject): String {
        val s = src(req)
        val manga = mangaFrom(req["manga"]!! as JsonObject).also { it.url = relBase(s, it.url) }
        // Go through the 1.6 combined call: newer sources implement getMangaUpdate and leave
        // getMangaDetails throwing; the compat default delegates to getMangaDetails for old sources.
        val out = if (s is HttpSource) runBlocking { s.getMangaUpdate(manga, emptyList(), true, false).manga }
                  else runBlocking { s.getMangaDetails(manga) }
        // The 1.6 combined getMangaUpdate returns a manga carrying only CHANGED fields, expecting the
        // caller to merge it onto the existing entry — so SManga's lateinit `url`/`title` are often left
        // unset and throw when we serialize them or call getMangaUrl. Backfill both from the request.
        if (!urlInitialized(out)) out.url = if (urlInitialized(manga)) manga.url else ""
        if (!titleInitialized(out)) out.title = if (titleInitialized(manga)) manga.title else ""
        val realUrl = if (s is HttpSource) runCatching { s.getMangaUrl(out) }.getOrNull() else null
        return buildJsonObject {
            put("manga", mangaJson(out))
            if (realUrl != null) put("realUrl", realUrl)
        }.toString()
    }

    private fun chapters(req: JsonObject): String {
        val s = src(req)
        val manga = mangaFrom(req["manga"]!! as JsonObject).also { it.url = relBase(s, it.url) }
        val list = if (s is HttpSource) runBlocking { s.getMangaUpdate(manga, emptyList(), false, true).chapters }
                   else runBlocking { s.getChapterList(manga) }
        return buildJsonArray {
            for (c in list) {
                // The absolute browser URL, exactly like the IKVM path's parsed.RealUrl =
                // source.getChapterUrl(chapter). Without it the app's URL-reconstruction fallback
                // rebuilds the address and can double the host (e.g. "violetscans.orghttps://...").
                val realUrl = if (s is HttpSource) runCatching { s.getChapterUrl(c) }.getOrNull() else null
                add(chapterJson(c, realUrl))
            }
        }.toString()
    }

    private fun pages(req: JsonObject): String {
        val s = src(req)
        val chapter = chapterFrom(req["chapter"]!! as JsonObject).also { it.url = relBase(s, it.url) }
        val list = runBlocking { s.getPageList(chapter) }
        return buildJsonArray { for (p in list) add(pageJson(p)) }.toString()
    }

    private fun image(ex: HttpExchange) {
        try {
            val req = readObj(ex)
            val s = src(req) as? HttpSource ?: throw IllegalStateException("Source does not support HTTP")
            val p = pageFrom(req["page"]!! as JsonObject)
            if (p.imageUrl.isNullOrEmpty()) {
                val u = runCatching { runBlocking { s.getImageUrl(p) } }.getOrNull()
                if (!u.isNullOrEmpty()) p.imageUrl = u
            }
            val resp = runBlocking { s.getImage(p) }
            try {
                if (resp.code != 200) throw RuntimeException("Request error! ${resp.code}")
                val body = resp.body
                val ct = body.contentType()?.toString() ?: "application/octet-stream"
                val bytes = body.bytes()
                ex.responseHeaders.set("Content-Type", ct)
                ex.sendResponseHeaders(200, bytes.size.toLong())
                ex.responseBody.use { it.write(bytes) }
            } finally {
                runCatching { resp.close() }
            }
        } catch (t: Throwable) {
            respond(ex, 500, errorJson(t))
        }
    }

    // Preferences: full PreferenceScreen collection/set is implemented in a follow-up
    // (SidecarPreferences). Stubbed here so the first end-to-end slice compiles and runs.
    private fun preferences(req: JsonObject): String {
        src(req)
        return "[]"
    }

    private fun setPreference(req: JsonObject): String {
        src(req)
        return """{"ok":true}"""
    }

    private fun unload(req: JsonObject): String {
        val jarPath = req["jarPath"]!!.jsonPrimitive.content
        extension.bridge.unloadExtension(jarPath)
        return """{"ok":true}"""
    }

    // ---- JSON mapping (field names mirror the source-api model) ----

    private fun sourceMeta(s: CatalogueSource) = buildJsonObject {
        put("id", s.id)
        put("name", s.name)
        put("lang", s.lang)
        put("supportsLatest", s.supportsLatest)
        put("isConfigurable", s is ConfigurableSource)
        put("isHttp", s is HttpSource)
        put("baseUrl", if (s is HttpSource) s.baseUrl else "")
        put("versionId", if (s is HttpSource) s.versionId else 0)
    }

    private fun mangasPage(mp: eu.kanade.tachiyomi.source.model.MangasPage) = buildJsonObject {
        put("hasNextPage", mp.hasNextPage)
        put("mangas", buildJsonArray { for (m in mp.mangas) add(mangaJson(m)) })
    }.toString()

    // SManga.url is a lateinit var; a source may hand back a manga without it set.
    private fun urlInitialized(m: SManga): Boolean = try { m.url; true } catch (_: Throwable) { false }
    private fun titleInitialized(m: SManga): Boolean = try { m.title; true } catch (_: Throwable) { false }

    private fun mangaJson(m: SManga) = buildJsonObject {
        put("url", if (urlInitialized(m)) m.url else "")
        put("title", if (titleInitialized(m)) m.title else "")
        m.artist?.let { put("artist", it) }
        m.author?.let { put("author", it) }
        m.description?.let { put("description", it) }
        m.genre?.let { put("genre", it) }
        put("status", m.status)
        m.thumbnail_url?.let { put("thumbnail_url", it) }
        put("initialized", m.initialized)
        // carry the 1.6 memo blob so a source can thread data from search -> details/chapters
        if (m.memo.isNotEmpty()) put("memo", m.memo)
    }

    private fun chapterJson(c: SChapter, realUrl: String? = null) = buildJsonObject {
        put("url", c.url)
        if (!realUrl.isNullOrEmpty()) put("realUrl", realUrl)
        put("name", c.name)
        put("date_upload", c.date_upload)
        put("chapter_number", c.chapter_number)
        c.scanlator?.let { put("scanlator", it) }
        // memo carries per-chapter context (e.g. GraphQL ids) that getPageList needs
        if (c.memo.isNotEmpty()) put("memo", c.memo)
    }

    private fun pageJson(p: Page) = buildJsonObject {
        put("index", p.index)
        put("url", p.url)
        p.imageUrl?.let { put("imageUrl", it) }
    }

    private fun mangaFrom(o: JsonObject): SManga = SManga.create().apply {
        url = o["url"]?.jsonPrimitive?.content ?: ""
        title = o["title"]?.jsonPrimitive?.content ?: ""
        o["artist"]?.let { artist = it.jsonPrimitive.content }
        o["author"]?.let { author = it.jsonPrimitive.content }
        o["description"]?.let { description = it.jsonPrimitive.content }
        o["genre"]?.let { genre = it.jsonPrimitive.content }
        o["status"]?.let { status = it.jsonPrimitive.content.toInt() }
        o["thumbnail_url"]?.let { thumbnail_url = it.jsonPrimitive.content }
        (o["memo"] as? JsonObject)?.let { memo = it }
    }

    private fun chapterFrom(o: JsonObject): SChapter = SChapter.create().apply {
        url = o["url"]?.jsonPrimitive?.content ?: ""
        name = o["name"]?.jsonPrimitive?.content ?: ""
        o["scanlator"]?.let { scanlator = it.jsonPrimitive.content }
        (o["memo"] as? JsonObject)?.let { memo = it }
    }

    private fun pageFrom(o: JsonObject): Page {
        val idx = o["index"]?.jsonPrimitive?.content?.toInt() ?: 0
        val url = o["url"]?.jsonPrimitive?.content ?: ""
        val imageUrl = o["imageUrl"]?.jsonPrimitive?.content
        return Page(idx, url, imageUrl)
    }

    // ---- HTTP plumbing ----

    private inline fun guarded(ex: HttpExchange, body: () -> String) {
        try {
            respond(ex, 200, body())
        } catch (t: Throwable) {
            respond(ex, 500, errorJson(t))
        }
    }

    private fun readObj(ex: HttpExchange): JsonObject {
        val text = ex.requestBody.use(InputStream::readBytes).toString(Charsets.UTF_8)
        if (text.isBlank()) return JsonObject(emptyMap())
        return json.parseToJsonElement(text) as JsonObject
    }

    private fun respond(ex: HttpExchange, code: Int, body: String) {
        val bytes = body.toByteArray(Charsets.UTF_8)
        ex.responseHeaders.set("Content-Type", "application/json")
        ex.sendResponseHeaders(code, bytes.size.toLong())
        ex.responseBody.use { it.write(bytes) }
    }

    private fun errorJson(t: Throwable): String {
        val root = generateSequence(t) { it.cause }.last()
        return buildJsonObject {
            put("error", (t.message ?: t.javaClass.name))
            put("type", t.javaClass.name)
            put("rootType", root.javaClass.name)
            put("rootMessage", (root.message ?: ""))
            put("stack", root.stackTrace.take(10).joinToString("\n") { it.toString() })
        }.toString()
    }
}
