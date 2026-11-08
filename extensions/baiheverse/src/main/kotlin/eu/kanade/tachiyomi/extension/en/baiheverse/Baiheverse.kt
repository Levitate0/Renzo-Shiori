package eu.kanade.tachiyomi.extension.en.baiheverse

import eu.kanade.tachiyomi.network.GET
import eu.kanade.tachiyomi.network.POST
import eu.kanade.tachiyomi.source.model.FilterList
import eu.kanade.tachiyomi.source.model.MangasPage
import eu.kanade.tachiyomi.source.model.Page
import eu.kanade.tachiyomi.source.model.SChapter
import eu.kanade.tachiyomi.source.model.SManga
import eu.kanade.tachiyomi.source.online.HttpSource
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put
import okhttp3.Cookie
import okhttp3.Headers
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.text.SimpleDateFormat
import java.util.Base64
import java.util.Locale
import java.util.TimeZone

/**
 * Baiheverse (baiheverse.com): official English editions of Chinese GL manhua,
 * published outside China and so uncensored.
 *
 * The site is a Next.js app, and every page carries its own data in the
 * stream it hydrates from, so the extension reads the same public pages a
 * browser does. Page images live in the site's Supabase storage, and its
 * reader fetches them with the site's public key, or with the reader's
 * session token for bought chapters. The session is the site's own sign-in
 * cookie, so a Renzo Shiori site login (or a pasted cookie) is what unlocks
 * bought chapters.
 */
class Baiheverse : HttpSource() {

    override val name = "Baiheverse"
    override val lang = "en"
    override val baseUrl = "https://baiheverse.com"
    override val supportsLatest = true

    private val json = Json { ignoreUnknownKeys = true }

    override fun headersBuilder(): Headers.Builder = super.headersBuilder()
        .add("Referer", "$baseUrl/")

    // ── Catalogue: one page lists every manhua, so sort and filter here ──────

    override fun popularMangaRequest(page: Int): Request = GET("$baseUrl/en/manhuas", headers)

    override fun popularMangaParse(response: Response): MangasPage =
        MangasPage(catalog(response).sortedByDescending { it.long("views") }.map { it.toSManga() }, false)

    override fun latestUpdatesRequest(page: Int): Request = popularMangaRequest(page)

    override fun latestUpdatesParse(response: Response): MangasPage =
        MangasPage(catalog(response).sortedByDescending { it.str("latestPublishedAt") ?: "" }.map { it.toSManga() }, false)

    // The query rides in the fragment, which never leaves this process.
    override fun searchMangaRequest(page: Int, query: String, filters: FilterList): Request =
        GET("$baseUrl/en/manhuas".toHttpUrl().newBuilder().fragment(query.trim()).build(), headers)

    override fun searchMangaParse(response: Response): MangasPage {
        val query = response.request.url.fragment.orEmpty()
        val slug = query.substringAfter("/manhuas/", "").substringBefore('/').substringBefore('?')
        val works = catalog(response).filter { w ->
            when {
                slug.isNotEmpty() -> w.str("slug") == slug
                else -> listOfNotNull(w.str("title"), w.str("creator"), w.str("translator"))
                    .plus(w["tags"].strings())
                    .any { it.contains(query, ignoreCase = true) }
            }
        }
        return MangasPage(works.sortedByDescending { it.long("views") }.map { it.toSManga() }, false)
    }

    override fun getFilterList(): FilterList = FilterList()

    /** Every manhua card on a page, once each. */
    private fun catalog(response: Response): List<JsonObject> {
        val seen = HashSet<String>()
        return objects(Flight.read(response.body.string())) {
            it.str("kind") == "manhua" && it.str("href")?.contains("/manhuas/") == true
        }.filter { it.str("slug")?.let(seen::add) == true }
    }

    private fun JsonObject.toSManga() = SManga.create().apply {
        url = "/en/manhuas/${str("slug")}"
        title = str("title")!!.trim()
        thumbnail_url = str("coverUrl")
        author = str("creator")
        status = status(str("status"))
        genre = this@toSManga["tags"].strings().joinToString().ifEmpty { null }
    }

