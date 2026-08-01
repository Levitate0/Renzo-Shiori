package app.renzoshiori.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Mail
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.MilitaryTech
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.CreateUserRequestDto
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.data.network.AccountApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch

/**
 * Account menu → "Users" (Admin+). Transliteration of
 * RenzoFrontend/src/app/users/page.tsx + components/comp/users/user-manager.tsx.
 *
 * The web renders one wide table (Avatar / Username / Level / OPDS Path /
 * Active / Password / Last Login / Actions). On a phone each row becomes a
 * card carrying the exact same eight facts — the only change the brief allows.
 *
 * The web's "first user" bootstrap form is intentionally absent: it only ever
 * appears when the database has zero users, which cannot happen here because
 * reaching this screen requires being signed in as one.
 */
@Composable
fun UsersScreen(onBack: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }

    var users by remember { mutableStateOf<List<UserDto>?>(null) }
    var currentUser by remember { mutableStateOf<UserDto?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }

    var createOpen by remember { mutableStateOf(false) }
    var editTarget by remember { mutableStateOf<UserDto?>(null) }
    var inviteTarget by remember { mutableStateOf<UserDto?>(null) }
    var deleteTarget by remember { mutableStateOf<UserDto?>(null) }

    suspend fun refresh() {
        val api = app.network.currentServiceOf<AccountApi>() ?: return
        currentUser = runCatching { api.me() }.getOrNull()
        runCatching { api.listUsers() }
            .onSuccess { users = it; loadError = null }
            .onFailure { users = emptyList(); loadError = it.apiMessage("Failed to load users") }
    }

    LaunchedEffect(Unit) { refresh() }

    SettingsScaffold(title = "Users", onBack = onBack, snackbar = snackbar) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
        ) {
            Text(
                "Manage user accounts, invite new users, and configure access permissions.",
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.MutedForeground,
            )
            Spacer(Modifier.height(12.dp))
            RenzoButton(
                text = "Add User",
                icon = Icons.Filled.Add,
                modifier = Modifier.fillMaxWidth(),
                onClick = { createOpen = true },
            )
            Spacer(Modifier.height(16.dp))

            when {
                loadError != null -> ErrorBox(loadError!!)
                users == null -> LoadingBlock("Loading users...")
                users!!.isEmpty() -> EmptyNote("No users yet.")
                else -> users!!.forEach { user ->
                    UserRow(
                        user = user,
                        currentLevel = currentUser?.level ?: UserLevel.USER,
                        onEdit = { editTarget = user },
                        onInvite = { inviteTarget = user },
                        onDelete = { deleteTarget = user },
                    )
                }
            }
            Spacer(Modifier.height(24.dp))
        }
    }

    // ── Create ──────────────────────────────────────────────────────────
    if (createOpen) {
        CreateUserDialog(
            onDismiss = { createOpen = false },
            onCreated = {
                createOpen = false
                scope.launch { refresh() }
            },
        )
    }

    // ── Edit ────────────────────────────────────────────────────────────
    editTarget?.let { target ->
        UserEditDialog(
            user = target,
            currentUserLevel = currentUser?.level ?: UserLevel.USER,
            isSelf = currentUser?.id == target.id,
            onDismiss = { editTarget = null },
            onSaved = { scope.launch { refresh() } },
        )
    }

    // ── Invite ──────────────────────────────────────────────────────────
    inviteTarget?.let { target ->
        InviteDialog(user = target, onDismiss = { inviteTarget = null })
    }

    // ── Delete ──────────────────────────────────────────────────────────
    deleteTarget?.let { target ->
        RenzoDialog(
            onDismiss = { deleteTarget = null },
            title = "Delete User",
            description = "Are you sure you want to delete ${target.username}? This action cannot be undone.",
        ) {
            Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                RenzoButton("Cancel", variant = "outline", onClick = { deleteTarget = null })
                Spacer(Modifier.width(8.dp))
                RenzoButton(
                    text = "Delete",
                    variant = "destructive",
                    onClick = {
                        deleteTarget = null
                        scope.launch {
                            runCatching { app.network.currentServiceOf<AccountApi>()?.deleteUser(target.id) }
                                .onFailure { snackbar.showSnackbar(it.apiMessage("Failed to delete user")) }
                            refresh()
                        }
                    },
                )
            }
        }
    }
}

