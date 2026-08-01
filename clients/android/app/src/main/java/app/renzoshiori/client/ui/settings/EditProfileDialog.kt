package app.renzoshiori.client.ui.settings

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Route
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.UpdateUserDto
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.data.network.AccountApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch

/**
 * Account menu → "Edit…". The web opens EditUserDialog (user-dialog.tsx) on the
 * signed-in user, so this loads GET /api/auth/me and hands it to the shared
 * [UserEditDialog] with `isSelf = true` — which is what hides Level/Active,
 * exactly as the web's `canChangeLevelOrActive` does.
 *
 * There is deliberately no username field: the backend has no rename endpoint.
 */
@Composable
fun EditProfileDialog(onDismiss: () -> Unit, onSaved: () -> Unit = {}) {
    val app = LocalContext.current.applicationContext as RenzoApp
    var me by remember { mutableStateOf<UserDto?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        runCatching { app.network.currentServiceOf<AccountApi>()?.me() }
            .onSuccess { me = it }
            .onFailure { loadError = it.apiMessage("Couldn't load your profile") }
    }

    val user = me
    if (user == null) {
        RenzoDialog(onDismiss = onDismiss, title = "Edit profile") {
            loadError?.let { ErrorBox(it) }
            if (loadError == null) LoadingBlock()
            Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                RenzoButton("Close", variant = "outline", onClick = onDismiss)
            }
        }
    } else {
        UserEditDialog(
            user = user,
            currentUserLevel = user.level,
            isSelf = true,
            onDismiss = onDismiss,
            onSaved = onSaved,
        )
    }
}

/**
 * user-dialog.tsx, transliterated. Shared by the account menu's "Edit…" (self)
 * and Users → Edit (admin). Every side-by-side row in the web dialog stacks
 * vertically here.
 */