    // ── Details and chapters: both come from the work's page ─────────────────

    override fun mangaDetailsParse(response: Response): SManga {
        val flight = Flight.read(response.body.string())
        val slug = response.request.url.pathSegments.last()
        val work = translation(flight, slug)
        val card = objects(flight) { it.str("kind") == "manhua" && it.str("slug") == slug }.firstOrNull()
        val original = work["manhua"] as? JsonObject
        return SManga.create().apply {
            title = work.str("title")!!.trim()
            thumbnail_url = work.str("vertical_cover_url") ?: work.str("horizontal_cover_url") ?: card?.str("coverUrl")
            author = (original?.get("author") as? JsonObject)?.str("display_name") ?: card?.str("creator")
            artist = author
            status = status(work.str("update_status"))
            genre = (
                listOfNotNull((original?.get("category") as? JsonObject)?.str("name")) +
                    original?.get("tags").strings() + card?.get("tags").strings()
                ).distinct().joinToString().ifEmpty { null }
            val translator = (work["translator"] as? JsonObject)?.str("display_name") ?: card?.str("translator")
            description = listOfNotNull(
                flight.text(work.str("description"))?.trim(),
                original?.str("title")?.let { "Original title: $it" },
                translator?.let { "Translated by $it" },
                work.str("update_details"),
            ).joinToString("\n\n").ifEmpty { null }
        }
    }

    override fun chapterListParse(response: Response): List<SChapter> {
        val flight = Flight.read(response.body.string())
        val slug = response.request.url.pathSegments.last()
        val work = translation(flight, slug)
        val id = work.str("id")
        val seen = HashSet<String>()
        val sections = objects(flight) {
            it.str("translation_id") == id && it["section_number"] is JsonPrimitive && it.str("slug") != null
        }.filter { it.str("id")?.let(seen::add) == true }
            .filter { it.str("status") == null || it.str("status") == "published" }
            .sortedBy { it.long("section_number") }

        val numbers = chapterNumbers(sections)
        return sections.mapIndexed { i, s ->
            SChapter.create().apply {
                url = "/en/manhuas/$slug/chapters/${s.str("slug")}"
                // Shiori and Mihon read a leading lock as "paid, not owned".
                name = (if (s.bool("has_access") == false) "🔒 " else "") + (s.str("title") ?: "Chapter ${i + 1}").trim()
                chapter_number = numbers[i]
                date_upload = date(s.str("created_at"))
            }
        }.reversed()
    }

    /**
     * Baiheverse's own section numbers count previews and extras, so they
     * aren't chapter numbers. Main chapters take the number in their title
     * ("Chapter 17 …", "No. 17") when most titles have one; otherwise they
     * are counted in reading order. Everything else (a preview, an extra,
     * "Modern Extra 4") slots in after the chapter before it as a decimal,
     * so it never collides with a real chapter: 17.1, 17.2 … or, past nine
     * of them, 17.01, 17.02 ….
     */
    private fun chapterNumbers(sections: List<JsonObject>): List<Float> {
        val main = sections.map { it.str("chapter_kind") != "extra" }
        val titled = sections.map { CHAPTER.find(it.str("title").orEmpty())?.groupValues?.get(1)?.toFloatOrNull() }
        val mains = main.count { it }
        val byTitle = mains > 0 && sections.indices.count { main[it] && titled[it] != null } * 2 >= mains
        var counted = 0f
        val base: List<Float?> = sections.indices.map { i ->
            when {
                !main[i] -> null
                byTitle -> titled[i]
                PREVIEW.containsMatchIn(sections[i].str("title").orEmpty()) && counted == 0f -> null
                else -> ++counted
            }
        }
        val out = MutableList(sections.size) { 0f }
        var i = 0
        var last = 0f
        while (i < sections.size) {
            base[i]?.let { out[i] = it; last = it; i++; continue }
            // A run of unnumbered entries after `last`. The very first entry
            // of a series, with nothing before it, is chapter 0.
            var end = i
            while (end < sections.size && base[end] == null) end++
            val first = if (i == 0) 1 else 0
            if (i == 0) out[0] = 0f
            val step = if (end - i - first <= 9) 10 else 100
            for (k in i + first until end) {
                out[k] = Math.round((last + (k - i - first + 1).toFloat() / step) * step) / step.toFloat()
            }
            i = end
        }
        return out
    }