/** One table row from user-manager.tsx, folded into a card. */
@Composable
private fun UserRow(
    user: UserDto,
    currentLevel: Int,
    onEdit: () -> Unit,
    onInvite: () -> Unit,
    onDelete: () -> Unit,
) {
    var menuOpen by remember { mutableStateOf(false) }
    val levelLabel = when (user.level) {
        UserLevel.OWNER -> "Owner"
        UserLevel.ADMIN -> "Admin"
        UserLevel.MANAGER -> "Manager"
        else -> "User"
    }
    val levelColor = when (user.level) {
        UserLevel.OWNER -> RenzoColors.Red
        UserLevel.ADMIN -> RenzoColors.Amber
        UserLevel.MANAGER -> Color(0xFFA855F7)
        else -> RenzoColors.Blue
    }
    // Same hierarchy the web enforces on the dropdown items.
    val canEditOrInvite = user.level != UserLevel.OWNER || currentLevel == UserLevel.OWNER
    val canDelete = user.level != UserLevel.OWNER &&
        (user.level != UserLevel.ADMIN || currentLevel == UserLevel.OWNER)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 10.dp)
            .clip(RoundedCornerShape(12.dp))
            .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
            .background(RenzoColors.Card)
            .padding(14.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Avatar(decodeAvatar(user.avatarBase64), user.username.take(2).uppercase(), size = 36)
            Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                Text(user.username, style = MaterialTheme.typography.titleSmall, color = RenzoColors.Foreground)
                Spacer(Modifier.height(4.dp))
                RenzoBadge(levelLabel, levelColor, icon = Icons.Filled.MilitaryTech)
            }
            if (canEditOrInvite || canDelete) {
                Box {
                    IconGhostButton(
                        Icons.Filled.MoreHoriz, "Actions", RenzoColors.MutedForeground,
                    ) { menuOpen = true }
                    DropdownMenu(
                        expanded = menuOpen,
                        onDismissRequest = { menuOpen = false },
                        containerColor = RenzoColors.Popover,
                    ) {
                        if (canEditOrInvite) {
                            DropdownMenuItem(
                                text = { Text("Edit...", color = RenzoColors.Foreground) },
                                onClick = { menuOpen = false; onEdit() },
                            )
                            DropdownMenuItem(
                                text = { Text("Invite...", color = RenzoColors.Foreground) },
                                leadingIcon = {
                                    Icon(
                                        Icons.Filled.Mail, contentDescription = null,
                                        tint = RenzoColors.Foreground, modifier = Modifier.size(16.dp),
                                    )
                                },
                                onClick = { menuOpen = false; onInvite() },
                            )
                        }
                        if (canDelete) {
                            DropdownMenuItem(
                                text = { Text("Delete...", color = RenzoColors.Red) },
                                leadingIcon = {
                                    Icon(
                                        Icons.Filled.Delete, contentDescription = null,
                                        tint = RenzoColors.Red, modifier = Modifier.size(16.dp),
                                    )
                                },
                                onClick = { menuOpen = false; onDelete() },
                            )
                        }
                    }
                }
            }
        }

        Spacer(Modifier.height(10.dp))
        Text(
            user.opdsPath,
            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
            color = RenzoColors.MutedForeground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier
                    .size(8.dp)
                    .clip(CircleShape)
                    .background(if (user.isActive) RenzoColors.Green else RenzoColors.Red),
            )
            Text(
                if (user.isActive) "Active" else "Inactive",
                style = MaterialTheme.typography.labelSmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(start = 6.dp, end = 12.dp),
            )
            if (user.hasPassword) {
                RenzoBadge("Password set", RenzoColors.Green)
            } else {
                RenzoBadge("Password not set", RenzoColors.Amber)
            }
        }
        Text(
            "Last login: ${user.lastLoginAt?.let { formatShortDate(it) } ?: "Never"}",
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
            modifier = Modifier.padding(top = 8.dp),
        )
    }
}

