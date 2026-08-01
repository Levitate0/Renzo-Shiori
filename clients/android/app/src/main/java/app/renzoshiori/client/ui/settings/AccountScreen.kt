package app.renzoshiori.client.ui.settings

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Mail
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material.icons.filled.VpnKey
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.RenzoStore
import app.renzoshiori.client.data.model.ChangePasswordRequestDto
import app.renzoshiori.client.data.model.UpdateUserDto
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.network.AccountApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch

/**
 * Per-user Account page — transliterated from
 * RenzoFrontend/src/app/account/page.tsx.
 *
 * Same three sections behind the same section nav (Account / Site Logins /
 * Scrobbler), with the web's Profile picture and Security cards inline under
 * "Account". Everything here only ever reads or writes the caller's own data.
 *
 * The one addition over the web is the offline download folder, which is a
 * native-only concern (SAF tree) and has always lived on this screen, plus the
 * Sign out button the shell hands us via [onLogout].
 */
@Composable
fun AccountScreen(
    username: String,
    onBack: () -> Unit,
    onLogout: () -> Unit,
) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }

    var user by remember { mutableStateOf<UserDto?>(null) }
    var activeSection by remember { mutableStateOf("account") }

    // ── Profile picture ─────────────────────────────────────────────────
    var avatarBase64 by remember { mutableStateOf<String?>(null) }
    var avatarContentType by remember { mutableStateOf<String?>(null) }
    var previewBase64 by remember { mutableStateOf<String?>(null) }
    var gravatarEmail by remember { mutableStateOf("") }
    var avatarError by remember { mutableStateOf("") }
    var savingAvatar by remember { mutableStateOf(false) }

    // ── Email + password ────────────────────────────────────────────────
    var email by remember { mutableStateOf("") }
    var savingEmail by remember { mutableStateOf(false) }
    var currentPassword by remember { mutableStateOf("") }
    var newPassword by remember { mutableStateOf("") }
    var confirmPassword by remember { mutableStateOf("") }
    var passwordError by remember { mutableStateOf("") }
    var savingPassword by remember { mutableStateOf(false) }

    // ── Native-only: offline download folder ────────────────────────────
    val store = remember { RenzoStore(context.applicationContext) }
    var folderLabel by remember { mutableStateOf(store.folderLabel()) }
    val folderPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocumentTree(),
    ) { uri: Uri? ->
        if (uri != null) {
            context.contentResolver.takePersistableUriPermission(
                uri,
                android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION or
                    android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
            store.setFolder(uri)
            folderLabel = store.folderLabel()
        }
    }

    suspend fun reload() {
        runCatching { app.network.currentServiceOf<AccountApi>()?.me() }
            .onSuccess {
                user = it
                previewBase64 = it?.avatarBase64
                email = it?.email ?: ""
            }
    }

    LaunchedEffect(Unit) { reload() }

    val avatarPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument(),
    ) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                when (val loaded = readPickedAvatar(context, uri)) {
                    is AvatarLoad.Ok -> {
                        avatarBase64 = loaded.base64
                        avatarContentType = loaded.contentType
                        previewBase64 = loaded.base64
                        avatarError = ""
                    }
                    is AvatarLoad.Failed -> avatarError = loaded.message
                }
            }
        }
    }

    val sections = listOf(
        SettingsNavSection("account", "Account", Icons.Filled.Person),
        SettingsNavSection("site-logins", "Site Logins", Icons.Filled.VpnKey),
        SettingsNavSection("scrobbler", "Scrobbler", Icons.Filled.Sensors),
    )

    SettingsScaffold(title = "Account", onBack = onBack, snackbar = snackbar) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
        ) {
            PageHeading(
                "Account",
                "Personal settings for ${user?.username ?: username} — private to you.",
            )
            Spacer(Modifier.height(16.dp))

            SettingsSectionNav(
                sections = sections,
                activeId = activeSection,
                onChange = { activeSection = it },
                drawerTitle = "Account",
            )
            Spacer(Modifier.height(16.dp))

            when (activeSection) {
                // ── Account ─────────────────────────────────────────────
                "account" -> {
                    SettingsCard(
                        title = "Profile picture",
                        description = "Shown on the account menu and the users list.",
                    ) {
                        if (avatarError.isNotEmpty()) ErrorBox(avatarError)

                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                            Avatar(
                                image = decodeAvatar(previewBase64),
                                initials = (user?.username ?: username).take(2).uppercase(),
                            )
                            Spacer(Modifier.width(12.dp))
                            Column(modifier = Modifier.weight(1f)) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    RenzoButton(
                                        text = "Upload image",
                                        icon = Icons.Filled.Upload,
                                        variant = "outline",
                                        small = true,
                                        onClick = { avatarPicker.launch(ALLOWED_AVATAR_TYPES.toTypedArray()) },
                                    )
                                    if (previewBase64 != null) {
                                        Spacer(Modifier.width(8.dp))
                                        RenzoButton(
                                            text = "Remove",
                                            variant = "ghost",
                                            small = true,
                                            busy = savingAvatar,
                                            onClick = {
                                                savingAvatar = true
                                                avatarError = ""
                                                scope.launch {
                                                    runCatching {
                                                        app.network.currentServiceOf<AccountApi>()
                                                            ?.updateMe(UpdateUserDto(removeAvatar = true))
                                                    }
                                                        .onSuccess {
                                                            avatarBase64 = null
                                                            avatarContentType = null
                                                            previewBase64 = null
                                                            snackbar.showSnackbar("Avatar removed")
                                                        }
                                                        .onFailure {
                                                            avatarError = it.apiMessage("Failed to remove avatar")
                                                        }
                                                    savingAvatar = false
                                                    reload()
                                                }
                                            },
                                        )
                                    }
                                }
                                Hint("Any image works — PNG, JPEG, GIF or WebP, up to 2MB.")
                            }
                        }

                        Spacer(Modifier.height(14.dp))
                        FieldLabel("Use Gravatar")
                        RenzoTextField(
                            value = gravatarEmail,
                            onValueChange = { gravatarEmail = it },
                            placeholder = "you@example.com",
                            keyboardType = KeyboardType.Email,
                        )
                        Spacer(Modifier.height(8.dp))
                        if (gravatarEmail.isNotBlank()) {
                            RenzoButton(
                                text = "Fetch",
                                variant = "secondary",
                                small = true,
                                onClick = {
                                    avatarError = ""
                                    scope.launch {
                                        when (val loaded = fetchGravatarBase64(gravatarEmail)) {
                                            is AvatarLoad.Ok -> {
                                                avatarBase64 = loaded.base64
                                                avatarContentType = loaded.contentType
                                                previewBase64 = loaded.base64
                                            }
                                            is AvatarLoad.Failed -> avatarError = loaded.message
                                        }
                                    }
                                },
                            )
                        }
                        Hint(
                            "Looks up that address's Gravatar as a preview — nothing changes until " +
                                "you save. The email itself is never sent to the server.",
                        )

                        if (avatarBase64 != null) {
                            Spacer(Modifier.height(12.dp))
                            RenzoButton(
                                text = if (savingAvatar) "Saving…" else "Save avatar",
                                busy = savingAvatar,
                                onClick = {
                                    savingAvatar = true
                                    avatarError = ""
                                    scope.launch {
                                        runCatching {
                                            app.network.currentServiceOf<AccountApi>()?.updateMe(
                                                UpdateUserDto(
                                                    avatarBase64 = avatarBase64,
                                                    avatarContentType = avatarContentType,
                                                ),
                                            )
                                        }
                                            .onSuccess {
                                                avatarBase64 = null
                                                avatarContentType = null
                                                gravatarEmail = ""
                                                snackbar.showSnackbar("Avatar updated")
                                            }
                                            .onFailure {
                                                avatarError = it.apiMessage("Failed to save avatar")
                                            }
                                        savingAvatar = false
                                        reload()
                                    }
                                },
                            )
                        }
                    }

                    Spacer(Modifier.height(16.dp))

                    // ── Security ────────────────────────────────────────
                    SettingsCard(
                        title = "Security",
                        description = "Changing your password signs out all your other sessions.",
                    ) {
                        FieldLabel("Email", Icons.Filled.Mail)
                        RenzoTextField(
                            value = email,
                            onValueChange = { email = it },
                            placeholder = "Optional — used for password reset",
                            keyboardType = KeyboardType.Email,
                        )
                        Spacer(Modifier.height(8.dp))
                        if (email.trim() != (user?.email ?: "")) {
                            RenzoButton(
                                text = if (savingEmail) "Saving…" else "Save",
                                busy = savingEmail,
                                onClick = {
                                    savingEmail = true
                                    scope.launch {
                                        runCatching {
                                            app.network.currentServiceOf<AccountApi>()
                                                ?.updateMe(UpdateUserDto(email = email.trim()))
                                        }
                                            .onSuccess { snackbar.showSnackbar("Email updated") }
                                            .onFailure {
                                                snackbar.showSnackbar(it.apiMessage("Failed to update email"))
                                            }
                                        savingEmail = false
                                        reload()
                                    }
                                },
                            )
                        }
                        Hint(
                            "Password-reset links are sent here. Leave empty to disable email reset " +
                                "for this account.",
                        )

                        CardDivider(top = 20, bottom = 12)
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(bottom = 10.dp)) {
                            Icon(
                                Icons.Filled.Lock, contentDescription = null,
                                tint = RenzoColors.Foreground,
                                modifier = Modifier.padding(end = 6.dp).size(14.dp),
                            )
                            Text(
                                "Password",
                                style = MaterialTheme.typography.titleSmall,
                                color = RenzoColors.Foreground,
                            )
                        }
                        if (passwordError.isNotEmpty()) ErrorBox(passwordError)
                        LabelledField("Current password", currentPassword, { currentPassword = it }, password = true)
                        LabelledField(
                            label = "New password",
                            value = newPassword,
                            onValueChange = { newPassword = it },
                            placeholder = "At least 8 characters",
                            password = true,
                        )
                        LabelledField("Confirm new password", confirmPassword, { confirmPassword = it }, password = true)
                        RenzoButton(
                            text = if (savingPassword) "Changing…" else "Update password",
                            icon = Icons.Filled.Check,
                            busy = savingPassword,
                            onClick = {
                                passwordError = ""
                                when {
                                    newPassword.length < 8 ->
                                        passwordError = "New password must be at least 8 characters"
                                    newPassword != confirmPassword ->
                                        passwordError = "Passwords do not match"
                                    else -> {
                                        savingPassword = true
                                        scope.launch {
                                            runCatching {
                                                app.network.currentServiceOf<AccountApi>()?.changePassword(
                                                    ChangePasswordRequestDto(currentPassword, newPassword),
                                                )
                                            }
                                                .onSuccess { response ->
                                                    val serverError = response?.error
                                                    if (response?.success == false && !serverError.isNullOrBlank()) {
                                                        passwordError = serverError
                                                    } else {
                                                        currentPassword = ""
                                                        newPassword = ""
                                                        confirmPassword = ""
                                                        snackbar.showSnackbar("Password changed")
                                                    }
                                                }
                                                .onFailure {
                                                    passwordError = it.apiMessage("Failed to change password")
                                                }
                                            savingPassword = false
                                        }
                                    }
                                }
                            },
                        )
                    }

                    Spacer(Modifier.height(16.dp))

                    // ── Offline downloads (native-only) ─────────────────
                    SettingsCard(
                        title = "Offline downloads",
                        description = "Where chapters downloaded for offline reading are written on " +
                            "this device.",
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                            Text(
                                folderLabel ?: "App default (private storage)",
                                style = MaterialTheme.typography.bodyMedium,
                                color = RenzoColors.MutedForeground,
                                modifier = Modifier.weight(1f),
                            )
                            RenzoButton(
                                text = "Choose…",
                                variant = "outline",
                                small = true,
                                onClick = { folderPicker.launch(null) },
                            )
                        }
                        CardDivider()
                        RenzoButton(
                            text = "Sign out",
                            icon = Icons.AutoMirrored.Filled.Logout,
                            variant = "destructive",
                            onClick = onLogout,
                        )
                    }
                }

                // ── Site logins ─────────────────────────────────────────
                "site-logins" -> SettingsCard(
                    title = "Site Logins",
                    description = "Log in to coin/paid sites (e.g. EzManga) so Renzo Shiori can load " +
                        "chapters you own — these credentials are yours alone and aren't visible to " +
                        "other users.",
                ) {
                    SiteLoginsSection(snackbar)
                }

                // ── Scrobbler ───────────────────────────────────────────
                "scrobbler" -> SettingsCard(
                    title = "Scrobbler",
                    description = "Link external trackers (AniList, MyAnimeList, Kitsu, MangaDex) to " +
                        "sync your reading progress. Your connections are private to your account — " +
                        "no other user can see them.",
                ) {
                    ScrobblerSettings(snackbar)
                }
            }

            Spacer(Modifier.height(28.dp))
        }
    }
}
