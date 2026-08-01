package app.renzoshiori.client.ui.reader

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.RenzoStore
import app.renzoshiori.client.data.model.ReaderChapterDto
import app.renzoshiori.client.data.model.ReaderMarkRequestDto
import app.renzoshiori.client.data.model.ReaderProgressRequestDto
import app.renzoshiori.client.data.network.encodeFilename
import app.renzoshiori.client.data.network.pageUrl
import app.renzoshiori.client.data.network.streamPageUrl
import app.renzoshiori.client.ui.series.chapterKey
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

/** How this chapter's pages are sourced. */
enum class PageSource { OFFLINE, SERVER_FILE, STREAM }

data class ReaderUiState(
    val loading: Boolean = true,
    val error: String? = null,
    val chapterNumber: Double = 0.0,
    val chapterName: String = "",
    /** Page models Coil can load: absolute URLs (server) or raw bytes (offline). */
    val pages: List<Any> = emptyList(),
    val webtoon: Boolean = false,
    val source: PageSource = PageSource.SERVER_FILE,
    val resumePage: Int = 0,
    val hasNext: Boolean = false,
    val hasPrev: Boolean = false,
    val completed: Boolean = false,
)

/**
 * Loads a chapter's pages from the best available source, tracks the current
 * page, and reports progress/completion to the server (rate-limited, mirrors
 * the web reader's reporting contract). Works fully offline when the chapter
 * is saved on-device — progress then syncs next time the server is reachable
 * (server-side read-state remains the source of truth).
 */
