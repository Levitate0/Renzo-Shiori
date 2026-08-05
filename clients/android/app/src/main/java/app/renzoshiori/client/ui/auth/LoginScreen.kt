package app.renzoshiori.client.ui.auth

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.widthIn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.R
import app.renzoshiori.client.ui.tv.LocalIsTv

/**
 * The signed-out half of the app, ported 1:1 from RenzoFrontend:
 *
 *  - `/login`                  → [LoginCard]           (this file)
 *  - `/user-select`            → [UserSelectScreen]    (UserSelectScreen.kt)
 *  - `/auth/forgot-password`   → [ForgotPasswordScreen]
 *  - `/auth/reset-password`    → [ResetPasswordScreen]
 *  - `/auth/set-password`      → [SetPasswordScreen]
 *
 * The web routes between these with next/navigation; there is no NavHost this
 * side of sign-in (MainActivity only mounts one auth composable), so the same
 * five destinations are switched here by [AuthRoute].
 */
internal enum class AuthRoute { Root, ForgotPassword, ResetPassword, SetPassword }

@Composable
fun LoginScreen(
    step: AuthStep.Login,
    loading: Boolean,
    error: String?,
    onLogin: (username: String, password: String, rememberMe: Boolean) -> Unit,
    onSelectUser: (String) -> Unit,
) {
    // Same activity-scoped instance MainActivity holds (default ViewModel key),
    // so the password flows below share the connected server and can hand the
    // finished session straight back to the auth gate.
    val vm: AuthViewModel = viewModel()
    var route by remember { mutableStateOf(AuthRoute.Root) }
    // The web lands back on /login?reset=1 after a successful reset and shows a
    // confirmation note above the form.
    var resetDone by remember { mutableStateOf(false) }

    val flow by vm.passwordFlow.collectAsState()
    // Non-null exactly when the server has auth disabled and has users.
    val profiles = step.users

    LaunchedEffect(flow.resetDone) {
        if (flow.resetDone) {
            resetDone = true
            route = AuthRoute.Root
            vm.clearPasswordFlow()
        }
    }

    when (route) {
        AuthRoute.Root -> if (profiles != null) {
            // Auth disabled + users exist: the server hands back the profile
            // list, which is exactly when the web app shows /user-select.
            UserSelectScreen(
                users = profiles,
                loading = loading,
                error = error,
                onSelectUser = onSelectUser,
            )
        } else {
            LoginCard(
                loading = loading,
                error = error,
                resetDone = resetDone,
                rememberedUsername = vm.rememberedUsername,
                onLogin = onLogin,
                onForgotPassword = { vm.clearPasswordFlow(); route = AuthRoute.ForgotPassword },
                onHaveInvite = { vm.clearPasswordFlow(); route = AuthRoute.SetPassword },
            )
        }

        AuthRoute.ForgotPassword -> ForgotPasswordScreen(
            state = flow,
            onSubmit = vm::forgotPassword,
            onHaveResetLink = { vm.clearPasswordFlow(); route = AuthRoute.ResetPassword },
            onBackToLogin = { vm.clearPasswordFlow(); route = AuthRoute.Root },
        )

        AuthRoute.ResetPassword -> ResetPasswordScreen(
            state = flow,
            onSubmit = vm::resetPassword,
            onBackToLogin = { vm.clearPasswordFlow(); route = AuthRoute.Root },
        )

        AuthRoute.SetPassword -> SetPasswordScreen(
            state = flow,
            onSubmit = vm::setPassword,
            onBackToLogin = { vm.clearPasswordFlow(); route = AuthRoute.Root },
        )
    }
}

/**
 * RenzoFrontend/src/app/login/page.tsx — banner, "Enter your credentials to log
 * in", username + password, Remember me (default on, pre-filled from the
 * remembered username), full-width primary "Log in", "Forgot password?".
 */
@Composable
private fun LoginCard(
    loading: Boolean,
    error: String?,
    resetDone: Boolean,
    rememberedUsername: String?,
    onLogin: (String, String, Boolean) -> Unit,
    onForgotPassword: () -> Unit,
    onHaveInvite: () -> Unit,
) {
    var username by remember { mutableStateOf(rememberedUsername.orEmpty()) }
    var password by remember { mutableStateOf("") }
    // Default on: staying signed in until explicit logout is the expected
    // behavior for a personal/installed app; unchecking opts into a 24h session.
    var rememberMe by remember { mutableStateOf(true) }
    var localError by remember { mutableStateOf<String?>(null) }

    // With a remote there's no tap to place the cursor, so land on the first
    // field. Password then follows on ImeAction.Next and Go submits.
    val isTv = LocalIsTv.current
    val usernameRequester = remember { FocusRequester() }
    LaunchedEffect(isTv) {
        if (isTv) runCatching { usernameRequester.requestFocus() }
    }

    fun submit() {
        if (username.isBlank() || password.isBlank()) {
            // The web form relies on the browser's `required` validation here.
            localError = "Enter your username and password."
            return
        }
        localError = null
        onLogin(username, password, rememberMe)
    }
    val shownError = localError ?: error

    AuthPageScaffold {
        AuthCard {
            AuthCardHeader(spacing = 12.dp) {
                Image(
                    painter = painterResource(R.drawable.renzo_login_banner),
                    contentDescription = "Renzo Shiori",
                    contentScale = ContentScale.FillWidth,
                    modifier = Modifier.widthIn(max = 256.dp).fillMaxWidth(),
                )
                AuthCardDescription("Enter your credentials to log in")
            }
            AuthCardContent {
                if (resetDone && shownError == null) {
                    AuthNoticeBox("Password reset successful. Log in with your new password.")
                    Spacer(Modifier.height(16.dp))
                }
                if (shownError != null) {
                    AuthErrorBox(shownError)
                    Spacer(Modifier.height(16.dp))
                }

                AuthLabel("Username")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = username,
                    onValueChange = { username = it; localError = null },
                    placeholder = "Enter your username",
                    imeAction = ImeAction.Next,
                    modifier = Modifier.focusRequester(usernameRequester),
                )
                Spacer(Modifier.height(16.dp))

                AuthLabel("Password")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = password,
                    onValueChange = { password = it; localError = null },
                    placeholder = "Enter your password",
                    isPassword = true,
                    imeAction = ImeAction.Go,
                    onImeAction = { submit() },
                )
                Spacer(Modifier.height(16.dp))

                AuthCheckboxRow(
                    checked = rememberMe,
                    onCheckedChange = { rememberMe = it },
                    label = "Remember me",
                )
                Spacer(Modifier.height(16.dp))

                AuthPrimaryButton(
                    text = if (loading) "Logging in..." else "Log in",
                    onClick = { submit() },
                    enabled = !loading,
                )
                Spacer(Modifier.height(16.dp))

                AuthLinkRow("Forgot password?", onForgotPassword)
                // No email client can hand a token to this app the way a
                // browser link does, so the invite (set-password) flow needs a
                // door of its own here.
                AuthLinkRow("Have an invite link? Set your password", onHaveInvite)
            }
        }
    }
}
