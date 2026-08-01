package app.renzoshiori.client.ui.home

import androidx.compose.runtime.Composable
import app.renzoshiori.client.ui.settings.ChangePasswordDialog
import app.renzoshiori.client.ui.settings.EditProfileDialog
import app.renzoshiori.client.ui.settings.ImportBackupDialog

/**
 * The three account-menu entries the web opens as dialogs rather than pages
 * (user-menu.tsx renders EditUserDialog / ChangePasswordDialog /
 * ImportBackupDialog inline). Everything else in that menu is a route.
 */
enum class AccountDialog { EditProfile, ChangePassword, ImportBackup }

@Composable
fun AccountDialogHost(dialog: AccountDialog?, onDismiss: () -> Unit) {
    when (dialog) {
        AccountDialog.EditProfile -> EditProfileDialog(onDismiss = onDismiss)
        AccountDialog.ChangePassword -> ChangePasswordDialog(onDismiss = onDismiss)
        AccountDialog.ImportBackup -> ImportBackupDialog(onDismiss = onDismiss)
        null -> Unit
    }
}