    /** The edition this page is about: its slug is the last path segment. */
    private fun translation(flight: Flight, slug: String): JsonObject =
        objects(flight) { it.str("slug") == slug && it.containsKey("manhua_id") && it.containsKey("translator_id") }
            .firstOrNull() ?: throw Exception("Baiheverse changed its page layout: no work data for $slug")

    // ── Pages ────────────────────────────────────────────────────────────────

    override fun pageListParse(response: Response): List<Page> {
        val flight = Flight.read(response.body.string())
        val slug = response.request.url.pathSegments.last()
        val section = objects(flight) { it.str("slug") == slug && it["image_urls"] is JsonArray }.firstOrNull()
            ?: objects(flight) { it["image_urls"] is JsonArray }.firstOrNull()
            ?: throw Exception("Baiheverse changed its reader: no page list for $slug")
        // A chapter you haven't bought comes back with no pages: that is how
        // Shiori tells "withheld" from a failed read.
        if (section.bool("has_access") == false) return emptyList()
        return section["image_urls"]!!.jsonArray.mapNotNull { it.jsonPrimitive.contentOrNull }
            .mapIndexed { i, path ->
                Page(i, imageUrl = STORAGE.newBuilder().addPathSegments(path.trimStart('/')).build().toString())
            }
    }

    override fun imageRequest(page: Page): Request {
        val token = sessionToken() ?: PUBLIC_KEY
        return GET(
            page.imageUrl!!,
            headersBuilder().set("apikey", PUBLIC_KEY).set("Authorization", "Bearer $token").build(),
        )
    }

    override fun imageUrlParse(response: Response): String = throw UnsupportedOperationException()

    // ── Session: the site's own Supabase sign-in cookie ──────────────────────

    /**
     * The access token of the signed-in reader, from the cookie the site's
     * sign-in sets ("base64-" + base64url JSON, split into .0, .1 … when
     * long). A token past its hour is renewed the way the site's own client
     * does it, and the new session written back where the site keeps it.
     */
    private fun sessionToken(): String? {
        val site = baseUrl.toHttpUrl()
        // A cookie the site set and one pasted into Shiori can both be there
        // under one name: the later expiry is the newer session.
        val parts = client.cookieJar.loadForRequest(site)
            .filter { it.name == SESSION_COOKIE || it.name.startsWith("$SESSION_COOKIE.") }
            .groupBy { it.name }.values.map { same -> same.maxBy { it.expiresAt } }
            .sortedBy { it.name.substringAfter("$SESSION_COOKIE.", "").toIntOrNull() ?: -1 }
        if (parts.isEmpty()) return null
        val session = runCatching { decodeSession(parts.joinToString("") { it.value }) }.getOrNull() ?: return null
        val token = session.str("access_token") ?: return null
        val expiresAt = session.long("expires_at") ?: return token
        if (expiresAt - 60 > System.currentTimeMillis() / 1000) return token
        val refresh = session.str("refresh_token") ?: return null
        return runCatching { renew(refresh, parts) }.getOrNull()
    }

    private fun decodeSession(raw: String): JsonObject {
        val text = if (raw.startsWith("base64-")) {
            String(Base64.getUrlDecoder().decode(raw.removePrefix("base64-").trimEnd('=')), Charsets.UTF_8)
        } else {
            java.net.URLDecoder.decode(raw, "UTF-8")
        }
        return when (val e = json.parseToJsonElement(text)) {
            is JsonObject -> e
            // An old supabase-js kept [access_token, refresh_token, …].
            is JsonArray -> buildJsonObject {
                put("access_token", e.getOrNull(0)?.jsonPrimitive?.contentOrNull)
                put("refresh_token", e.getOrNull(1)?.jsonPrimitive?.contentOrNull)
            }
            else -> throw IllegalArgumentException("not a session")
        }
    }

