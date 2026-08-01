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
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp

/**
 * RenzoFrontend/src/app/auth/forgot-password/page.tsx — email in, generic
 * "if that address is on file" confirmation out (the endpoint deliberately
 * never reveals whether the account exists).
 *
 * [onHaveResetLink] is the one native-only addition: the emailed link opens a
 * browser, not this app, so the reset page needs a door that a URL can't
 * provide here.
 */
@Composable
fun ForgotPasswordScreen(
    state: PasswordFlowState,
    onSubmit: (String) -> Unit,
    onHaveResetLink: () -> Unit,
    onBackToLogin: () -> Unit,
) {
    var email by remember { mutableStateOf("") }
    var localError by remember { mutableStateOf<String?>(null) }

    fun submit() {
        if (email.isBlank()) {
            localError = "Enter your email address."
            return
        }
        localError = null
        onSubmit(email.trim())
    }
    val shownError = localError ?: state.error

    AuthPageScaffold {
        AuthCard {
            AuthCardHeader(spacing = 4.dp) {
                AuthCardTitle("Forgot Password")
                AuthCardDescription("Enter your account's email address and we'll send you a reset link.")
            }
            AuthCardContent {
                if (state.emailSubmitted) {
                    AuthNoticeBox(
                        "If that email address is on file, a reset link has been sent. " +
                            "The link expires in 60 minutes — check your spam folder too.",
                    )
                    Spacer(Modifier.height(16.dp))
                    AuthOutlineButton("Back to login", onBackToLogin)
                    Spacer(Modifier.height(16.dp))
                    AuthLinkRow("I have the reset link", onHaveResetLink)
                } else {
                    if (shownError != null) {
                        AuthErrorBox(shownError)
                        Spacer(Modifier.height(16.dp))
                    }
                    AuthLabel("Email")
                    Spacer(Modifier.height(8.dp))
                    AuthInput(
                        value = email,
                        onValueChange = { email = it; localError = null },
                        placeholder = "Enter your email address",
                        keyboardType = KeyboardType.Email,
                        imeAction = ImeAction.Go,
                        onImeAction = { submit() },
                    )
                    Spacer(Modifier.height(16.dp))
                    AuthPrimaryButton(
                        text = if (state.pending) "Sending…" else "Send reset link",
                        onClick = { submit() },
                        enabled = !state.pending,
                    )
                    Spacer(Modifier.height(16.dp))
                    AuthLinkRow("Back to login", onBackToLogin)
                    AuthLinkRow("I already have the reset link", onHaveResetLink)
                }
            }
        }
    }
}
