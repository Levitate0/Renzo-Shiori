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
import app.renzoshiori.client.data.model.ChapterDetailDto
import app.renzoshiori.client.data.model.ChapterSourceDto
import app.renzoshiori.client.data.model.DownloadInfoDto
import app.renzoshiori.client.data.model.ReaderChaptersDto
import app.renzoshiori.client.data.model.ReaderMarkRequestDto
import app.renzoshiori.client.data.model.SeriesStatus
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.data.network.ApiService
import app.renzoshiori.client.data.network.CreateFavoriteListDto
import app.renzoshiori.client.data.network.DeleteDownloadsRequestDto
import app.renzoshiori.client.data.network.DisableLinkRequestDto
import app.renzoshiori.client.data.network.FavoriteItemRequestDto
import app.renzoshiori.client.data.network.FavoriteListDto
import app.renzoshiori.client.data.network.ProviderExtendedDto
import app.renzoshiori.client.data.network.ScrobblerConfigDto
import app.renzoshiori.client.data.network.SeriesDetailApi
import app.renzoshiori.client.data.network.SeriesDetailSettingsDto
import app.renzoshiori.client.data.network.SeriesExtendedDto
import app.renzoshiori.client.data.network.SeriesIntegrityResultDto
import app.renzoshiori.client.data.network.SeriesMatchStatusDto
import app.renzoshiori.client.data.network.SetCategoryRequestDto
import app.renzoshiori.client.data.network.encodeFilename
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

/**
 * One chapter row, as the web series page renders it: the merged
 * api/serie/chapters entry (downloaded / source / upload date / locked)
 * joined with the reader read-state (progress / completed / bookmarked) by
 * chapter number — the exact same join chapters-section.tsx does.
 */
data class ChapterRowUi(
    val number: Double,
    val name: String,
    val downloaded: Boolean,
    val sourceProviderId: String? = null,
    val sourceProviderName: String?,
    val uploadDate: String?,
    val locked: Boolean,
    val progress: Float,
    val isCompleted: Boolean,
    val bookmarked: Boolean,
    val filename: String?,
    val pageCount: Int?,
    /** Sources that can (re-)download this chapter — the row's source picker. */
    val availableProviders: List<ChapterSourceDto> = emptyList(),
)

/** The Perm / Cover / Title / Status pill toggles of one source row. */
data class ProviderSwitchState(
    val useTitle: Boolean = false,
    val useCover: Boolean = false,
    val useStorage: Boolean = false,
    val useStatus: Boolean = false,
)

/** Which delete-downloads confirmation the chapters section is showing. */
enum class DeleteDownloadsScope { ALL, SELECTED }

/** A transient message — the native stand-in for the web's toast(). */
data class ToastMessage(
    val title: String,
    val description: String? = null,
    val destructive: Boolean = false,
)