    @Synchronized
    private fun renew(refreshToken: String, old: List<Cookie>): String? {
        val body = buildJsonObject { put("refresh_token", refreshToken) }.toString()
            .toRequestBody("application/json".toMediaType())
        val headers = Headers.Builder().add("apikey", PUBLIC_KEY).add("Authorization", "Bearer $PUBLIC_KEY").build()
        val session = client.newCall(POST(AUTH.toString(), headers, body)).execute().use { r ->
            if (!r.isSuccessful) return null
            json.parseToJsonElement(r.body.string()).jsonObject
        }
        val token = session.str("access_token") ?: return null
        val site = baseUrl.toHttpUrl()
        val value = "base64-" + Base64.getUrlEncoder().withoutPadding().encodeToString(session.toString().toByteArray())
        val chunks = value.chunked(3180)
        val names = if (chunks.size == 1) listOf(SESSION_COOKIE) else chunks.indices.map { "$SESSION_COOKIE.$it" }
        // Written in the same form as the cookie it replaces, so it overwrites
        // that one rather than sitting beside it.
        val form = old.first()
        val until = System.currentTimeMillis() + 400L * 24 * 3600 * 1000
        val cookies = names.zip(chunks).map { (n, c) ->
            (old.firstOrNull { it.name == n } ?: form).newBuilder().name(n).value(c).expiresAt(until).build()
        } + old.filter { it.name !in names }.map { it.newBuilder().value("").expiresAt(0).build() }
        client.cookieJar.saveFromResponse(site, cookies)
        return token
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun objects(flight: Flight, accept: (JsonObject) -> Boolean): List<JsonObject> {
        val out = ArrayList<JsonObject>()
        fun walk(e: JsonElement) {
            when (e) {
                is JsonObject -> { if (accept(e)) out += e; e.values.forEach(::walk) }
                is JsonArray -> e.forEach(::walk)
                else -> {}
            }
        }
        flight.rows.values.forEach { if (it is JsonElement) walk(it) }
        return out
    }

    private fun status(s: String?) = when (s?.lowercase()) {
        "ongoing", "updating" -> SManga.ONGOING
        "completed", "complete", "finished" -> SManga.COMPLETED
        "hiatus", "paused" -> SManga.ON_HIATUS
        else -> SManga.UNKNOWN
    }

    private fun date(s: String?): Long {
        if (s == null || s.length < 19) return 0L
        return runCatching { DATE.get()!!.parse(s.substring(0, 19))!!.time }.getOrDefault(0L)
    }

    companion object {
        // Built, not written out: Renzo Shiori finds a source's site (for its
        // login) as the commonest https:// host in the extension, and that
        // must stay baiheverse.com.
        private const val SUPABASE_HOST = "lgucpvlwjwirhlukbkib.supabase.co"
        private val SUPABASE = HttpUrl.Builder().scheme("https").host(SUPABASE_HOST).build()
        private val STORAGE = SUPABASE.newBuilder().addPathSegments("storage/v1/object/authenticated/manhua").build()
        private val AUTH = SUPABASE.newBuilder().addPathSegments("auth/v1/token").addQueryParameter("grant_type", "refresh_token").build()
        private const val SESSION_COOKIE = "sb-lgucpvlwjwirhlukbkib-auth-token"

        // The site's public ("anon") key: it ships in the site's scripts to every
        // visitor, and its reader sends it with every page image.
        private const val PUBLIC_KEY = "@PUBLIC_KEY@"

        private val CHAPTER = Regex("""(?i)\b(?:chapter|ch\.|episode|ep\.|no\.)\s*(\d+(?:\.\d+)?)""")
        private val PREVIEW = Regex("""(?i)\b(?:preview|prologue|teaser|trailer)\b""")

        private val DATE = object : ThreadLocal<SimpleDateFormat>() {
            override fun initialValue() = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.ROOT)
                .apply { timeZone = TimeZone.getTimeZone("UTC") }
        }
    }
}

