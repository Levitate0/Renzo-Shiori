package app.renzoshiori.client.ui.settings

import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.ChangePasswordRequestDto
import app.renzoshiori.client.data.network.AccountApi
import kotlinx.coroutines.launch

/**
 * Self-service password change — transliterated from
 * RenzoFrontend/src/components/comp/users/change-password-dialog.tsx.
 *
 * The ≥8-character and confirm-match checks are client-side niceties; the
 * backend re-verifies the current password and rate-limits attempts, and
 * changing it signs out this account's other sessions.
 */
@Composable
fun ChangePasswordDialog(onDismiss: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()

    var currentPassword by remember { mutableStateOf("") }
    var newPassword by remember { mutableStateOf("") }
    var confirmPassword by remember { mutableStateOf("") }
    var pending by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf("") }
    var done by remember { mutableStateOf(false) }

    RenzoDialog(
        onDismiss = onDismiss,
        title = "Change Password",
        description = "Enter your current password, then choose a new one.",
    ) {
        if (error.isNotEmpty()) ErrorBox(error)
        if (done) SuccessBox("Password changed")

        LabelledField("Current password", currentPassword, { currentPassword = it }, password = true)
        LabelledField("New password", newPassword, { newPassword = it }, password = true, hint = "At least 8 characters")
        LabelledField("Confirm new password", confirmPassword, { confirmPassword = it }, password = true)

        Spacer(Modifier.height(4.dp))
        RenzoButton(
            text = if (pending) "Changing…" else "Change password",
            busy = pending,
            modifier = Modifier.fillMaxWidth(),
            onClick = {
                error = ""
                done = false
                when {
                    newPassword.length < 8 -> error = "New password must be at least 8 characters"
                    newPassword != confirmPassword -> error = "Passwords do not match"
                    else -> {
                        pending = true
                        scope.launch {
                            val api = app.network.currentServiceOf<AccountApi>()
                            runCatching {
                                api?.changePassword(ChangePasswordRequestDto(currentPassword, newPassword))
                            }
                                .onSuccess { response ->
                                    val serverError = response?.error
                                    if (response?.success == false && !serverError.isNullOrBlank()) {
                                        error = serverError
                                    } else {
                                        done = true
                                        currentPassword = ""
                                        newPassword = ""
                                        confirmPassword = ""
                                    }
                                }
                                .onFailure { error = it.apiMessage("Failed to change password") }
                            pending = false
                        }
                    }
                }
            },
        )
    }
}