data class SeriesDetailUiState(
    val loading: Boolean = true,
    val error: String? = null,

    // ── Series ──
    val series: SeriesExtendedDto? = null,
    /** Title of the source flagged "Use as Title", else the series title. */
    val title: String = "",
    /** Cover of the source flagged "Use as Cover", else the series cover. */
    val displayThumbnail: String = "",
    /** DISABLED when every known source is off, else the series status. */
    val effectiveStatus: Int = SeriesStatus.UNKNOWN,
    val pausedDownloads: Boolean = false,

    // ── Sources ──
    val providers: List<ProviderExtendedDto> = emptyList(),
    val providerSwitches: Map<String, ProviderSwitchState> = emptyMap(),
    val providerDisabled: Map<String, Boolean> = emptyMap(),
    val providerFromChapters: Map<String, String> = emptyMap(),
    /** Provider ids, highest priority first — local buffer until Apply. */
    val providerOrder: List<String> = emptyList(),
    val orderDirty: Boolean = false,
    val applyingOrder: Boolean = false,

    // ── Chapters ──
    val chapters: List<ChapterRowUi> = emptyList(),
    val query: String = "",
    val missingOnly: Boolean = false,
    val selecting: Boolean = false,
    val selected: Set<Double> = emptySet(),
    val pending: Set<Double> = emptySet(),
    val readPending: Set<Double> = emptySet(),
    val markingAll: Boolean = false,
    val bulkPending: Boolean = false,
    val downloadAllPending: Boolean = false,
    val deleteDownloadsPending: Boolean = false,
    val confirmDelete: DeleteDownloadsScope? = null,
    val chaptersLoading: Boolean = true,
    val chaptersError: Boolean = false,
    /** chapterKeys (seriesId:number) already saved on this device. */
    val offlineKeys: Set<String> = emptySet(),
    val offlineSaving: Set<Double> = emptySet(),

    // ── Latest downloads ──
    val downloads: List<DownloadInfoDto> = emptyList(),
    val downloadsLoading: Boolean = true,
    val downloadsError: Boolean = false,

    // ── Permissions + settings ──
    val canEdit: Boolean = false,
    val canDelete: Boolean = false,
    val canManageDownloads: Boolean = false,
    val readerEnabled: Boolean = true,
    val categories: List<String> = emptyList(),
    val defaultSourcePriorityOrder: List<String> = emptyList(),

    // ── Hero action row ──
    val favoriteLists: List<FavoriteListDto> = emptyList(),
    val trackerConfigs: List<ScrobblerConfigDto> = emptyList(),
    val trackerMatches: List<SeriesMatchStatusDto> = emptyList(),
    val trackerBusy: Boolean = false,

    // ── Pending admin actions ──
    val refreshPending: Boolean = false,
    val scanPending: Boolean = false,
    val verifyPending: Boolean = false,
    val deletePending: Boolean = false,
    val deleted: Boolean = false,
    val verifyResult: SeriesIntegrityResultDto? = null,
    val showVerifyDialog: Boolean = false,
    val showCleanupDialog: Boolean = false,
    val cleanupPending: Boolean = false,

    val toast: ToastMessage? = null,
) {
    val total: Int get() = chapters.size
    val downloadedCount: Int get() = chapters.count { it.downloaded }
    val missingCount: Int get() = total - downloadedCount

    /** The chapter rows the toolbar's filter + "Missing only" chip leave visible. */
    val filteredChapters: List<ChapterRowUi>
        get() {
            var list = chapters
            if (missingOnly) list = list.filter { !it.downloaded }
            val q = query.trim().lowercase()
            if (q.isNotEmpty()) {
                list = list.filter { c ->
                    c.number.toString().contains(q) || c.name.lowercase().contains(q)
                }
            }
            return list
        }

    val filteredNumbers: List<Double> get() = filteredChapters.map { it.number }
    val allVisibleSelected: Boolean
        get() = filteredNumbers.isNotEmpty() && filteredNumbers.all { it in selected }

    /** Selected rows that actually have a file — what "delete selected" removes. */
    val selectedDownloadedNumbers: List<Double>
        get() {
            val downloaded = chapters.filter { it.downloaded }.map { it.number }.toSet()
            return selected.filter { it in downloaded }
        }

    val allDownloadedNumbers: List<Double> get() = chapters.filter { it.downloaded }.map { it.number }

    val isFavorited: Boolean
        get() = series != null && favoriteLists.any { series.id in it.seriesIds }

    /** Trackers the user connected AND that carry a live link for this series. */
    val activeTrackerLinks: List<ScrobblerConfigDto>
        get() = trackerConfigs.filter { c ->
            c.isConnected && trackerMatches.any {
                it.seriesId.equals(series?.id ?: "", ignoreCase = true) &&
                    it.provider == c.provider &&
                    (it.mappingStatus == 1 || it.mappingStatus == 2)
            }
        }
    val tracked: Boolean get() = activeTrackerLinks.isNotEmpty()
    val hasConnectedTracker: Boolean get() = trackerConfigs.any { it.isConnected }
}

fun chapterKey(seriesId: String, number: Double): String = "$seriesId:$number"

/** SeriesStatus.DISABLED — the web's synthetic "every source is off" status. */
const val SERIES_STATUS_DISABLED = 7