/** users/page.tsx's "Create New User" dialog. */
@Composable
private fun CreateUserDialog(onDismiss: () -> Unit, onCreated: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    var username by remember { mutableStateOf("") }
    var level by remember { mutableStateOf(UserLevel.USER) }
    var errorText by remember { mutableStateOf("") }
    var pending by remember { mutableStateOf(false) }

    RenzoDialog(
        onDismiss = onDismiss,
        title = "Create New User",
        description = "Create a new user. They will need to be invited to set their password.",
    ) {
        if (errorText.isNotEmpty()) ErrorBox(errorText)
        LabelledField("Username", username, { username = it }, placeholder = "Enter username")
        FieldLabel("Level")
        RenzoSelect(
            options = listOf(
                UserLevel.USER.toString() to "User",
                UserLevel.MANAGER.toString() to "Manager",
                UserLevel.ADMIN.toString() to "Admin",
            ),
            value = level.toString(),
            onChange = { level = it.toIntOrNull() ?: UserLevel.USER },
        )
        Spacer(Modifier.height(16.dp))
        Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
            RenzoButton("Cancel", variant = "outline", onClick = onDismiss)
            Spacer(Modifier.width(8.dp))
            RenzoButton(
                text = if (pending) "Creating..." else "Create User",
                busy = pending,
                onClick = {
                    errorText = ""
                    if (username.length < 3 || username.length > 32) {
                        errorText = "Username must be between 3 and 32 characters"
                    } else {
                        pending = true
                        scope.launch {
                            runCatching {
                                app.network.currentServiceOf<AccountApi>()
                                    ?.createUser(CreateUserRequestDto(username, level))
                            }
                                .onSuccess { onCreated() }
                                .onFailure { errorText = it.apiMessage("Failed to create user") }
                            pending = false
                        }
                    }
                },
            )
        }
    }
}

/**
 * user-invite-dialog.tsx: generates the invite message on open, auto-copies it,
 * and offers Regenerate. The message carries the user's OPDS path and either a
 * set-password link or share instructions, depending on the server's auth mode.
 */
@Composable
private fun InviteDialog(user: UserDto, onDismiss: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    val clipboard = LocalClipboardManager.current
    var message by remember(user.id) { mutableStateOf<String?>(null) }
    var errorText by remember(user.id) { mutableStateOf("") }
    var pending by remember(user.id) { mutableStateOf(false) }
    var copied by remember(user.id) { mutableStateOf(false) }

    suspend fun generate() {
        pending = true
        errorText = ""
        runCatching { app.network.currentServiceOf<AccountApi>()?.generateInvite(user.id) }
            .onSuccess {
                message = it?.message
                it?.message?.let { text ->
                    clipboard.setText(AnnotatedString(text))
                    copied = true
                }
            }
            .onFailure { errorText = it.apiMessage("Failed to generate invite") }
        pending = false
    }

    LaunchedEffect(user.id) { generate() }

    RenzoDialog(
        onDismiss = onDismiss,
        title = "Invite User: ${user.username}",
        description = "Share this message with the user. It includes their unique OPDS path and " +
            "how to get signed in.",
    ) {
        if (errorText.isNotEmpty()) ErrorBox(errorText)
        if (message == null && pending) LoadingBlock("Generating…")

        message?.let { text ->
            FieldLabel("Invite Message")
            Text(
                text,
                style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                color = RenzoColors.Foreground,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                    .padding(12.dp),
            )
            Spacer(Modifier.height(10.dp))
            RenzoButton(
                text = if (copied) "Copied!" else "Copy to Clipboard",
                icon = Icons.Filled.ContentCopy,
                variant = "outline",
                small = true,
                modifier = Modifier.fillMaxWidth(),
                onClick = {
                    clipboard.setText(AnnotatedString(text))
                    copied = true
                },
            )
        }

        Spacer(Modifier.height(16.dp))
        Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
            RenzoButton("Close", variant = "outline", onClick = onDismiss)
            if (message != null) {
                Spacer(Modifier.width(8.dp))
                RenzoButton(
                    text = if (pending) "Regenerating..." else "Regenerate",
                    icon = Icons.Filled.Refresh,
                    variant = "secondary",
                    busy = pending,
                    onClick = { scope.launch { generate() } },
                )
            }
        }
    }
}
