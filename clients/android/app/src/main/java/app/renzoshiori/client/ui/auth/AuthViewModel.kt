package app.renzoshiori.client.ui.auth

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import app.renzoshiori.client.data.auth.TokenStore
import app.renzoshiori.client.data.model.ForgotPasswordRequestDto
import app.renzoshiori.client.data.model.ResetPasswordRequestDto
import app.renzoshiori.client.data.model.SetPasswordRequestDto
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.network.ApiService
import app.renzoshiori.client.data.network.AuthExtraApi
import app.renzoshiori.client.data.network.NetworkModule
import app.renzoshiori.client.data.network.serverErrorMessage
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

/**
 * State for the three public password flows (/auth/forgot-password,
 * /auth/reset-password, /auth/set-password). Kept separate from [AuthUiState]
 * so an in-flight reset can never fight with the login card's own
 * loading/error, exactly like the web app where each page owns its own
 * `pending`/`error`/`submitted` state.
 */
data class PasswordFlowState(
    val pending: Boolean = false,
    val error: String? = null,
    /** forgot-password: the generic "if that email is on file…" confirmation. */
    val emailSubmitted: Boolean = false,
    /** reset-password succeeded — the web navigates to /login?reset=1. */
    val resetDone: Boolean = false,
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

    private val _passwordFlow = MutableStateFlow(PasswordFlowState())
    val passwordFlow: StateFlow<PasswordFlowState> = _passwordFlow.asStateFlow()

    private var api: ApiService? = null

    init {
        // Any 401 outside the auth endpoints means the session is gone (expired
        // token, revoked session, server restarted). Land on the login screen
        // rather than leaving the user staring at an empty library with no
        // explanation. The server address is kept, so it's one tap to get back
        // in — and this collector lives for the app's lifetime, so it catches
        // an expiry on any screen, not just at startup.
        viewModelScope.launch {
            app.renzoshiori.client.data.auth.SessionEvents.expired.collect {
                if (_state.value.step is AuthStep.SignedIn) {
                    tokenStore.clearSession()
                    val serverUrl = tokenStore.serverUrl
                    val status = serverUrl?.let {
                        runCatching { network.apiFor(it).authStatus() }.getOrNull()
                    }
                    _state.value = AuthUiState(
                        step = if (serverUrl == null) {
                            AuthStep.Connect
                        } else {
                            AuthStep.Login(users = status?.users, serverUrl = serverUrl)
                        },
                        error = "Your session expired — please sign in again.",
                    )
                }
            }
        }
    }

    /** Retrofit client for the public password endpoints, bound to the connected server. */
    private fun authExtra(): AuthExtraApi? = network.currentServiceOf<AuthExtraApi>()

    /**
     * The web login page pre-fills `localStorage.renzo_remembered_username`;
     * the native equivalent is the encrypted TokenStore's last signed-in name,
     * which [login] keeps in sync with the Remember me checkbox.
     */
    val rememberedUsername: String? get() = tokenStore.lastUsername

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
                // Mirrors the web page's REMEMBERED_USER_KEY handling: the name
                // is remembered only while the box is ticked.
                tokenStore.lastUsername = if (rememberMe) username else null
                _passwordFlow.value = PasswordFlowState()
                _state.value = AuthUiState(step = AuthStep.SignedIn(resp.user))
            }.onFailure { e ->
                // Surface the server's own words — "Invalid credentials",
                // "User has no password set…", or the rate limiter's
                // "Too many failed attempts. Try again in about N minutes."
                _state.value = _state.value.copy(
                    loading = false,
                    error = e.serverErrorMessage("Login failed — check your username and password."),
                )
            }
        }
    }

    /**
     * No-password profile pick, only valid when the server has auth disabled.
     * POST /api/auth/select-user answers with a bare UserDto and issues no JWT
     * (see [AuthExtraApi]) — the profile is carried by the `X-Renzo-User`
     * header from then on, so nothing is written to the token slot here.
     */
    fun selectUser(username: String) {
        val extra = authExtra() ?: return
        _state.value = _state.value.copy(loading = true, error = null)
        viewModelScope.launch {
            runCatching {
                extra.selectUserProfile(app.renzoshiori.client.data.model.SelectUserRequestDto(username))
            }.onSuccess { user ->
                tokenStore.lastUsername = username
                _state.value = AuthUiState(step = AuthStep.SignedIn(user))
            }.onFailure { e ->
                _state.value = _state.value.copy(
                    loading = false,
                    error = e.serverErrorMessage("Couldn't sign in as $username."),
                )
            }
        }
    }

    // ── Public password flows (web: /auth/forgot-password, /auth/reset-password,
    //    /auth/set-password) ───────────────────────────────────────────────

    /** Clears any leftover pending/error/submitted state when a flow is left. */
    fun clearPasswordFlow() {
        _passwordFlow.value = PasswordFlowState()
    }

    /** POST /api/auth/forgot-password — always the same generic confirmation. */
    fun forgotPassword(email: String) {
        val extra = authExtra() ?: run {
            _passwordFlow.value = PasswordFlowState(error = "Not connected to a server.")
            return
        }
        _passwordFlow.value = _passwordFlow.value.copy(pending = true, error = null)
        viewModelScope.launch {
            runCatching { extra.forgotPassword(ForgotPasswordRequestDto(email.trim())) }
                .onSuccess { _passwordFlow.value = PasswordFlowState(emailSubmitted = true) }
                .onFailure { e ->
                    _passwordFlow.value = PasswordFlowState(
                        error = e.serverErrorMessage("Something went wrong. Try again."),
                    )
                }
        }
    }

    /** POST /api/auth/reset-password — token identifies the account, no username. */
    fun resetPassword(token: String, newPassword: String) {
        val extra = authExtra() ?: run {
            _passwordFlow.value = PasswordFlowState(error = "Not connected to a server.")
            return
        }
        _passwordFlow.value = _passwordFlow.value.copy(pending = true, error = null)
        viewModelScope.launch {
            runCatching { extra.resetPassword(ResetPasswordRequestDto(token.trim(), newPassword)) }
                .onSuccess { _passwordFlow.value = PasswordFlowState(resetDone = true) }
                .onFailure { e ->
                    _passwordFlow.value = PasswordFlowState(
                        error = e.serverErrorMessage("Invalid or expired reset link. Request a new one."),
                    )
                }
        }
    }

    /**
     * POST /api/auth/set-password — the invite flow. Returns a real session, so
     * this signs the user straight in, exactly like the web page's
     * setAuthFromToken(...) + router.push('/library').
     */
    fun setPassword(username: String, token: String, password: String) {
        val extra = authExtra() ?: run {
            _passwordFlow.value = PasswordFlowState(error = "Not connected to a server.")
            return
        }
        _passwordFlow.value = _passwordFlow.value.copy(pending = true, error = null)
        viewModelScope.launch {
            runCatching {
                extra.setPassword(SetPasswordRequestDto(username.trim(), token.trim(), password))
            }.onSuccess { resp ->
                tokenStore.accessToken = resp.token
                tokenStore.lastUsername = resp.user.username
                _passwordFlow.value = PasswordFlowState()
                _state.value = AuthUiState(step = AuthStep.SignedIn(resp.user))
            }.onFailure { e ->
                _passwordFlow.value = PasswordFlowState(
                    error = e.serverErrorMessage("Failed to set password"),
                )
            }
        }
    }

    /**
     * Sign out. POST /api/auth/logout first so the server revokes the refresh
     * token (a client-only clear used to leave it valid in the DB), then drop
     * the local token. The server address is kept, so this lands back on the
     * Login/profile step — the web's logout() goes to /login or /user-select,
     * never back to "which server?".
     */
    fun logout() {
        val serverUrl = tokenStore.serverUrl
        val currentApi = api
        viewModelScope.launch {
            runCatching { currentApi?.logout() }
            tokenStore.clearSession()
            _passwordFlow.value = PasswordFlowState()
            if (serverUrl == null) {
                _state.value = AuthUiState(step = AuthStep.Connect)
            } else {
                val status = runCatching { network.apiFor(serverUrl).authStatus() }.getOrNull()
                _state.value = AuthUiState(
                    step = AuthStep.Login(users = status?.users, serverUrl = serverUrl),
                )
            }
        }
    }

    companion object {
        fun factory(application: Application) = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T = AuthViewModel(application) as T
        }
    }
}
