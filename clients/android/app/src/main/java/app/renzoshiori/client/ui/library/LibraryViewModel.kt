package app.renzoshiori.client.ui.library

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.FavoriteListDto
import app.renzoshiori.client.data.model.LibraryRowDto
import app.renzoshiori.client.data.model.ScrobblerConfigLiteDto
import app.renzoshiori.client.data.model.SettingsLiteDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.data.network.LibraryExtrasApi
import app.renzoshiori.client.data.offline.OfflineRepository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class LibraryUiState(
    val loading: Boolean = true,
    val error: String? = null,
    /** Browsing the on-device offline library instead of the live server one. */
    val offlineMode: Boolean = false,
    val series: List<LibraryRowDto> = emptyList(),
    val offlineSeries: List<OfflineRepository.OfflineSeries> = emptyList(),
    val searchTerm: String = "",
    /** Owner-only display preference — every user's library instead of just mine. */
    val viewAllLibraries: Boolean = false,
    val canOwner: Boolean = false,
    /** Manager+ — gates the Add Series wording ("Request Series" otherwise). */
    val canAddSeries: Boolean = false,
    val favoriteLists: List<FavoriteListDto> = emptyList(),
    val settings: SettingsLiteDto? = null,
    /** Connected trackers — the Track-all chip self-hides when empty. */
    val connectedTrackers: List<ScrobblerConfigLiteDto> = emptyList(),
    val trackingAll: Boolean = false,
    /** Transient banner, e.g. the Track-all confirmation/error toast text. */
    val toast: String? = null,
)

class LibraryViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as RenzoApp

    private val _state = MutableStateFlow(LibraryUiState())
    val state: StateFlow<LibraryUiState> = _state.asStateFlow()

    val baseUrl: String get() = app.tokenStore.serverUrl ?: ""

    private fun extras(): LibraryExtrasApi? = app.network.currentServiceOf<LibraryExtrasApi>()

    init {
        refresh()
        loadSideData()
    }

    fun refresh() {
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            if (_state.value.offlineMode) {
                loadOffline()
            } else {
                val api = extras()
                if (api == null) {
                    _state.value = _state.value.copy(loading = false, error = "Not connected")
                    return@launch
                }
                runCatching { api.library(_state.value.viewAllLibraries) }
                    .onSuccess { rows ->
                        // Deduplicate by id — the web page does the same before rendering.
                        val seen = HashSet<String>()
                        val unique = rows.filter { seen.add(it.id) }
                        _state.value = _state.value.copy(loading = false, series = unique, error = null)
                    }
                    .onFailure {
                        // Server unreachable — fall back to offline browsing
                        // automatically (same "auto" behavior as the web pill).
                        loadOffline(autoFellBack = true)
                    }
            }
        }
    }

    /** Permissions, favourites, settings and trackers — everything the ribbon shows. */
    private fun loadSideData() {
        viewModelScope.launch {
            runCatching { app.network.currentApi()?.me() }.getOrNull()?.let { me ->
                _state.value = _state.value.copy(
                    canOwner = me.level >= UserLevel.OWNER,
                    canAddSeries = me.level >= UserLevel.MANAGER,
                )
            }
            val api = extras() ?: return@launch
            runCatching { api.favorites() }.getOrNull()?.let {
                _state.value = _state.value.copy(favoriteLists = it)
            }
            runCatching { api.settings() }.getOrNull()?.let {
                _state.value = _state.value.copy(settings = it)
            }
            runCatching { api.scrobblerConfigs() }.getOrNull()?.let { configs ->
                _state.value = _state.value.copy(connectedTrackers = configs.filter { it.isConnected })
            }
        }
    }

    private suspend fun loadOffline(autoFellBack: Boolean = false) {
        val offline = withContext(Dispatchers.IO) { app.offline.listSeries() }
        _state.value = _state.value.copy(
            loading = false,
            offlineMode = true,
            offlineSeries = offline,
            error = if (autoFellBack && offline.isEmpty()) "Server unreachable and nothing saved offline." else null,
        )
    }

    fun setOfflineMode(offline: Boolean) {
        _state.value = _state.value.copy(offlineMode = offline)
        refresh()
    }

    fun setSearch(term: String) {
        _state.value = _state.value.copy(searchTerm = term)
    }

    fun setViewAllLibraries(viewAll: Boolean) {
        _state.value = _state.value.copy(viewAllLibraries = viewAll)
        refresh()
    }

    /** Track all — auto-matches the whole library on every connected tracker. */
    fun trackAll() {
        val connected = _state.value.connectedTrackers
        if (connected.isEmpty() || _state.value.trackingAll) return
        _state.value = _state.value.copy(trackingAll = true)
        viewModelScope.launch {
            val api = extras()
            // Report what actually went wrong: a non-2xx never throws through
            // Retrofit's Response<T>, so it has to be checked explicitly, and
            // a timeout/connection drop should not read the same as a refusal.
            var failure: String? = null
            runCatching {
                connected.forEach { tracker ->
                    val response = api?.autoMatchAll(tracker.provider)
                    if (response != null && !response.isSuccessful && failure == null) {
                        failure = "${tracker.displayName}: HTTP ${response.code()}"
                    }
                }
            }.onFailure { e ->
                failure = when (e) {
                    is java.net.SocketTimeoutException -> "the server is still working — give it a minute and check Trackers"
                    is java.io.IOException -> "can't reach the server"
                    else -> e.message?.take(120) ?: "unknown error"
                }
            }
            val ok = failure == null
            _state.value = _state.value.copy(
                trackingAll = false,
                toast = if (ok) "Tracking all your series…" else "Couldn't track all series — $failure",
            )
        }
    }

    fun clearToast() {
        _state.value = _state.value.copy(toast = null)
    }

    companion object {
        fun factory(application: Application) = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T = LibraryViewModel(application) as T
        }
    }
}
