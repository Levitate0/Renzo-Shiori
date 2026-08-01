package app.renzoshiori.client.ui.series

import android.app.Application
import android.content.Intent
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.RenzoDownloadService
import app.renzoshiori.client.RenzoStore
import app.renzoshiori.client.data.model.ReaderChapterDto
import app.renzoshiori.client.data.model.ReaderChaptersDto
import app.renzoshiori.client.data.model.ReaderMarkRequestDto
import app.renzoshiori.client.data.network.encodeFilename
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

data class SeriesDetailUiState(
    val loading: Boolean = true,
    val error: String? = null,
    val title: String = "",
    val chapters: List<ReaderChapterDto> = emptyList(),
    /** chapterKeys (seriesId:number) already saved on this device. */
    val offlineKeys: Set<String> = emptySet(),
    /** Hero metadata (cover/status/author/description), loaded alongside chapters. */
    val info: app.renzoshiori.client.data.model.SeriesInfoDto? = null,
)

fun chapterKey(seriesId: String, number: Double): String = "$seriesId:$number"

class SeriesDetailViewModel(
    application: Application,
    private val seriesId: String,
) : AndroidViewModel(application) {
    private val app = application as RenzoApp
    private val store = RenzoStore(application)

    private val _state = MutableStateFlow(SeriesDetailUiState())
    val state: StateFlow<SeriesDetailUiState> = _state.asStateFlow()

    val baseUrl: String get() = app.tokenStore.serverUrl ?: ""

    init {
        refresh()
    }

    fun refresh() {
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            val api = app.network.currentApi()
            if (api == null) {
                _state.value = _state.value.copy(loading = false, error = "Not connected")
                return@launch
            }
            val info = runCatching { api.series(seriesId) }.getOrNull()
            runCatching { api.readerChapters(seriesId) }
                .onSuccess { dto: ReaderChaptersDto ->
                    val offline = withContext(Dispatchers.IO) {
                        dto.chapters.map { chapterKey(seriesId, it.number) }
                            .filter { store.hasChapter(it) }
                            .toSet()
                    }
                    _state.value = SeriesDetailUiState(
                        loading = false,
                        title = dto.title,
                        chapters = dto.chapters.sortedByDescending { it.number },
                        offlineKeys = offline,
                        info = info,
                    )
                }
                .onFailure {
                    _state.value = _state.value.copy(loading = false, error = "Couldn't load chapters.")
                }
        }
    }

    /**
     * Queue chapters for offline download via the existing RenzoDownloadService —
     * the exact same JSON job payload the web bridge used to enqueue, now built
     * natively. Only server-downloaded chapters (filename != null) qualify:
     * page URLs come from /api/reader/page, and page counts must be known.
     */
    fun saveOffline(chapters: List<ReaderChapterDto>) {
        val token = app.tokenStore.accessToken ?: return
        val base = app.tokenStore.serverUrl ?: return
        viewModelScope.launch(Dispatchers.IO) {
            val api = app.network.currentApi() ?: return@launch
            val jobChapters = JSONArray()
            for (ch in chapters) {
                val filename = ch.filename ?: continue
                val key = chapterKey(seriesId, ch.number)
                if (store.hasChapter(key)) continue
                val pageCount = ch.pageCount
                    ?: runCatching { api.chapterInfo(seriesId, encodeFilename(filename)).pageCount }.getOrNull()
                    ?: continue
                val paths = JSONArray()
                for (p in 0 until pageCount) {
                    paths.put("/api/reader/page?seriesId=$seriesId&filename=${encodeFilename(filename)}&page=$p")
                }
                jobChapters.put(
                    JSONObject()
                        .put("chapterKey", key)
                        .put("chapterNumber", ch.number)
                        .put("pagePaths", paths),
                )
            }
            if (jobChapters.length() == 0) return@launch

            val payload = JSONObject()
                .put("baseUrl", base)
                .put("token", token)
                .put(
                    "series",
                    JSONObject()
                        .put("seriesId", seriesId)
                        .put("title", _state.value.title),
                )
                .put("chapters", jobChapters)
            store.enqueueJob(payload.toString())

            val ctx = getApplication<Application>()
            val intent = Intent(ctx, RenzoDownloadService::class.java).setAction(RenzoDownloadService.ACTION_ENQUEUE)
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                ctx.startForegroundService(intent)
            } else {
                ctx.startService(intent)
            }
        }
    }

    fun markRead(numbers: List<Double>, read: Boolean) {
        viewModelScope.launch {
            val api = app.network.currentApi() ?: return@launch
            runCatching {
                api.markChapters(ReaderMarkRequestDto(seriesId, numbers, read))
            }.onSuccess { refresh() }
        }
    }

    companion object {
        fun factory(application: Application, seriesId: String) = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T =
                SeriesDetailViewModel(application, seriesId) as T
        }
    }
}