class ReaderViewModel(
    application: Application,
    private val seriesId: String,
    initialChapter: Double,
) : AndroidViewModel(application) {
    private val app = application as RenzoApp
    private val store = RenzoStore(application)

    private val _state = MutableStateFlow(ReaderUiState(chapterNumber = initialChapter))
    val state: StateFlow<ReaderUiState> = _state.asStateFlow()

    private var chapters: List<ReaderChapterDto> = emptyList()
    private var lastReportedPage = -1
    private var lastReportAt = 0L

    init {
        openChapter(initialChapter)
    }

    fun openChapter(number: Double) {
        _state.value = ReaderUiState(loading = true, chapterNumber = number)
        lastReportedPage = -1
        viewModelScope.launch {
            // Chapter list gives filename/pageCount/read-state + prev/next.
            if (chapters.isEmpty()) {
                val api = app.network.currentApi()
                chapters = runCatching { api?.readerChapters(seriesId)?.chapters }.getOrNull() ?: emptyList()
            }
            val sorted = chapters.sortedBy { it.number }
            val idx = sorted.indexOfFirst { it.number == number }
            val chapter = sorted.getOrNull(idx)
            val hasPrev = idx > 0
            val hasNext = idx >= 0 && idx < sorted.size - 1

            val key = chapterKey(seriesId, number)
            val offline = withContext(Dispatchers.IO) { loadOfflinePages(key) }
            if (offline != null) {
                _state.value = ReaderUiState(
                    loading = false,
                    chapterNumber = number,
                    chapterName = chapter?.name ?: "",
                    pages = offline,
                    webtoon = true, // offline pages have no dims metadata; long-strip default reads fine either way
                    source = PageSource.OFFLINE,
                    resumePage = resumeFrom(chapter),
                    hasPrev = hasPrev,
                    hasNext = hasNext,
                    completed = chapter?.isCompleted ?: false,
                )
                return@launch
            }

            val api = app.network.currentApi()
            val base = app.tokenStore.serverUrl
            if (api == null || base == null) {
                _state.value = _state.value.copy(loading = false, error = "This chapter isn't saved offline and the server is unreachable.")
                return@launch
            }

            if (chapter?.filename != null) {
                // Downloaded on the server → /api/reader/page per index.
                val info = runCatching { api.chapterInfo(seriesId, encodeFilename(chapter.filename)) }.getOrNull()
                if (info == null || info.pageCount == 0) {
                    _state.value = _state.value.copy(loading = false, error = "Couldn't load chapter pages.")
                    return@launch
                }
                _state.value = ReaderUiState(
                    loading = false,
                    chapterNumber = number,
                    chapterName = chapter.name,
                    pages = (0 until info.pageCount).map { pageUrl(base, seriesId, chapter.filename, it) },
                    webtoon = info.suggestedMode == "webtoon" || info.suggestedMode == "longstrip",
                    source = PageSource.SERVER_FILE,
                    resumePage = resumeFrom(chapter),
                    hasPrev = hasPrev,
                    hasNext = hasNext,
                    completed = chapter.isCompleted,
                )
            } else {
                // Not downloaded anywhere → stream live from the source.
                val stream = runCatching { api.streamPages(seriesId, number) }.getOrNull()
                if (stream == null || stream.pageCount == 0) {
                    _state.value = _state.value.copy(
                        loading = false,
                        error = if (stream?.locked == true) "This chapter is locked on the source site." else "Couldn't load chapter pages.",
                    )
                    return@launch
                }
                _state.value = ReaderUiState(
                    loading = false,
                    chapterNumber = number,
                    chapterName = chapter?.name ?: "",
                    pages = (0 until stream.pageCount).map { streamPageUrl(base, seriesId, number, it) },
                    webtoon = true, // no dims for streamed pages; continuous is the safer default
                    source = PageSource.STREAM,
                    resumePage = resumeFrom(chapter),
                    hasPrev = hasPrev,
                    hasNext = hasNext,
                    completed = chapter?.isCompleted ?: false,
                )
            }
        }
    }

    private fun resumeFrom(chapter: ReaderChapterDto?): Int {
        val ch = chapter ?: return 0
        if (ch.isCompleted || ch.progress <= 0f || ch.progress >= 1f) return 0
        val total = ch.pageCount ?: return 0
        return (ch.progress * total).toInt().coerceIn(0, total - 1)
    }

    private fun loadOfflinePages(key: String): List<Any>? {
        if (!store.hasChapter(key)) return null
        val entry: JSONObject = store.getManifest().optJSONObject("chapters")?.optJSONObject(key) ?: return null
        val paths = entry.optJSONArray("pagePaths") ?: return null
        val out = ArrayList<Any>(paths.length())
        for (i in 0 until paths.length()) {
            out.add(store.readFile(paths.getString(i)) ?: return null)
        }
        return out
    }

    /** Called by the screen as the visible page changes (0-based). */
    fun onPageViewed(page0: Int) {
        val s = _state.value
        val total = s.pages.size
        if (total == 0) return
        val page1 = page0 + 1
        val atEnd = page1 >= total
        val now = System.currentTimeMillis()
        // Rate-limit like the web reader (~800ms), but always report the end.
        if (page1 == lastReportedPage) return
        if (!atEnd && now - lastReportAt < 800) return
        lastReportedPage = page1
        lastReportAt = now

        if (atEnd && !s.completed) {
            _state.value = s.copy(completed = true)
            // Update the cached chapter list so prev/next state stays honest.
            chapters = chapters.map { if (it.number == s.chapterNumber) it.copy(isCompleted = true, progress = 1f) else it }
        }

        viewModelScope.launch {
            val api = app.network.currentApi() ?: return@launch
            runCatching {
                api.setProgress(
                    ReaderProgressRequestDto(
                        seriesId = seriesId,
                        chapterNumber = s.chapterNumber,
                        lastReadPage = page1,
                        totalPages = total,
                    ),
                )
            }
            // Belt & braces on completion, mirroring web autoMarkRead.
            if (atEnd) {
                runCatching { api.markChapters(ReaderMarkRequestDto(seriesId, listOf(s.chapterNumber), true)) }
            }
        }
    }

    fun nextChapter() {
        val sorted = chapters.sortedBy { it.number }
        val idx = sorted.indexOfFirst { it.number == _state.value.chapterNumber }
        sorted.getOrNull(idx + 1)?.let { openChapter(it.number) }
    }

    fun prevChapter() {
        val sorted = chapters.sortedBy { it.number }
        val idx = sorted.indexOfFirst { it.number == _state.value.chapterNumber }
        if (idx > 0) sorted.getOrNull(idx - 1)?.let { openChapter(it.number) }
    }

    fun toggleMode() {
        _state.value = _state.value.copy(webtoon = !_state.value.webtoon)
    }

    companion object {
        fun factory(application: Application, seriesId: String, chapter: Double) =
            object : ViewModelProvider.Factory {
                @Suppress("UNCHECKED_CAST")
                override fun <T : ViewModel> create(modelClass: Class<T>): T =
                    ReaderViewModel(application, seriesId, chapter) as T
            }
    }
}