@Composable
fun UserEditDialog(
    user: UserDto,
    currentUserLevel: Int,
    isSelf: Boolean,
    onDismiss: () -> Unit,
    onSaved: () -> Unit,
) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val clipboard = LocalClipboardManager.current

    var level by remember(user.id) { mutableStateOf(user.level) }
    var isActive by remember(user.id) { mutableStateOf(user.isActive) }
    var email by remember(user.id) { mutableStateOf(user.email ?: "") }
    var removeAvatar by remember(user.id) { mutableStateOf(false) }
    var avatarBase64 by remember(user.id) { mutableStateOf<String?>(null) }
    var avatarContentType by remember(user.id) { mutableStateOf<String?>(null) }
    var previewBase64 by remember(user.id) { mutableStateOf(user.avatarBase64) }
    var gravatarEmail by remember(user.id) { mutableStateOf("") }
    var opdsPath by remember(user.id) { mutableStateOf(user.opdsPath) }
    var errorText by remember(user.id) { mutableStateOf("") }
    var saving by remember(user.id) { mutableStateOf(false) }
    var regenerating by remember(user.id) { mutableStateOf(false) }
    var copied by remember(user.id) { mutableStateOf(false) }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                when (val loaded = readPickedAvatar(context, uri)) {
                    is AvatarLoad.Ok -> {
                        avatarBase64 = loaded.base64
                        avatarContentType = loaded.contentType
                        previewBase64 = loaded.base64
                        removeAvatar = false
                        errorText = ""
                    }
                    is AvatarLoad.Failed -> errorText = loaded.message
                }
            }
        }
    }

    // Admin-only server-side; a plain user gets a 403, so it simply isn't shown
    // to them rather than being offered and failing.
    val canRegenerateOpds = currentUserLevel >= UserLevel.ADMIN
    val isTargetOwner = user.level == UserLevel.OWNER
    val isCurrentOwner = currentUserLevel == UserLevel.OWNER
    val canEditThisUser = !isTargetOwner || isCurrentOwner
    val canChangeLevelOrActive = canEditThisUser && !isSelf &&
        (user.level != UserLevel.ADMIN || isCurrentOwner)

    RenzoDialog(
        onDismiss = onDismiss,
        title = if (isSelf) "Edit profile" else "Edit User: ${user.username}",
        description = "Update user settings and avatar.",
    ) {
        if (errorText.isNotEmpty()) ErrorBox(errorText)

        // ── Avatar ──────────────────────────────────────────────────────
        FieldLabel("Avatar")
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Avatar(
                image = if (removeAvatar) null else decodeAvatar(previewBase64),
                initials = user.username.take(2).uppercase(),
            )
            Spacer(Modifier.width(16.dp))
            RenzoButton(
                text = "Upload Image",
                icon = Icons.Filled.Upload,
                variant = "outline",
                small = true,
                onClick = { picker.launch(ALLOWED_AVATAR_TYPES.toTypedArray()) },
            )
        }
        Spacer(Modifier.height(14.dp))

        // ── Gravatar ────────────────────────────────────────────────────
        FieldLabel("Get from Gravatar")
        RenzoTextField(
            value = gravatarEmail,
            onValueChange = { gravatarEmail = it },
            placeholder = "Enter email for Gravatar",
        )
        Spacer(Modifier.height(8.dp))
        RenzoButton(
            text = "Fetch",
            variant = "secondary",
            small = true,
            onClick = {
                if (gravatarEmail.isNotBlank()) {
                    scope.launch {
                        when (val loaded = fetchGravatarBase64(gravatarEmail)) {
                            is AvatarLoad.Ok -> {
                                avatarBase64 = loaded.base64
                                avatarContentType = loaded.contentType
                                previewBase64 = loaded.base64
                                removeAvatar = false
                                errorText = ""
                            }
                            is AvatarLoad.Failed -> errorText = loaded.message
                        }
                    }
                }
            },
        )
        Hint(
            "Email is used only on this device to fetch the Gravatar image. It is never sent to " +
                "the backend.",
        )
        Spacer(Modifier.height(14.dp))

        // ── Remove avatar ───────────────────────────────────────────────
        if (previewBase64 != null && !removeAvatar) {
            SwitchRow(checked = false, onCheckedChange = { removeAvatar = it }, label = "Remove avatar")
        }

        // ── Email ───────────────────────────────────────────────────────
        LabelledField(
            label = "Email",
            value = email,
            onValueChange = { email = it },
            placeholder = "Optional — used for password reset",
            hint = "Password-reset links are sent here. Leave empty to disable email reset for " +
                "this account.",
        )

        // ── OPDS path ───────────────────────────────────────────────────
        FieldLabel("OPDS Path")
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                    .background(RenzoColors.Muted.copy(alpha = 0.3f))
                    .padding(horizontal = 10.dp, vertical = 8.dp),
            ) {
                Icon(
                    Icons.Filled.Route, contentDescription = null,
                    tint = RenzoColors.MutedForeground, modifier = Modifier.size(12.dp),
                )
                Text(
                    opdsPath,
                    style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(start = 6.dp).weight(1f),
                )
                IconGhostButton(
                    if (copied) Icons.Filled.Check else Icons.Filled.ContentCopy,
                    "Copy OPDS URL",
                    if (copied) RenzoColors.Green else RenzoColors.MutedForeground,
                ) {
                    val base = app.tokenStore.serverUrl?.trimEnd('/') ?: ""
                    clipboard.setText(AnnotatedString("$base/${opdsPath.trimStart('/')}"))
                    copied = true
                }
            }
            if (canRegenerateOpds) {
                Spacer(Modifier.width(8.dp))
                RenzoButton(
                    text = if (regenerating) "Regenerating..." else "Regenerate",
                    icon = Icons.Filled.Refresh,
                    variant = "outline",
                    small = true,
                    busy = regenerating,
                    onClick = {
                        regenerating = true
                        scope.launch {
                            runCatching { app.network.currentServiceOf<AccountApi>()?.regenerateOpds(user.id) }
                                .onSuccess { it?.let { path -> opdsPath = path.opdsPath } }
                                .onFailure { errorText = it.apiMessage("Couldn't regenerate the OPDS path") }
                            regenerating = false
                        }
                    },
                )
            }
        }
        Spacer(Modifier.height(14.dp))

        // ── Level + Active (admin hierarchy only, hidden otherwise) ──────
        if (canChangeLevelOrActive) {
            FieldLabel("Level")
            RenzoSelect(
                options = listOf(
                    UserLevel.USER.toString() to "User",
                    UserLevel.MANAGER.toString() to "Manager",
                    UserLevel.ADMIN.toString() to "Admin",
                ),
                value = level.toString(),
                onChange = { level = it.toIntOrNull() ?: level },
            )
            Spacer(Modifier.height(12.dp))
            SwitchRow(checked = isActive, onCheckedChange = { isActive = it }, label = "Active")
        }

        Spacer(Modifier.height(8.dp))
        Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
            RenzoButton("Cancel", variant = "outline", onClick = onDismiss)
            Spacer(Modifier.width(8.dp))
            RenzoButton(
                text = if (saving) "Saving..." else "Save",
                busy = saving,
                onClick = {
                    saving = true
                    errorText = ""
                    val body = UpdateUserDto(
                        avatarBase64 = avatarBase64.takeIf { !removeAvatar },
                        avatarContentType = avatarContentType.takeIf { !removeAvatar && avatarBase64 != null },
                        removeAvatar = if (removeAvatar) true else null,
                        email = email.trim().takeIf { it != (user.email ?: "") },
                        level = level.takeIf { canChangeLevelOrActive && it != user.level },
                        isActive = isActive.takeIf { canChangeLevelOrActive && it != user.isActive },
                    )
                    scope.launch {
                        val api = app.network.currentServiceOf<AccountApi>()
                        runCatching {
                            // Self-edits go through /api/auth/me: /api/users/{id}
                            // is admin-only, so a regular user editing their own
                            // avatar/email would otherwise be rejected.
                            if (isSelf) api?.updateMe(body) else api?.updateUser(user.id, body)
                        }
                            .onSuccess {
                                saving = false
                                onSaved()
                                onDismiss()
                            }
                            .onFailure {
                                errorText = it.apiMessage("Failed to update user")
                                saving = false
                            }
                    }
                },
            )
        }
    }
}
