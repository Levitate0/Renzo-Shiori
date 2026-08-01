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
 * RenzoFrontend/src/app/auth/set-password/page.tsx — the invite flow. The web
 * page takes `?username=…&token=…` from the invite email and shows an
 * "Invalid Link" card without them; on a phone the same two values are pasted
 * instead, and pasting the whole invite URL fills both at once.
 *
 * Success returns a real session, so the ViewModel signs straight in — the web
 * equivalent of setAuthFromToken(result) + router.push('/library').
 */
@Composable
fun SetPasswordScreen(
    state: PasswordFlowState,
    onSubmit: (username: String, token: String, password: String) -> Unit,
    onBackToLogin: () -> Unit,
) {
    var username by remember { mutableStateOf("") }
    var token by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var confirmPassword by remember { mutableStateOf("") }
    var localError by remember { mutableStateOf<String?>(null) }

    fun submit() {
        val cleanToken = extractQueryValue(token, "token")
        if (username.isBlank() || cleanToken.isBlank()) {
            localError = "Paste your invitation link (or fill in both the username and token)."
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
        onSubmit(username.trim(), cleanToken, password)
    }
    val shownError = localError ?: state.error

    AuthPageScaffold {
        AuthCard {
            AuthCardHeader(spacing = 4.dp) {
                AuthCardTitle("Set Your Password")
                AuthCardDescription(
                    if (username.isBlank()) {
                        "Please set your password to continue."
                    } else {
                        "Hello ${username.trim()}! Please set your password to continue."
                    },
                )
            }
            AuthCardContent {
                if (shownError != null) {
                    AuthErrorBox(shownError)
                    Spacer(Modifier.height(16.dp))
                }

                AuthLabel("Username")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = username,
                    onValueChange = { username = it; localError = null },
                    placeholder = "Your username",
                    imeAction = ImeAction.Next,
                )
                Spacer(Modifier.height(16.dp))

                AuthLabel("Invitation link or token")
                Spacer(Modifier.height(8.dp))
                AuthInput(
                    value = token,
                    onValueChange = { pasted ->
                        token = pasted
                        localError = null
                        // A pasted invite URL carries the username too — take it
                        // so the two fields can't drift apart.
                        if (pasted.contains("username=")) {
                            val fromLink = extractQueryValue(pasted, "username")
                            if (fromLink.isNotBlank() && fromLink != pasted) {
                                username = runCatching {
                                    java.net.URLDecoder.decode(fromLink, "UTF-8")
                                }.getOrDefault(fromLink)
                            }
                        }
                    },
                    placeholder = "Paste the link from your invitation email",
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
                    text = if (state.pending) "Setting password..." else "Set Password & Log In",
                    onClick = { submit() },
                    enabled = !state.pending,
                )
                Spacer(Modifier.height(16.dp))
                AuthLinkRow("Back to login", onBackToLogin)
            }
        }
    }
}