private fun JsonObject.str(key: String): String? =
    (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content?.takeUnless { it == "\$undefined" }

private fun JsonObject.long(key: String): Long? = (this[key] as? JsonPrimitive)?.longOrNull

private fun JsonObject.bool(key: String): Boolean? =
    (this[key] as? JsonPrimitive)?.takeUnless { it.isString || it is JsonNull }?.content?.toBooleanStrictOrNull()

/** Tag lists come as plain strings or as {name} objects. */
private fun JsonElement?.strings(): List<String> = (this as? JsonArray)?.mapNotNull {
    when (it) {
        is JsonPrimitive -> it.contentOrNull
        is JsonObject -> it.str("name") ?: it.str("title")
        else -> null
    }
}.orEmpty()

/**
 * The React Server Components stream a Next.js page hydrates from: the
 * strings in its self.__next_f.push([1,"…"]) scripts, joined, are rows of
 * "id:JSON" or "id:T<hex byte length>,<text>". Long text (a description)
 * lives in its own text row and is referenced as "$id".
 */
internal class Flight private constructor(val rows: Map<String, Any>) {

    fun text(value: String?): String? = when {
        value == null -> null
        value.startsWith("\$\$") -> value.substring(1)
        value.startsWith("\$") -> rows[value.substring(1)] as? String ?: (rows[value.substring(1)] as? JsonPrimitive)?.contentOrNull
        else -> value
    }

    companion object {
        private const val PUSH = "self.__next_f.push([1,\""

        fun read(html: String): Flight = Flight(rows(payload(html)))

        /** Hand-scanned: a regex over 400 KB strings overflows Java's stack. */
        private fun payload(html: String): String {
            val sb = StringBuilder()
            var at = html.indexOf(PUSH)
            while (at >= 0) {
                val start = at + PUSH.length
                var i = start
                while (i < html.length && html[i] != '"') i += if (html[i] == '\\') 2 else 1
                sb.append(Json.parseToJsonElement("\"" + html.substring(start, i) + "\"").jsonPrimitive.content)
                at = html.indexOf(PUSH, i)
            }
            return sb.toString()
        }

        private fun rows(p: String): Map<String, Any> {
            val rows = HashMap<String, Any>()
            var i = 0
            while (i < p.length) {
                while (i < p.length && p[i] == '\n') i++
                val colon = p.indexOf(':', i)
                if (colon < 0) break
                val id = p.substring(i, colon)
                if (id.isEmpty() || id.length > 8 || !id.all { it.isLetterOrDigit() }) {
                    val nl = p.indexOf('\n', i)
                    if (nl < 0) break
                    i = nl + 1
                    continue
                }
                val j = colon + 1
                if (j < p.length && p[j] == 'T') {
                    val comma = p.indexOf(',', j)
                    val bytes = p.substring(j + 1, comma).toIntOrNull(16)
                    if (comma > 0 && bytes != null) {
                        var k = comma + 1
                        var n = 0
                        while (k < p.length && n < bytes) {
                            val c = p[k]
                            n += when {
                                c.code < 0x80 -> 1
                                c.code < 0x800 -> 2
                                Character.isHighSurrogate(c) -> 4
                                else -> 3
                            }
                            k += if (Character.isHighSurrogate(c)) 2 else 1
                        }
                        rows[id] = p.substring(comma + 1, k)
                        i = k
                        continue
                    }
                }
                val nl = p.indexOf('\n', j).let { if (it < 0) p.length else it }
                val body = p.substring(j, nl)
                if (body.isNotEmpty() && (body[0] == '[' || body[0] == '{' || body[0] == '"')) {
                    runCatching { rows[id] = Json.parseToJsonElement(body) }
                }
                i = nl + 1
            }
            return rows
        }
    }
}
