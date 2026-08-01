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
import app.renzoshiori.client.data.network.ReaderApi
import app.renzoshiori.client.data.network.ReaderBookmarkRequestDto
import app.renzoshiori.client.data.network.encodeFilename
import app.renzoshiori.client.data.network.pageUrl
import app.renzoshiori.client.data.network.streamPageUrl
import app.renzoshiori.client.ui.series.chapterKey
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import kotlin.math.floor

/** How this chapter's pages are sourced. */
enum class PageSource { OFFLINE, SERVER_FILE, STREAM }

/**
 * One chapter laid out in the reader. The first segment is the chapter the
 * reader opened on; later ones are appended as the reader scrolls past a
 * chapter boundary (the web reader's infinite scroll).
 */
data class ReaderSegment(
    val key: String,
    val chapterNumber: Double,
    val name: String,
    val pageCount: Int,
    /** Page models Coil can load: absolute URLs (server) or raw bytes (offline). */
    val pages: List<Any> = emptyList(),
    /** Per-page (width, height) when the server measured them; null = unknown. */
    val dims: List<Pair<Int, Int>?> = emptyList(),
    val streaming: Boolean = false,
    val filename: String? = null,
)

data class ReaderUiState(
    val loading: Boolean = true,
    val error: String? = null,
    val seriesTitle: String = "",
    val seriesType: String = "",
    val chapters: List<ReaderChapterDto> = emptyList(),
    /** The chapter the reader opened on (segments[0]). */
    val chapterNumber: Double = 0.0,
    val segments: List<ReaderSegment> = emptyList(),
    val activeSegIndex: Int = 0,
    /** 0-based, within the active segment. */
    val currentPage: Int = 0,
    val resumePage: Int = 0,
    /** Server-measured layout verdict for the opening chapter ("webtoon"/"longstrip"/"paged"). */
    val serverSuggestedMode: String? = null,
    /** Client-side verdict from decoded image sizes (streamed / offline pages). */
    val detectedMode: String? = null,
    val source: PageSource = PageSource.SERVER_FILE,
    val locked: Boolean = false,
    val lockedUrl: String? = null,
    val unlockChecking: Boolean = false,
    val appending: Boolean = false,
    /** Why the next chapter couldn't be appended — surfaced with a Retry. */
    val appendError: String? = null,
    val openingLabel: String? = null,
    val arrivedLabel: String? = null,
    val cacheBusy: Boolean = false,
    val toast: String? = null,
    val settings: ReaderSettings = ReaderSettings(),
    val seriesModeOverride: ReaderMode? = null,
) {
    val activeSegment: ReaderSegment? get() = segments.getOrNull(activeSegIndex)
    val activeChapterNumber: Double get() = activeSegment?.chapterNumber ?: chapterNumber
    val activeChapter: ReaderChapterDto? get() = chapters.firstOrNull { it.number == activeChapterNumber }
    val activePageCount: Int get() = activeSegment?.pageCount ?: 0
    val activeLabel: String get() = activeSegment?.let { it.name.ifBlank { "Chapter ${trimNumber(it.chapterNumber)}" } } ?: ""
    val streaming: Boolean get() = source == PageSource.STREAM

    /** Every chapter with a number, in reading order — all of them are readable. */
    val readable: List<ReaderChapterDto> get() = chapters.sortedBy { it.number }

    fun neighbor(direction: Int): ReaderChapterDto? {
        val list = readable
        val idx = list.indexOfFirst { it.number == activeChapterNumber }
        return if (idx < 0) null else list.getOrNull(idx + direction)
    }

    val hasNext: Boolean get() = neighbor(1) != null
    val hasPrev: Boolean get() = neighbor(-1) != null

    fun nameOf(chapter: ReaderChapterDto): String =
        chapter.name.ifBlank { "Chapter ${trimNumber(chapter.number)}" }

    /** Name of the chapter after the one on screen ("· next: …" in the top bar). */
    val nextChapterName: String? get() = neighbor(1)?.let { nameOf(it) }

    /**
     * The web reader's mode resolution, verbatim: an explicit choice wins; then
     * the server's measurement of the actual archive pages; then the client-side
     * measurement of decoded images; then the series type label as a last resort.
     */
    fun resolvedMode(): ResolvedMode {
        val chosen = seriesModeOverride ?: settings.mode
        if (chosen != ReaderMode.AUTO) return ResolvedMode.of(chosen)

        val type = seriesType.lowercase()
        val typedPaged = if (type.contains("manga") && !type.contains("manhwa") && !type.contains("manhua"))
            ResolvedMode.PAGED_RTL else ResolvedMode.PAGED

        if (serverSuggestedMode != null) {
            if (serverSuggestedMode == "webtoon") return ResolvedMode.WEBTOON
            if (serverSuggestedMode == "longstrip") return ResolvedMode.LONGSTRIP
            return typedPaged
        }
        if (detectedMode == "webtoon") return ResolvedMode.WEBTOON
        if (detectedMode == "longstrip") return ResolvedMode.LONGSTRIP
        if (detectedMode == "paged") return typedPaged
        // Nothing measured yet: a streamed manhwa/manhua/webtoon starts scrolling
        // immediately (a later measurement can still correct it).
        if (streaming && (type.contains("manhwa") || type.contains("manhua") || type.contains("webtoon")))
            return ResolvedMode.WEBTOON
        return ResolvedMode.PAGED
    }
}

