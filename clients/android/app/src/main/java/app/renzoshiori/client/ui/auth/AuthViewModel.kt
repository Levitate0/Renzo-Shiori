package app.renzoshiori.client.ui.auth

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import app.renzoshiori.client.data.auth.TokenStore
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.network.ApiService
import app.renzoshiori.client.data.network.NetworkModule
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

sealed interface AuthStep {
    data object Connect : AuthStep
    data class Login(val users: List<UserDto>?, val serverUrl: String) : AuthStep
    data class SignedIn(val user: UserDto) : AuthStep
}

data class AuthUiState(
    val step: AuthStep = AuthStep.Connect,
    val loading: Boolean = false,
    val error: String? = null,
)

/** Known product-name aliases a server may report — mirrors the old WebView
 * shell's isRenzoServer() check, including legacy brand names. */
private val KNOWN_PRODUCTS = setOf("renzo shiori", "renzo", "rensaio")

class AuthViewModel(application: Application) : AndroidViewModel(application) {
    // Shared singletons from RenzoApp — the SAME TokenStore/NetworkModule the
    // rest of the app (Library/Series/Reader ViewModels, Coil) uses, so the
    // refresh cookie jar and token state can never diverge between screens.
    private val tokenStore = (application as app.renzoshiori.client.RenzoApp).tokenStore
    private val network = (application as app.renzoshiori.client.RenzoApp).network

    private val _state = MutableStateFlow(AuthUiState())
    val state: StateFlow<AuthUiState> = _state.asStateFlow()

    private var api: ApiService? = null

    init {
        // Already connected + logged in from a previous launch? Skip straight
        // to Library. Only a definite 401/403 (expired/revoked token) drops
        // the session — any other failure (offline, server briefly down) must
        // NOT kick the user back to Connect: proceed signed-in with what we
        // know locally so the offline library still opens on a plane.
        val savedServer = tokenStore.serverUrl
        val savedToken = tokenStore.accessToken
        if (savedServer != null && savedToken != null) {
            api = network.apiFor(savedServer)
            viewModelScope.launch {
                runCatching { api!!.me() }
                    .onSuccess { _state.value = AuthUiState(step = AuthStep.SignedIn(it)) }
                    .onFailure { e ->
                        val code = (e as? retrofit2.HttpException)?.code()
                        if (code == 401 || code == 403) {
                            tokenStore.clearSession()
                        } else {
                            _state.value = AuthUiState(
                                step = AuthStep.SignedIn(offlinePlaceholderUser(tokenStore.lastUsername)),
                            )
                        }
                    }
            }
        }
    }

    private fun offlinePlaceholderUser(username: String?): UserDto = UserDto(
        id = "",
        username = username ?: "you",
        level = app.renzoshiori.client.data.model.UserLevel.USER,
        opdsPath = "",
        createdAt = "",
        isActive = true,
        hasPassword = true,
    )

    fun connect(rawAddress: String) {
        val trimmed = rawAddress.trim().trimEnd('/')
        if (trimmed.isEmpty()) return
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            val candidates = if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) {
                listOf(trimmed)
            } else {
                listOf("https://$trimmed", "http://$trimmed")
            }
            for (candidate in candidates) {
                val candidateApi = network.apiFor(candidate)
                val info = runCatching { candidateApi.systemInfo() }.getOrNull()
                if (info != null && info.product.lowercase() in KNOWN_PRODUCTS) {
                    tokenStore.serverUrl = candidate
                    api = candidateApi
                    proceedPastConnect(candidate)
                    return@launch
                }
            }
            _state.value = _state.value.copy(loading = false, error = "Couldn't find a Renzo Shiori server at that address.")
        }
    }

    private suspend fun proceedPastConnect(serverUrl: String) {
        val status = runCatching { api!!.authStatus() }.getOrNull()
        if (status == null) {
            _state.value = _state.value.copy(loading = false, error = "Connected, but couldn't read server status.")
            return
        }
        _state.value = AuthUiState(step = AuthStep.Login(users = status.users, serverUrl = serverUrl))
    }

    fun login(username: String, password: String, rememberMe: Boolean) {
        val currentApi = api ?: return
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            runCatching {
                currentApi.login(
                    app.renzoshiori.client.data.model.LoginRequestDto(username, password, rememberMe),
                )
            }.onSuccess { resp ->
                tokenStore.accessToken = resp.token
                tokenStore.lastUsername = username
                _state.value = AuthUiState(step = AuthStep.SignedIn(resp.user))
            }.onFailure {
                _state.value = _state.value.copy(loading = false, error = "Login failed — check your username and password.")
            }
        }
    }

    /** No-password profile pick, only valid when the server has auth disabled. */
    fun selectUser(username: String) {
        val currentApi = api ?: return
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            runCatching {
                currentApi.selectUser(app.renzoshiori.client.data.model.SelectUserRequestDto(username))
            }.onSuccess { resp ->
                tokenStore.accessToken = resp.token
                tokenStore.lastUsername = username
                _state.value = AuthUiState(step = AuthStep.SignedIn(resp.user))
            }.onFailure {
                _state.value = _state.value.copy(loading = false, error = "Couldn't sign in as $username.")
            }
        }
    }

    fun logout() {
        tokenStore.clearSession()
        _state.value = AuthUiState(step = AuthStep.Connect)
    }

    companion object {
        fun factory(application: Application) = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T = AuthViewModel(application) as T
        }
    }
}
