package app.renzoshiori.client.ui.auth

import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp

/**
 * RenzoFrontend/src/app/auth/reset-password/page.tsx — new password + confirm,
 * with the same client-side rules the web form applies before it posts
 * (min 8 characters, both fields equal); everything else comes back from the
 * server verbatim.
 *
 * The web page reads its token from `?token=` and shows an "Invalid Link" card
 * when it's missing. A phone has no such query string — the emailed link opens
 * a browser — so the token is a pasted field here instead, and the whole
 * copied URL is accepted: [extractQueryValue] lifts the token out of it.
 */
@Composable
fun ResetPasswordScreen(
    state: PasswordFlowState,
    onSubmit: (token: String, newPassword: String) -> Unit,
    onBackToLogin: () -> Unit,
) {
    var token by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var confirmPassword by remember { mutableStateOf("") }
    var localError by remember { mutableStateOf<String?>(null) }

    fun submit() {
        val cleanToken = extractQueryValue(token, "token")
        if (cleanToken.isBlank()) {
            localError = "Paste the reset link (or the token from it) to continue."
            return
        }
        // The same two checks the web form runs before it posts.
        if (password.length < 8) {
            localError = "Password must be at least 8 characters"
            return
        }
        if (password != confirmPassword) {
            localError = "Passwords do not match"
            return
        }
        localError = null
        onSubmit(cleanToken, password)
    }
    val shownError = localError ?: state.error

    AuthPageScaffold {
        AuthCard {
            AuthCardHeader(spacing = 4.dp) {
                AuthCardTitle("Reset Password")
                AuthCardDescription("Choose a new password for your account.")
            }
            AuthCardContent {
                if (shownError != null) {
                    AuthErrorBox(shownError)
                    Spacer(Modifier.height(16.dp))
                }

                AuthLabel("Reset link or token")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = token,
                    onValueChange = { token = it; localError = null },
                    placeholder = "Paste the link from your reset email",
                    imeAction = ImeAction.Next,
                )
                Spacer(Modifier.height(16.dp))

                AuthLabel("New Password")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = password,
                    onValueChange = { password = it; localError = null },
                    placeholder = "Enter your new password",
                    isPassword = true,
                    imeAction = ImeAction.Next,
                )
                Spacer(Modifier.height(16.dp))

                AuthLabel("Confirm Password")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = confirmPassword,
                    onValueChange = { confirmPassword = it; localError = null },
                    placeholder = "Confirm your new password",
                    isPassword = true,
                    imeAction = ImeAction.Go,
                    onImeAction = { submit() },
                )
                Spacer(Modifier.height(16.dp))

                AuthPrimaryButton(
                    text = if (state.pending) "Resetting…" else "Reset Password",
                    onClick = { submit() },
                    enabled = !state.pending,
                )
                Spacer(Modifier.height(16.dp))
                AuthLinkRow("Back to login", onBackToLogin)
            }
        }
    }
}