/** "12.0" → "12", "12.5" → "12.5" (matches the web's chapter labels). */
fun trimNumber(n: Double): String =
    if (n == floor(n) && !n.isInfinite()) n.toLong().toString() else n.toString()

/**
 * Loads a chapter's pages from the best available source (offline bytes →
 * downloaded server file → live stream), tracks the current page, reports
 * progress/completion, and owns the reader's persisted settings. Works fully
 * offline when the chapter is saved on-device — progress then syncs next time
 * the server is reachable (server-side read-state remains the source of truth).
 */
class ReaderViewModel(
    application: Application,
    private val seriesId: String,
    initialChapter: Double,
) : AndroidViewModel(application) {
    private val app = application as RenzoApp
    private val store = RenzoStore(application)
    private val prefs = ReaderPrefs(application)

    private val _state = MutableStateFlow(
        ReaderUiState(
            chapterNumber = initialChapter,
            settings = prefs.load(),
            seriesModeOverride = prefs.seriesMode(seriesId),
        ),
    )
    val state: StateFlow<ReaderUiState> = _state.asStateFlow()

    private var lastReportedKey = ""
    private var lastReportedPage = -1
    private var lastReportAt = 0L
    /** Progress reporting is armed a moment after load so layout/resume churn isn't written as reading. */
    private var progressArmedAt = 0L
    private var appendStopped = false
    private var appending = false
    /** True once anything was streamed live — only then is the server cache worth clearing on exit. */
    private var usedStream = false
    private val loadedDims = ArrayList<Pair<Int, Int>>()

    init {
        openChapter(initialChapter)
    }

    // ── Loading ───────────────────────────────────────────────────────────

    fun openChapter(number: Double, openingLabel: String? = null) {
        _state.update {
            it.copy(
                loading = true,
                error = null,
                chapterNumber = number,
                segments = emptyList(),
                activeSegIndex = 0,
                currentPage = 0,
                resumePage = 0,
                serverSuggestedMode = null,
                detectedMode = null,
                locked = false,
                lockedUrl = null,
                openingLabel = openingLabel,
                arrivedLabel = null,
            )
        }
        lastReportedKey = ""
        lastReportedPage = -1
        appendStopped = false
        loadedDims.clear()
        progressArmedAt = System.currentTimeMillis() + 1200

        viewModelScope.launch {
            if (_state.value.chapters.isEmpty()) {
                val api = app.network.currentApi()
                val list = runCatching { api?.readerChapters(seriesId) }.getOrNull()
                if (list != null) {
                    _state.update {
                        it.copy(
                            chapters = list.chapters,
                            seriesTitle = list.title,
                            seriesType = list.type ?: "",
                        )
                    }
                }
            }
            val known = _state.value.chapters.firstOrNull { it.number == number }
            if (known == null && _state.value.chapters.isNotEmpty()) {
                _state.update { it.copy(loading = false, error = "Chapter not found.") }
                return@launch
            }
            // No server metadata (offline, server unreachable): a bare chapter is
            // enough for buildSegment to find on-device pages.
            val chapter = known ?: ReaderChapterDto(number = number)

            when (val result = buildSegment(chapter)) {
                is SegResult.Ok -> {
                    if (result.source == PageSource.STREAM) usedStream = true
                    _state.update {
                        it.copy(
                            loading = false,
                            segments = listOf(result.segment),
                            activeSegIndex = 0,
                            source = result.source,
                            serverSuggestedMode = result.suggestedMode,
                            resumePage = resumeFrom(chapter, result.segment.pageCount),
                            currentPage = resumeFrom(chapter, result.segment.pageCount),
                            arrivedLabel = it.openingLabel,
                            openingLabel = null,
                        )
                    }
                    if (_state.value.arrivedLabel != null) {
                        launch {
                            delay(2000)
                            _state.update { it.copy(arrivedLabel = null) }
                        }
                    }
                }
                is SegResult.Locked -> {
                    _state.update {
                        it.copy(loading = false, locked = true, lockedUrl = result.url, openingLabel = null)
                    }
                    startUnlockWatch(number)
                }
                is SegResult.Failed ->
                    _state.update { it.copy(loading = false, error = result.message, openingLabel = null) }
            }
        }
    }

    private sealed interface SegResult {
        data class Ok(val segment: ReaderSegment, val suggestedMode: String?, val source: PageSource) : SegResult
        data class Locked(val url: String?) : SegResult
        data class Failed(val message: String) : SegResult
    }

    /** Resolve a chapter's pages: saved-on-device → downloaded on the server → live stream. */
    private suspend fun buildSegment(chapter: ReaderChapterDto): SegResult {
        val number = chapter.number
        val name = chapter.name.ifBlank { "Chapter ${trimNumber(number)}" }

        val offline = withContext(Dispatchers.IO) { loadOfflinePages(chapterKey(seriesId, number)) }
        if (offline != null) {
            return SegResult.Ok(
                ReaderSegment(
                    key = "ch:$number",
                    chapterNumber = number,
                    name = name,
                    pageCount = offline.size,
                    pages = offline,
                    dims = List(offline.size) { null },
                ),
                suggestedMode = null, // no dims metadata — smart-detect measures the decoded images
                source = PageSource.OFFLINE,
            )
        }

        val api = app.network.currentApi()
        val base = app.tokenStore.serverUrl
        if (api == null || base == null)
            return SegResult.Failed("This chapter isn't saved offline and the server is unreachable.")

        if (chapter.locked) return SegResult.Locked(chapter.url)

        val filename = chapter.filename
        if (filename != null) {
            val attempt = runCatching { api.chapterInfo(seriesId, encodeFilename(filename)) }
            val info = attempt.getOrNull()
            if (info == null) return SegResult.Failed(describeFailure(attempt.exceptionOrNull()))
            if (info.pageCount == 0) return SegResult.Failed("The server reported no pages for this chapter.")
            val dims = (0 until info.pageCount).map { i ->
                info.pages.firstOrNull { it.index == i }?.let { p ->
                    val w = p.width ?: 0
                    val h = p.height ?: 0
                    if (w > 0 && h > 0) w to h else null
                }
            }
            return SegResult.Ok(
                ReaderSegment(
                    key = "ch:$number",
                    chapterNumber = number,
                    name = name,
                    pageCount = info.pageCount,
                    pages = (0 until info.pageCount).map { pageUrl(base, seriesId, filename, it) },
                    dims = dims,
                    filename = filename,
                ),
                suggestedMode = info.suggestedMode,
                source = PageSource.SERVER_FILE,
            )
        }

        // Not downloaded anywhere → stream it live from the source. This is
        // the slow path: the server fetches the page list from the source site
        // (sometimes through a Cloudflare solver), so it can take a while.
        val attempt = runCatching { api.streamPages(seriesId, number) }
        val stream = attempt.getOrNull()
        if (stream == null) return SegResult.Failed(describeFailure(attempt.exceptionOrNull()))
        if (stream.locked || stream.pageCount <= 0) return SegResult.Locked(chapter.url)
        return SegResult.Ok(
            ReaderSegment(
                key = "ch:$number",
                chapterNumber = number,
                name = name,
                pageCount = stream.pageCount,
                pages = (0 until stream.pageCount).map { streamPageUrl(base, seriesId, number, it) },
                dims = List(stream.pageCount) { null },
                streaming = true,
            ),
            suggestedMode = null,
            source = PageSource.STREAM,
        )
    }

    private fun resumeFrom(chapter: ReaderChapterDto?, pageCount: Int): Int {
        val ch = chapter ?: return 0
        if (ch.isCompleted || ch.progress <= 0f || ch.progress >= 1f || pageCount <= 0) return 0
        return (ch.progress * pageCount).toInt().coerceIn(0, pageCount - 1)
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

    // ── Infinite scroll ───────────────────────────────────────────────────

    /**
     * Append the chapter after the last-loaded segment. Called by the strip as
     * the bottom approaches; no-ops unless infinite scroll is on and there is a
     * further, unlocked chapter to pull.
     */
    fun maybeAppendNext() {
        val s = _state.value
        if (!s.settings.infiniteScroll || appending || appendStopped || s.loading) return
        // Never queue a chapter while the reader is still inside an earlier
        // one. Without this, one append makes room for the next trigger and
        // the reader chain-loads (and marks read) chapter after chapter that
        // nobody has looked at. The probe line moves activeSegIndex onto the
        // newest segment only once it is genuinely on screen.
        if (s.activeSegIndex < s.segments.lastIndex) return
        val last = s.segments.lastOrNull() ?: return
        val list = s.readable
        val idx = list.indexOfFirst { it.number == last.chapterNumber }
        val next = if (idx < 0) null else list.getOrNull(idx + 1)
        if (next == null || next.locked) { appendStopped = true; return }

        appending = true
        _state.update { it.copy(appending = true) }
        viewModelScope.launch {
            when (val result = buildSegment(next)) {
                is SegResult.Ok -> {
                    if (result.source == PageSource.STREAM) usedStream = true
                    if (result.segment.pageCount > 0) {
                        _state.update { it.copy(segments = it.segments + result.segment, appendError = null) }
                    } else {
                        appendStopped = true
                    }
                }
                // A failed append must not silently dead-end the reader: say
                // what went wrong and let the reader try again, since the
                // usual cause is a slow source that succeeds on a second go.
                is SegResult.Failed -> {
                    appendStopped = true
                    _state.update { it.copy(appendError = result.message) }
                }
                is SegResult.Locked -> appendStopped = true
            }
            appending = false
            _state.update { it.copy(appending = false) }
        }
    }

    /** "Try again" from the end-of-chapter block after a failed append. */
    fun retryAppend() {
        appendStopped = false
        _state.update { it.copy(appendError = null) }
        maybeAppendNext()
    }

    /** Turn a network failure into something worth reading on screen. */
    private fun describeFailure(cause: Throwable?): String = when (cause) {
        is java.net.SocketTimeoutException ->
            "The source is taking too long to answer."
        is java.io.IOException ->
            "Can't reach the server."
        is retrofit2.HttpException ->
            "The server returned HTTP ${cause.code()}."
        else -> cause?.message?.take(120) ?: "Couldn't load chapter pages."
    }

    // ── Position + progress ───────────────────────────────────────────────

    /**
     * The strip / pager reports where the reader is. Progress is written against
     * whichever segment is on screen — with infinite scroll that may be several
     * chapters past the one the reader opened on.
     */
    fun onPosition(segIndex: Int, page0: Int) {
        val s = _state.value
        val seg = s.segments.getOrNull(segIndex) ?: return
        if (s.activeSegIndex != segIndex || s.currentPage != page0)
            _state.update { it.copy(activeSegIndex = segIndex, currentPage = page0) }
        reportProgress(seg, page0)
    }

    private fun reportProgress(seg: ReaderSegment, page0: Int) {
        val total = seg.pageCount
        if (total == 0) return
        if (System.currentTimeMillis() < progressArmedAt) return

        val page1 = (page0 + 1).coerceAtMost(total)
        val key = seg.chapterNumber.toString()
        if (key == lastReportedKey && page1 == lastReportedPage) return

        val now = System.currentTimeMillis()
        val atEnd = page1 >= total
        // Rate-limit writes within a chapter so scrolling doesn't spam; the end
        // is always reported so completion is never missed.
        if (key == lastReportedKey && !atEnd && now - lastReportAt < 800) return
        lastReportedKey = key
        lastReportedPage = page1
        lastReportAt = now

        val markRead = atEnd && _state.value.settings.autoMarkRead
        val progress = (page1.toFloat() / total).coerceAtMost(1f)
        _state.update { st ->
            st.copy(
                chapters = st.chapters.map { c ->
                    if (c.number != seg.chapterNumber) c
                    else c.copy(
                        progress = if (c.isCompleted) c.progress else progress,
                        isCompleted = c.isCompleted || markRead,
                    )
                },
            )
        }

        viewModelScope.launch {
            val api = app.network.currentApi() ?: return@launch
            runCatching {
                api.setProgress(
                    ReaderProgressRequestDto(
                        seriesId = seriesId,
                        chapterNumber = seg.chapterNumber,
                        lastReadPage = page1,
                        totalPages = total,
                        filename = seg.filename,
                    ),
                )
            }
            if (markRead) {
                runCatching { api.markChapters(ReaderMarkRequestDto(seriesId, listOf(seg.chapterNumber), true)) }
            }
        }
    }

    /**
     * Smart detect for pages with no server-measured dims (streamed or saved
     * offline): mirrors the server-side rule — tall panels are native webtoon
     * artwork, short wide slivers are off-cuts from slicing a long strip.
     */
    fun onImageLoaded(width: Int, height: Int) {
        val s = _state.value
        if (width <= 0 || height <= 0) return
        if (s.serverSuggestedMode != null) return
        if (s.detectedMode == "webtoon" || s.detectedMode == "longstrip") return
        loadedDims.add(width to height)
        val pageCount = s.activeSegment?.pageCount ?: return
        if (loadedDims.size < minOf(6, pageCount)) return

        val strips = loadedDims.count { it.second.toFloat() / it.first >= 3f }
        val slivers = loadedDims.count { it.second.toFloat() / it.first <= 0.5f }
        // ≥2.0 (not 1.6): ordinary paged comics include portrait pages up to ~1.9×.
        val tall = loadedDims.count { it.second.toFloat() / it.first >= 2.0f }
        val verdict = when {
            strips * 2 >= loadedDims.size -> "webtoon"
            tall * 5 >= loadedDims.size * 4 -> "webtoon"
            tall + slivers > 4 -> "longstrip"
            else -> "paged"
        }
        if (verdict != s.detectedMode) _state.update { it.copy(detectedMode = verdict) }
    }

    // ── Navigation ────────────────────────────────────────────────────────

    /** The chapter one step from the one on screen, or null at either end. */
    fun neighborChapter(direction: Int): ReaderChapterDto? = _state.value.neighbor(direction)

    fun goToChapter(direction: Int) {
        val s = _state.value
        val next = s.neighbor(direction)
        if (next == null) {
            _state.update {
                it.copy(toast = if (direction > 0) "That was the last chapter." else "This is the first chapter.")
            }
            return
        }
        openChapter(next.number, s.nameOf(next))
    }

    fun jumpToChapter(number: Double) {
        val s = _state.value
        if (number == s.chapterNumber && s.segments.size == 1) return
        openChapter(number, s.chapters.firstOrNull { it.number == number }?.let { s.nameOf(it) })
    }

    // ── Actions ───────────────────────────────────────────────────────────

    fun toggleBookmark() {
        val s = _state.value
        val chapter = s.activeChapter ?: return
        val number = chapter.number
        val next = !chapter.bookmarked
        viewModelScope.launch {
            val api = app.network.currentServiceOf<ReaderApi>()
            if (api == null) {
                _state.update { it.copy(toast = "Failed to update bookmark") }
                return@launch
            }
            val ok = runCatching {
                api.setBookmark(ReaderBookmarkRequestDto(seriesId, number, next))
            }.isSuccess
            if (ok) {
                _state.update { st ->
                    st.copy(
                        chapters = st.chapters.map { if (it.number == number) it.copy(bookmarked = next) else it },
                        toast = if (next) "Bookmarked" else "Bookmark removed",
                    )
                }
            } else {
                _state.update { it.copy(toast = "Failed to update bookmark") }
            }
        }
    }

    /** The settings sheet's "Mark chapter read / unread" button. */
    fun toggleChapterRead() {
        val s = _state.value
        val chapter = s.activeChapter ?: return
        val number = chapter.number
        val completed = !chapter.isCompleted
        _state.update { st ->
            st.copy(
                chapters = st.chapters.map {
                    if (it.number == number) it.copy(isCompleted = completed, progress = if (completed) 1f else 0f) else it
                },
            )
        }
        viewModelScope.launch {
            val api = app.network.currentApi() ?: return@launch
            runCatching { api.markChapters(ReaderMarkRequestDto(seriesId, listOf(number), completed)) }
        }
    }

    fun clearStreamCache() {
        if (_state.value.cacheBusy) return
        _state.update { it.copy(cacheBusy = true) }
        viewModelScope.launch {
            val api = app.network.currentServiceOf<ReaderApi>()
            val result = if (api == null) null else runCatching { api.clearStreamCache() }.getOrNull()
            _state.update {
                it.copy(
                    cacheBusy = false,
                    toast = when {
                        result == null -> "Couldn't clear the cache."
                        result.cleared > 0 ->
                            "Cleared ${result.cleared} cached page image${if (result.cleared == 1L) "" else "s"}."
                        else -> "Cache was already empty."
                    },
                )
            }
        }
    }

    /**
     * Locked-chapter unlock detection. The purchase happens on the source site,
     * so an explicit "check again" is the certain path; a lazy 60s heartbeat
     * covers purchases made elsewhere and chapters turning free with time.
     */
    private fun startUnlockWatch(number: Double) {
        viewModelScope.launch {
            delay(3000)
            while (_state.value.locked && _state.value.chapterNumber == number) {
                checkUnlock()
                delay(60_000)
            }
        }
    }

    fun checkUnlock() {
        val s = _state.value
        if (!s.locked || s.unlockChecking) return
        val number = s.chapterNumber
        _state.update { it.copy(unlockChecking = true) }
        viewModelScope.launch {
            val api = app.network.currentApi()
            // refresh=true bypasses the backend's cached (empty) page list.
            val stream = if (api == null) null else runCatching { api.streamPages(seriesId, number, true) }.getOrNull()
            _state.update { it.copy(unlockChecking = false) }
            if (stream != null && !stream.locked && stream.pageCount > 0) {
                _state.update { st ->
                    st.copy(chapters = st.chapters.map { if (it.number == number) it.copy(locked = false) else it })
                }
                openChapter(number)
            }
        }
    }

    // ── Settings ──────────────────────────────────────────────────────────

    fun updateSettings(transform: (ReaderSettings) -> ReaderSettings) {
        val next = transform(_state.value.settings)
        prefs.save(next)
        _state.update { it.copy(settings = next) }
    }

    /**
     * Reading mode. Library reading stores it PER SERIES (the web writes
     * `renzo_reader_mode_<id>`); "Auto" clears the override.
     */
    fun setMode(mode: ReaderMode) {
        prefs.setSeriesMode(seriesId, mode)
        _state.update { it.copy(seriesModeOverride = if (mode == ReaderMode.AUTO) null else mode) }
    }

    fun consumeToast() {
        if (_state.value.toast != null) _state.update { it.copy(toast = null) }
    }

    override fun onCleared() {
        super.onCleared()
        // Clear the server's streamed-page cache on exit — but only if pages were
        // actually streamed (downloaded reads don't touch that cache). viewModelScope
        // is already cancelled here, so this runs on its own short-lived scope.
        if (_state.value.settings.autoClearCache && usedStream) {
            val api = app.network.currentServiceOf<ReaderApi>()
            if (api != null) {
                CoroutineScope(SupervisorJob() + Dispatchers.IO).launch {
                    runCatching { api.clearStreamCache() }
                }
            }
        }
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
