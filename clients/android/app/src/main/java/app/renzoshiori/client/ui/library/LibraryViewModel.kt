package app.renzoshiori.client.ui.library

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.SeriesInfoDto
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
    val series: List<SeriesInfoDto> = emptyList(),
    val offlineSeries: List<OfflineRepository.OfflineSeries> = emptyList(),
    val searchTerm: String = "",
)

class LibraryViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as RenzoApp

    private val _state = MutableStateFlow(LibraryUiState())
    val state: StateFlow<LibraryUiState> = _state.asStateFlow()

    val baseUrl: String get() = app.tokenStore.serverUrl ?: ""

    init {
        refresh()
    }

    fun refresh() {
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            if (_state.value.offlineMode) {
                loadOffline()
            } else {
                val api = app.network.currentApi()
                if (api == null) {
                    _state.value = _state.value.copy(loading = false, error = "Not connected")
                    return@launch
                }
                runCatching { api.library() }
                    .onSuccess { _state.value = _state.value.copy(loading = false, series = it) }
                    .onFailure { e ->
                        // Server unreachable — fall back to offline browsing
                        // automatically (same "auto" behavior as the web pill).
                        loadOffline(autoFellBack = true)
                    }
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

    companion object {
        fun factory(application: Application) = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T = LibraryViewModel(application) as T
        }
    }
}
