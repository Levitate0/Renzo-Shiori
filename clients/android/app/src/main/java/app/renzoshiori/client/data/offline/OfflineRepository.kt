package app.renzoshiori.client.data.offline

import android.content.Context
import app.renzoshiori.client.RenzoStore
import org.json.JSONObject

/**
 * Native read-model over the same offline manifest RenzoStore has always kept
 * (kv key `renzo.offline.manifest.v1`, format v2 — series map + chapters map).
 *
 * Deliberate plan deviation: Room was originally slated here, but Room needs
 * KSP, and KSP for Kotlin 2.4 requires a newer AGP than the release machine's
 * Gradle 8.7 can host. Keeping the proven SharedPreferences-JSON manifest
 * means the toolchain stays put AND existing installs' downloads carry over
 * byte-for-byte with no migration step at all — the old WebView app, the
 * bundled offline reader, and this native UI all read the same manifest.
 */
class OfflineRepository(context: Context) {
    private val store = RenzoStore(context)

    data class OfflineSeries(
        val seriesId: String,
        val title: String,
        val coverPath: String?,
        val chapterCount: Int,
        val bytes: Long,
    )

    data class OfflineChapter(
        val seriesId: String,
        val chapterKey: String,
        val chapterNumber: Double,
        val seriesTitle: String,
        val pageCount: Int,
        val pagePaths: List<String>,
        val bytes: Long,
        val savedAt: Long,
    )

    fun listSeries(): List<OfflineSeries> {
        val m = store.getManifest()
        val chapters = m.optJSONObject("chapters") ?: JSONObject()
        val series = m.optJSONObject("series") ?: JSONObject()

        val counts = HashMap<String, Pair<Int, Long>>()
        for (key in chapters.keys()) {
            val c = chapters.optJSONObject(key) ?: continue
            val sid = c.optString("seriesId")
            val (n, b) = counts[sid] ?: (0 to 0L)
            counts[sid] = (n + 1) to (b + c.optLong("bytes"))
        }

        val out = ArrayList<OfflineSeries>()
        for (sid in series.keys()) {
            val s = series.optJSONObject(sid) ?: continue
            val agg = counts[sid] ?: continue // no chapters saved → don't list
            out.add(
                OfflineSeries(
                    seriesId = sid,
                    title = s.optString("title", sid),
                    coverPath = s.optString("coverPath").takeIf { it.isNotEmpty() },
                    chapterCount = agg.first,
                    bytes = agg.second,
                ),
            )
        }
        return out.sortedBy { it.title.lowercase() }
    }

    fun listChapters(seriesId: String? = null): List<OfflineChapter> {
        val chapters = store.getManifest().optJSONObject("chapters") ?: JSONObject()
        val out = ArrayList<OfflineChapter>()
        for (key in chapters.keys()) {
            val c = chapters.optJSONObject(key) ?: continue
            if (seriesId != null && c.optString("seriesId") != seriesId) continue
            val paths = c.optJSONArray("pagePaths")
            out.add(
                OfflineChapter(
                    seriesId = c.optString("seriesId"),
                    chapterKey = c.optString("chapterKey", key),
                    chapterNumber = c.optDouble("chapterNumber", 0.0),
                    seriesTitle = c.optString("seriesTitle"),
                    pageCount = c.optInt("pageCount"),
                    pagePaths = buildList { for (i in 0 until (paths?.length() ?: 0)) add(paths!!.getString(i)) },
                    bytes = c.optLong("bytes"),
                    savedAt = c.optLong("savedAt"),
                ),
            )
        }
        return out.sortedBy { it.chapterNumber }
    }

    fun isChapterOffline(chapterKey: String): Boolean = store.hasChapter(chapterKey)

    /** Raw page bytes for a saved page (Coil can display via ByteArray). */
    fun readPage(relPath: String): ByteArray? = store.readFile(relPath)

    fun deleteChapter(chapterKey: String) {
        val m = store.getManifest()
        val chapters = m.optJSONObject("chapters") ?: return
        val entry = chapters.optJSONObject(chapterKey)
        chapters.remove(chapterKey)
        // Delete page files best-effort.
        val paths = entry?.optJSONArray("pagePaths")
        if (paths != null) for (i in 0 until paths.length()) runCatching { store.deletePath(paths.getString(i)) }
        // Prune orphaned series entries (and their covers).
        val series = m.optJSONObject("series")
        if (series != null && entry != null) {
            val sid = entry.optString("seriesId")
            val stillHas = chapters.keys().asSequence().any { chapters.optJSONObject(it)?.optString("seriesId") == sid }
            if (!stillHas) {
                series.optJSONObject(sid)?.optString("coverPath")?.takeIf { it.isNotEmpty() }
                    ?.let { runCatching { store.deletePath(it) } }
                series.remove(sid)
            }
        }
        store.setManifest(m)
    }
}