class SeriesDetailViewModel(
    application: Application,
    private val seriesId: String,
) : AndroidViewModel(application) {
    private val app = application as RenzoApp
    private val store = RenzoStore(application)

    private val _state = MutableStateFlow(SeriesDetailUiState())
    val state: StateFlow<SeriesDetailUiState> = _state.asStateFlow()

    val baseUrl: String get() = app.tokenStore.serverUrl ?: ""

    private fun api(): ApiService? = app.network.currentApi()
    private fun detail(): SeriesDetailApi? = app.network.currentServiceOf<SeriesDetailApi>()

    init {
        refresh()
        // Opening a series kicks a stale-guarded source scan and then re-pulls
        // every 20s (downloads every 10s), exactly like chapters-section.tsx.
        viewModelScope.launch {
            while (true) {
                delay(20_000)
                if (_state.value.deleted) return@launch
                loadChapters()
                loadSeries()
            }
        }
        viewModelScope.launch {
            while (true) {
                delay(10_000)
                if (_state.value.deleted) return@launch
                loadDownloads()
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Loading
    // ──────────────────────────────────────────────────────────────────────

    fun refresh() {
        _state.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            val d = detail()
            if (d == null || api() == null) {
                _state.update { it.copy(loading = false, error = "Not connected") }
                return@launch
            }
            loadPermissionsAndSettings()
            loadSeries()
            if (_state.value.canManageDownloads) {
                runCatching { d.refreshSeries(seriesId, ifStale = true) }
            }
            loadChapters()
            loadDownloads()
            loadFavorites()
            loadTrackers()
            _state.update { it.copy(loading = false) }
        }
    }

    private suspend fun loadPermissionsAndSettings() {
        val a = api() ?: return
        val me = runCatching { a.me() }.getOrNull()
        val level = me?.level ?: UserLevel.OWNER
        val canManage = level >= UserLevel.MANAGER
        val defaults = parseDefaultPriorityOrder(me?.preferences)
        val settings: SeriesDetailSettingsDto =
            runCatching { detail()?.settings() }.getOrNull() ?: SeriesDetailSettingsDto()
        _state.update {
            it.copy(
                canEdit = canManage,
                canDelete = canManage,
                canManageDownloads = canManage,
                readerEnabled = settings.readerEnabled,
                categories = if (settings.categorizedFolders) settings.categories else emptyList(),
                defaultSourcePriorityOrder = defaults,
            )
        }
    }

    /** The per-user "Default priority order" list, out of the preferences blob. */
    private fun parseDefaultPriorityOrder(preferences: String?): List<String> {
        if (preferences.isNullOrBlank()) return emptyList()
        return runCatching {
            val arr = JSONObject(preferences).optJSONArray("defaultSourcePriorityOrder")
                ?: return emptyList()
            (0 until arr.length()).mapNotNull { i -> arr.optString(i).takeIf { it.isNotEmpty() } }
        }.getOrDefault(emptyList())
    }

    private suspend fun loadSeries() {
        val d = detail() ?: return
        val series = runCatching { d.seriesExtended(seriesId) }.getOrNull()
        if (series == null) {
            if (_state.value.series == null) {
                _state.update { it.copy(loading = false, error = "Couldn't load this series.") }
            }
            return
        }
        applySeries(series, reseedOrder = _state.value.providerOrder.isEmpty() || !_state.value.orderDirty)
    }

    /** Fold a server SeriesExtendedDto into the derived display state. */
    private fun applySeries(series: SeriesExtendedDto, reseedOrder: Boolean) {
        val visible = series.providers.filter { !it.isDeleted }
        val switches = visible.associate { p ->
            val unknownThumb = p.thumbnailUrl?.lowercase()?.contains("unknown") == true
            p.id to ProviderSwitchState(
                useTitle = p.useTitle,
                useCover = if (unknownThumb) false else p.useCover,
                useStorage = p.isStorage,
                useStatus = p.useStatus,
            )
        }
        val disabled = visible.associate { p -> p.id to (p.isUninstalled || p.isDisabled) }
        val fromChapters = visible.associate { p ->
            p.id to (p.fromChapter?.let { formatFromChapter(it) } ?: "")
        }
        val serverOrder = visible.sortedBy { it.priority }.map { it.id }
        val order = if (reseedOrder) serverOrder else reconcileOrder(_state.value.providerOrder, visible)

        val activeTitle = visible.firstOrNull { switches[it.id]?.useTitle == true }
        val activeCover = visible.firstOrNull { switches[it.id]?.useCover == true }
        val known = visible.filter { !it.isUnknown && !it.isUninstalled }
        val allDisabled = known.isNotEmpty() && known.all { disabled[it.id] == true }
        val hasActive = known.any { disabled[it.id] != true }
        val effectiveStatus =
            if (!hasActive || allDisabled) SERIES_STATUS_DISABLED else series.status

        _state.update {
            it.copy(
                series = series,
                providers = visible,
                providerSwitches = switches,
                providerDisabled = disabled,
                providerFromChapters = fromChapters,
                providerOrder = order,
                orderDirty = order != serverOrder,
                pausedDownloads = series.pausedDownloads,
                title = activeTitle?.title?.takeIf { t -> t.isNotEmpty() } ?: series.title,
                displayThumbnail = activeCover?.thumbnailUrl?.takeIf { u -> u.isNotEmpty() }
                    ?: series.thumbnailUrl,
                effectiveStatus = effectiveStatus,
                error = null,
            )
        }
    }

    /** Keep the local order buffer valid as sources come and go. */
    private fun reconcileOrder(buffer: List<String>, providers: List<ProviderExtendedDto>): List<String> {
        val known = providers.map { it.id }.toSet()
        val base = buffer.filter { it in known }
        val missing = providers.filter { it.id !in base }.sortedBy { it.priority }.map { it.id }
        return base + missing
    }

    private suspend fun loadChapters() {
        val a = api() ?: return
        val detailList: List<ChapterDetailDto> =
            runCatching { a.seriesChapters(seriesId) }.getOrNull() ?: emptyList()
        val reader = runCatching { a.readerChapters(seriesId) }.getOrNull()
        if (reader == null && detailList.isEmpty()) {
            _state.update { it.copy(chaptersLoading = false, chaptersError = true) }
            return
        }
        val rows = mergeChapters(detailList, reader)
        val offline = withContext(Dispatchers.IO) {
            rows.map { chapterKey(seriesId, it.number) }.filter { store.hasChapter(it) }.toSet()
        }
        _state.update {
            it.copy(
                chapters = rows,
                chaptersLoading = false,
                chaptersError = false,
                offlineKeys = offline,
            )
        }
    }

    /**
     * The join chapters-section.tsx does: the unified series chapter list
     * (downloaded / source / date / locked / available sources) overlaid with
     * the reader's read-state map, keyed by chapter number.
     */
    private fun mergeChapters(
        detailList: List<ChapterDetailDto>,
        reader: ReaderChaptersDto?,
    ): List<ChapterRowUi> {
        val readByNumber = (reader?.chapters ?: emptyList()).associateBy { it.number }
        val detailByNumber = detailList.filter { it.number != null }.associateBy { it.number!! }
        val numbers = (detailByNumber.keys + readByNumber.keys).toSortedSet()
        return numbers.map { n ->
            val d = detailByNumber[n]
            val r = readByNumber[n]
            ChapterRowUi(
                number = n,
                name = (d?.name ?: "").ifEmpty { r?.name ?: "" },
                downloaded = d?.downloaded ?: (r?.filename != null),
                sourceProviderId = d?.sourceProviderId,
                sourceProviderName = d?.sourceProviderName,
                uploadDate = d?.uploadDate,
                locked = (d?.locked ?: false) || (r?.locked ?: false),
                progress = r?.progress ?: 0f,
                isCompleted = r?.isCompleted ?: false,
                bookmarked = r?.bookmarked ?: false,
                filename = r?.filename,
                pageCount = r?.pageCount,
                availableProviders = d?.availableProviders ?: emptyList(),
            )
        }.sortedByDescending { it.number }
    }

    private suspend fun loadDownloads() {
        val d = detail() ?: return
        runCatching { d.downloadsForSeries(seriesId) }
            .onSuccess { list ->
                _state.update {
                    it.copy(
                        downloads = list.sortedByDescending { dl -> dl.scheduledDateUTC },
                        downloadsLoading = false,
                        downloadsError = false,
                    )
                }
            }
            .onFailure {
                _state.update { s -> s.copy(downloadsLoading = false, downloadsError = true) }
            }
    }

    private suspend fun loadFavorites() {
        val d = detail() ?: return
        runCatching { d.favorites() }.onSuccess { lists ->
            _state.update { it.copy(favoriteLists = lists) }
        }
    }

    private suspend fun loadTrackers() {
        val d = detail() ?: return
        val configs = runCatching { d.scrobblerConfigs() }.getOrNull() ?: emptyList()
        val matches = runCatching { d.scrobblerMatches() }.getOrNull() ?: emptyList()
        _state.update { it.copy(trackerConfigs = configs, trackerMatches = matches) }
    }

    // ──────────────────────────────────────────────────────────────────────
    // The one series write — everything on the Sources card funnels here
    // ──────────────────────────────────────────────────────────────────────

    /**
     * Builds the full SeriesExtendedDto the backend's PATCH expects out of the
     * current local switch/disabled/fromChapter/priority state, with optional
     * per-call overrides — the native equivalent of the web page's
     * updateSeriesWith*() family (which all end at the same mutation).
     */
    private suspend fun pushSeries(
        pausedOverride: Boolean? = null,
        nsfwOverride: Boolean? = null,
        hideDecimalOverride: Boolean? = null,
        switchOverrides: Map<String, ProviderSwitchState> = emptyMap(),
        disabledOverrides: Map<String, Boolean> = emptyMap(),
        fromChapterOverrides: Map<String, String> = emptyMap(),
        deleteProviderId: String? = null,
        priorityById: Map<String, Int> = emptyMap(),
    ): Boolean {
        val s = _state.value
        val series = s.series ?: return false
        val d = detail() ?: return false

        val providers = series.providers.map { p ->
            val sw = switchOverrides[p.id] ?: s.providerSwitches[p.id] ?: ProviderSwitchState(
                useTitle = p.useTitle,
                useCover = p.useCover,
                useStorage = p.isStorage,
                useStatus = p.useStatus,
            )
            val disabled = disabledOverrides[p.id] ?: s.providerDisabled[p.id] ?: p.isDisabled
            val fromRaw = fromChapterOverrides[p.id] ?: s.providerFromChapters[p.id]
            p.copy(
                isDisabled = if (p.isUninstalled) true else disabled,
                isDeleted = p.id == deleteProviderId,
                fromChapter = fromRaw?.let { it.trim().toDoubleOrNull() ?: 0.0 } ?: p.fromChapter,
                useTitle = sw.useTitle,
                useCover = sw.useCover,
                isStorage = sw.useStorage,
                useStatus = sw.useStatus,
                priority = priorityById[p.id] ?: p.priority,
            )
        }
        val payload = series.copy(
            pausedDownloads = pausedOverride ?: s.pausedDownloads,
            nsfw = nsfwOverride ?: series.nsfw,
            hideDecimalChapters = hideDecimalOverride ?: series.hideDecimalChapters,
            providers = providers,
        )
        return runCatching { d.updateSeries(payload) }
            .onSuccess { applySeries(it, reseedOrder = true) }
            .onFailure { toast("Couldn't save changes", "Please try again.", destructive = true) }
            .isSuccess
    }

    // ── Source pill toggles (title/cover/status are single-select) ─────────

    fun setUseTitle(providerId: String, enabled: Boolean) = viewModelScope.launch {
        pushSeries(switchOverrides = exclusiveSwitch(providerId, enabled) { sw, v -> sw.copy(useTitle = v) })
    }

    fun setUseCover(providerId: String, enabled: Boolean) = viewModelScope.launch {
        pushSeries(switchOverrides = exclusiveSwitch(providerId, enabled) { sw, v -> sw.copy(useCover = v) })
    }

    fun setUseStatus(providerId: String, enabled: Boolean) = viewModelScope.launch {
        pushSeries(switchOverrides = exclusiveSwitch(providerId, enabled) { sw, v -> sw.copy(useStatus = v) })
    }

    fun setUseStorage(providerId: String, enabled: Boolean) = viewModelScope.launch {
        val current = _state.value.providerSwitches[providerId] ?: ProviderSwitchState()
        pushSeries(switchOverrides = mapOf(providerId to current.copy(useStorage = enabled)))
    }

    /** Turning one on turns it off everywhere else, matching the web handlers. */
    private fun exclusiveSwitch(
        providerId: String,
        enabled: Boolean,
        set: (ProviderSwitchState, Boolean) -> ProviderSwitchState,
    ): Map<String, ProviderSwitchState> {
        val switches = _state.value.providerSwitches
        val result = mutableMapOf<String, ProviderSwitchState>()
        if (enabled) {
            switches.forEach { (id, sw) -> if (id != providerId) result[id] = set(sw, false) }
        }
        result[providerId] = set(switches[providerId] ?: ProviderSwitchState(), enabled)
        return result
    }

    fun setFromChapter(providerId: String, value: String) {
        _state.update { it.copy(providerFromChapters = it.providerFromChapters + (providerId to value)) }
    }

    /** Commit the "After ch." box (the web commits on blur / Enter). */
    fun commitFromChapter(providerId: String, value: String) = viewModelScope.launch {
        _state.update { it.copy(providerFromChapters = it.providerFromChapters + (providerId to value)) }
        pushSeries(fromChapterOverrides = mapOf(providerId to value))
    }

    fun setProviderDisabled(providerId: String, disabled: Boolean) = viewModelScope.launch {
        _state.update { it.copy(providerDisabled = it.providerDisabled + (providerId to disabled)) }
        pushSeries(disabledOverrides = mapOf(providerId to disabled))
    }

    fun deleteProvider(providerId: String) = viewModelScope.launch {
        _state.update { it.copy(providerDisabled = it.providerDisabled + (providerId to true)) }
        if (pushSeries(disabledOverrides = mapOf(providerId to true), deleteProviderId = providerId)) {
            toast("Source removed")
        }
    }

    // ── Priority order (local buffer until Apply) ──────────────────────────

    fun moveProvider(providerId: String, up: Boolean) {
        val order = _state.value.providerOrder.toMutableList()
        val idx = order.indexOf(providerId)
        if (idx < 0) return
        val swap = if (up) idx - 1 else idx + 1
        if (swap < 0 || swap >= order.size) return
        val tmp = order[idx]
        order[idx] = order[swap]
        order[swap] = tmp
        setOrder(order)
    }

    fun setOrder(newOrder: List<String>) {
        val serverOrder = _state.value.providers.sortedBy { it.priority }.map { it.id }
        _state.update { it.copy(providerOrder = newOrder, orderDirty = newOrder != serverOrder) }
    }

    /** Reset the buffer to the user's configured default order (by source name). */
    fun revertOrderToDefault() {
        val s = _state.value
        val defaults = s.defaultSourcePriorityOrder
        if (defaults.isEmpty()) {
            toast(
                "No default priority order set up",
                "Set one up on the Sources page's \"Default priority order\" tab first.",
            )
            return
        }
        val rank = defaults.mapIndexed { i, name -> name.lowercase() to i }.toMap()
        val next = s.providers
            .mapIndexed { i, p -> Triple(p, rank[p.provider.lowercase()] ?: Int.MAX_VALUE, i) }
            .sortedWith(compareBy({ it.second }, { it.third }))
            .map { it.first.id }
        setOrder(next)
    }

    fun applyOrder() = viewModelScope.launch {
        val order = _state.value.providerOrder
        if (order.isEmpty()) return@launch
        _state.update { it.copy(applyingOrder = true) }
        val priorityById = order.withIndex().associate { (i, id) -> id to i }
        val ok = pushSeries(priorityById = priorityById)
        _state.update { it.copy(applyingOrder = false) }
        if (ok) toast("Source priority order saved")
        else toast("Apply failed", "Could not update source priority.", destructive = true)
    }

    // ── Series-level toggles + admin actions ──────────────────────────────

    fun togglePausedDownloads() = viewModelScope.launch {
        val next = !_state.value.pausedDownloads
        _state.update { it.copy(pausedDownloads = next) }
        pushSeries(pausedOverride = next)
    }

    fun toggleNsfw() = viewModelScope.launch {
        pushSeries(nsfwOverride = !(_state.value.series?.nsfw ?: false))
    }

    fun toggleHideDecimalChapters() = viewModelScope.launch {
        pushSeries(hideDecimalOverride = !(_state.value.series?.hideDecimalChapters ?: false))
    }

    fun setCategory(category: String?) = viewModelScope.launch {
        val d = detail() ?: return@launch
        runCatching { d.setCategory(seriesId, SetCategoryRequestDto(category)) }
            .onSuccess { res ->
                toast(
                    if (res.moved) "Series moved" else "Category updated",
                    if (category != null) "Now filed under $category." else "Moved back to the library root.",
                )
                loadSeries()
            }
            .onFailure { toast("Couldn't change category", "Please try again.", destructive = true) }
    }

    fun refreshMetadata() = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(refreshPending = true) }
        runCatching { d.refreshSeries(seriesId) }
            .onSuccess { res ->
                toast(
                    "Refresh queued",
                    if (res.queued > 0)
                        "Checking ${res.queued} source${plural(res.queued)} for new metadata & chapters."
                    else "No active sources to refresh.",
                )
            }
            .onFailure {
                toast("Refresh failed", "Could not queue the series refresh. Please try again.", destructive = true)
            }
        _state.update { it.copy(refreshPending = false) }
    }

    fun scanForNewChapters() = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(scanPending = true) }
        runCatching { d.scanSeries(seriesId) }
            .onSuccess { res ->
                if (res.pruned > 0) loadChapters()
                val scanPart = if (res.queued > 0)
                    "Scanning ${res.queued} source${plural(res.queued)} for new chapters."
                else "No active sources to scan."
                val prunedPart = if (res.pruned > 0)
                    " Removed ${res.pruned} stale chapter${plural(res.pruned)} from disabled sources."
                else ""
                toast("Scan queued", scanPart + prunedPart)
            }
            .onFailure {
                toast("Scan failed", "Could not queue the chapter scan. Please try again.", destructive = true)
            }
        _state.update { it.copy(scanPending = false) }
    }

    fun verifyIntegrity() = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(verifyPending = true) }
        runCatching { d.verifyIntegrity(seriesId) }
            .onSuccess { res ->
                loadSeries()
                _state.update {
                    it.copy(
                        verifyResult = res,
                        showVerifyDialog = res.success,
                        showCleanupDialog = !res.success,
                    )
                }
            }
            .onFailure {
                toast("Verify failed", "Could not verify this series. Please try again.", destructive = true)
            }
        _state.update { it.copy(verifyPending = false) }
    }

    fun dismissVerifyDialog() = viewModelScope.launch {
        _state.update { it.copy(showVerifyDialog = false, verifyResult = null) }
        loadSeries()
    }

    fun dismissCleanupDialog() {
        _state.update { it.copy(showCleanupDialog = false, verifyResult = null) }
    }

    fun confirmCleanup() = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(cleanupPending = true) }
        runCatching { d.cleanupSeries(seriesId) }
        _state.update { it.copy(cleanupPending = false, showCleanupDialog = false, verifyResult = null) }
        loadSeries()
        loadChapters()
    }

    fun deleteSeries(alsoPhysical: Boolean) = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(deletePending = true) }
        runCatching { d.deleteSeries(seriesId, alsoPhysical) }
            .onSuccess { _state.update { s -> s.copy(deletePending = false, deleted = true) } }
            .onFailure {
                _state.update { s -> s.copy(deletePending = false) }
                toast("Couldn't delete series", "Please try again.", destructive = true)
            }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Chapters toolbar + rows
    // ──────────────────────────────────────────────────────────────────────

    fun setQuery(value: String) = _state.update { it.copy(query = value) }
    fun toggleMissingOnly() = _state.update { it.copy(missingOnly = !it.missingOnly) }

    fun toggleSelecting() = _state.update {
        if (it.selecting) it.copy(selecting = false, selected = emptySet())
        else it.copy(selecting = true)
    }

    fun exitSelection() = _state.update { it.copy(selecting = false, selected = emptySet()) }

    fun toggleSelected(number: Double) = _state.update {
        it.copy(selected = if (number in it.selected) it.selected - number else it.selected + number)
    }

    fun selectAllOrNone() = _state.update {
        val visible = it.filteredNumbers
        if (visible.isNotEmpty() && visible.all { n -> n in it.selected })
            it.copy(selected = it.selected - visible.toSet())
        else it.copy(selected = it.selected + visible)
    }

    /** Fill in every chapter between the first and last selected (visible order). */
    fun selectInBetween() = _state.update { s ->
        val visible = s.filteredNumbers
        val idxs = visible.indices.filter { visible[it] in s.selected }
        if (idxs.isEmpty()) return@update s
        val range = visible.subList(idxs.first(), idxs.last() + 1)
        s.copy(selected = s.selected + range)
    }

    fun invertSelection() = _state.update { s ->
        val visible = s.filteredNumbers
        val next = s.selected.toMutableSet()
        visible.forEach { if (it in next) next.remove(it) else next.add(it) }
        s.copy(selected = next)
    }

    fun toggleRead(number: Double, read: Boolean) = viewModelScope.launch {
        val a = api() ?: return@launch
        _state.update { it.copy(readPending = it.readPending + number) }
        // Optimistic flip so the row updates instantly, rolled back on failure.
        val previous = _state.value.chapters
        _state.update { s ->
            s.copy(
                chapters = s.chapters.map {
                    if (it.number == number) it.copy(isCompleted = read, progress = if (read) 1f else 0f) else it
                },
            )
        }
        runCatching { a.markChapters(ReaderMarkRequestDto(seriesId, listOf(number), read)) }
            .onFailure {
                _state.update { s -> s.copy(chapters = previous) }
                toast(
                    if (read) "Couldn't mark as read" else "Couldn't mark as unread",
                    "Please try again.",
                    destructive = true,
                )
            }
        _state.update { it.copy(readPending = it.readPending - number) }
        loadChapters()
    }

    fun markAllRead() = viewModelScope.launch {
        val a = api() ?: return@launch
        val numbers = _state.value.chapters.map { it.number }
        if (numbers.isEmpty()) return@launch
        _state.update { it.copy(markingAll = true) }
        val previous = _state.value.chapters
        _state.update { s -> s.copy(chapters = s.chapters.map { it.copy(isCompleted = true, progress = 1f) }) }
        runCatching { a.markChapters(ReaderMarkRequestDto(seriesId, numbers, true)) }
            .onSuccess {
                toast(
                    "All chapters marked as read",
                    "Marked ${numbers.size} chapter${plural(numbers.size)} as read.",
                )
            }
            .onFailure {
                _state.update { s -> s.copy(chapters = previous) }
                toast("Couldn't mark all as read", "Please try again.", destructive = true)
            }
        _state.update { it.copy(markingAll = false) }
        loadChapters()
    }

    fun bulkMark(read: Boolean) = viewModelScope.launch {
        val a = api() ?: return@launch
        val numbers = _state.value.selected.toList()
        if (numbers.isEmpty()) return@launch
        _state.update { it.copy(bulkPending = true) }
        runCatching { a.markChapters(ReaderMarkRequestDto(seriesId, numbers, read)) }
            .onSuccess {
                toast(
                    if (read) "Marked as read" else "Marked as unread",
                    "${numbers.size} chapter${plural(numbers.size)} updated.",
                )
                exitSelection()
            }
            .onFailure { toast("Couldn't update read state", "Please try again.", destructive = true) }
        _state.update { it.copy(bulkPending = false) }
        loadChapters()
    }

    fun redownload(number: Double, providerId: String? = null) = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(pending = it.pending + number) }
        runCatching { d.redownloadChapter(seriesId, number, providerId) }
            .onSuccess { res ->
                toast(
                    "Re-download queued",
                    res.sourceProviderName?.let { n -> "Queued chapter ${formatNumber(number)} from $n." }
                        ?: "Queued chapter ${formatNumber(number)} for download.",
                )
            }
            .onFailure {
                toast(
                    "Re-download failed",
                    "Could not queue the chapter. Please try again.",
                    destructive = true,
                )
            }
        _state.update { it.copy(pending = it.pending - number) }
        loadDownloads()
    }

    fun bulkDownload() = viewModelScope.launch {
        val d = detail() ?: return@launch
        val numbers = _state.value.selected.toList()
        if (numbers.isEmpty()) return@launch
        _state.update { it.copy(bulkPending = true, pending = it.pending + numbers) }
        var ok = 0
        for (n in numbers) {
            if (runCatching { d.redownloadChapter(seriesId, n, null) }.isSuccess) ok++
        }
        val failed = numbers.size - ok
        _state.update { it.copy(bulkPending = false, pending = it.pending - numbers.toSet()) }
        if (ok > 0) {
            toast(
                "Downloads queued",
                "Queued $ok chapter${plural(ok)}" +
                    (if (failed > 0) ", $failed couldn't be queued" else "") + ".",
            )
        } else {
            toast(
                "Couldn't queue downloads",
                "None of the selected chapters could be queued.",
                destructive = true,
            )
        }
        exitSelection()
        loadDownloads()
    }

    fun downloadAll() = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(downloadAllPending = true) }
        runCatching { d.downloadAll(seriesId) }
            .onSuccess { res ->
                toast(
                    "Downloading all chapters",
                    if (res.queued > 0)
                        "Queued ${res.queued} missing chapter${plural(res.queued)}; also checking sources for any newer ones."
                    else "No missing chapters to queue — checking sources for anything new.",
                )
            }
            .onFailure {
                toast(
                    "Couldn't queue downloads",
                    if (_state.value.pausedDownloads) "The series is paused — unpause it to download."
                    else "Please try again.",
                    destructive = true,
                )
            }
        _state.update { it.copy(downloadAllPending = false) }
        loadDownloads()
    }

    fun askDeleteDownloads(scope: DeleteDownloadsScope) = _state.update { it.copy(confirmDelete = scope) }
    fun dismissDeleteDownloads() = _state.update { it.copy(confirmDelete = null) }

    fun confirmDeleteDownloads() = viewModelScope.launch {
        val d = detail() ?: return@launch
        val scope = _state.value.confirmDelete ?: return@launch
        val numbers =
            if (scope == DeleteDownloadsScope.ALL) null else _state.value.selectedDownloadedNumbers
        if (scope == DeleteDownloadsScope.SELECTED && numbers.isNullOrEmpty()) {
            _state.update { it.copy(confirmDelete = null) }
            return@launch
        }
        _state.update { it.copy(deleteDownloadsPending = true) }
        runCatching { d.deleteDownloads(seriesId, DeleteDownloadsRequestDto(numbers)) }
            .onSuccess { res ->
                toast(
                    "Downloads deleted",
                    if (res.deleted > 0)
                        "Removed ${res.deleted} downloaded chapter${plural(res.deleted)} from disk."
                    else "No downloaded files were found to delete.",
                )
                if (scope == DeleteDownloadsScope.SELECTED) exitSelection()
            }
            .onFailure { toast("Couldn't delete downloads", "Please try again.", destructive = true) }
        _state.update { it.copy(deleteDownloadsPending = false, confirmDelete = null) }
        loadChapters()
    }

    // ──────────────────────────────────────────────────────────────────────
    // Offline (native-only) — unchanged download-service handoff
    // ──────────────────────────────────────────────────────────────────────

    /**
     * Queue chapters for offline download via the existing RenzoDownloadService —
     * the exact same JSON job payload the web bridge used to enqueue, now built
     * natively. Only server-downloaded chapters (filename != null) qualify:
     * page URLs come from /api/reader/page, and page counts must be known.
     */
    fun saveOffline(chapters: List<ChapterRowUi>) {
        val token = app.tokenStore.accessToken ?: return
        val base = app.tokenStore.serverUrl ?: return
        _state.update { it.copy(offlineSaving = it.offlineSaving + chapters.map { c -> c.number }) }
        viewModelScope.launch(Dispatchers.IO) {
            val a = api()
            if (a == null) {
                _state.update { s -> s.copy(offlineSaving = emptySet()) }
                return@launch
            }
            val jobChapters = JSONArray()
            for (ch in chapters) {
                val filename = ch.filename ?: continue
                val key = chapterKey(seriesId, ch.number)
                if (store.hasChapter(key)) continue
                val pageCount = ch.pageCount
                    ?: runCatching { a.chapterInfo(seriesId, encodeFilename(filename)).pageCount }.getOrNull()
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
            if (jobChapters.length() == 0) {
                _state.update { s -> s.copy(offlineSaving = emptySet()) }
                return@launch
            }

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
            _state.update { s -> s.copy(offlineSaving = emptySet()) }
            toast(
                "Saving offline",
                "${jobChapters.length()} chapter${plural(jobChapters.length())} queued to this device.",
            )
        }
    }

    fun markRead(numbers: List<Double>, read: Boolean) = viewModelScope.launch {
        val a = api() ?: return@launch
        runCatching { a.markChapters(ReaderMarkRequestDto(seriesId, numbers, read)) }
        loadChapters()
    }

    // ──────────────────────────────────────────────────────────────────────
    // Favourites + trackers (the hero's action row)
    // ──────────────────────────────────────────────────────────────────────

    fun toggleFavoriteList(listId: String, member: Boolean) = viewModelScope.launch {
        val d = detail() ?: return@launch
        val result = if (member) {
            runCatching { d.removeFavoriteItem(listId, seriesId) }
        } else {
            runCatching { d.addFavoriteItem(listId, FavoriteItemRequestDto(seriesId)) }
        }
        result.onFailure { toast("Failed to update favourites", destructive = true) }
        loadFavorites()
    }

    fun createFavoriteList(name: String, parentId: String? = null) = viewModelScope.launch {
        val d = detail() ?: return@launch
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return@launch
        runCatching { d.createFavoriteList(CreateFavoriteListDto(trimmed, parentId)) }
            .onFailure { toast("Failed to create list", destructive = true) }
        loadFavorites()
    }

    fun setTracking(enabled: Boolean) = viewModelScope.launch {
        val d = detail() ?: return@launch
        _state.update { it.copy(trackerBusy = true) }
        val result = runCatching {
            if (enabled) {
                d.autoMatchSeries(seriesId)
            } else {
                for (c in _state.value.activeTrackerLinks) {
                    d.disableTrackerLink(DisableLinkRequestDto(seriesId, c.provider))
                }
            }
        }
        result
            .onSuccess {
                toast(
                    if (enabled) "Looking for matches on your trackers…" else "Stopped tracking this series.",
                    if (enabled) "If nothing was linked, try again from the web app." else null,
                )
            }
            .onFailure { toast("Couldn't update tracking for this series.", destructive = true) }
        loadTrackers()
        _state.update { it.copy(trackerBusy = false) }
    }

    // ──────────────────────────────────────────────────────────────────────

    fun toast(title: String, description: String? = null, destructive: Boolean = false) {
        _state.update { it.copy(toast = ToastMessage(title, description, destructive)) }
    }

    fun dismissToast() = _state.update { it.copy(toast = null) }

    companion object {
        fun factory(application: Application, seriesId: String) = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T =
                SeriesDetailViewModel(application, seriesId) as T
        }
    }
}

/** "1 source" / "2 sources" — the web's `${n === 1 ? '' : 's'}`. */
private fun plural(n: Int): String = if (n == 1) "" else "s"

/** Whole numbers render without a trailing ".0" (matches the web's `${n}`). */
fun formatNumber(n: Double): String =
    if (n == n.toLong().toDouble()) n.toLong().toString() else n.toString()

private fun formatFromChapter(n: Double): String = formatNumber(n)
