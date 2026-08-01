package app.renzoshiori.client.ui.home

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.LibraryBooks
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Key
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.People
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.Text
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.R
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.ui.browse.BrowseScreen
import app.renzoshiori.client.ui.library.LibraryContent
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.OnlineOfflinePill
import app.renzoshiori.client.ui.queue.QueueScreen
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.updates.UpdatesScreen
import kotlinx.coroutines.launch

private enum class Section(val label: String, val icon: ImageVector) {
    Library("Library", Icons.AutoMirrored.Filled.LibraryBooks),
    Updates("Updates", Icons.Filled.Notifications),
    Browse("Browse", Icons.Filled.AutoAwesome),
    Queue("Queue", Icons.AutoMirrored.Filled.List),
}

/**
 * 1:1 Compose clone of the web frontend's mobile chrome (command-bar.tsx at
 * the <lg breakpoint): 56dp top bar — hamburger, torii logo + wordmark,
 * expanding search, Online/Offline pill, initials avatar. The drawer clones
 * the web Sheet drawer (banner header + icon rows with the active left
 * accent bar); the avatar opens the account dropdown menu (user-menu.tsx).
 */
@Composable
fun HomeShell(
    user: UserDto,
    onOpenSeries: (String) -> Unit,
    onOpenOfflineSeries: (String) -> Unit,
    onOpenAccount: () -> Unit,
    onLogout: () -> Unit,
) {
    var section by rememberSaveable { mutableStateOf(Section.Library.name) }
    var searchOpen by rememberSaveable { mutableStateOf(false) }
    // What the drawer shows: the section nav (hamburger) or the account menu
    // (avatar) — same popout panel either way, per the web app's pattern.
    var drawerMode by remember { mutableStateOf("nav") }
    val current = Section.valueOf(section)
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val clipboard = LocalClipboardManager.current

    val libraryVm: LibraryViewModel = viewModel(
        factory = LibraryViewModel.factory(
            androidx.compose.ui.platform.LocalContext.current.applicationContext as android.app.Application,
        ),
    )
    val libraryState by libraryVm.state.collectAsState()

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet(
                drawerContainerColor = RenzoColors.Background,
                modifier = Modifier.width(288.dp), // web drawer: w-72
            ) {
                Box(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
                    Image(
                        painter = painterResource(R.drawable.renzo_login_banner),
                        contentDescription = "Renzo Shiori",
                        modifier = Modifier.height(32.dp),
                    )
                }
                HorizontalDivider(color = RenzoColors.Border)
                if (drawerMode == "account") {
                    AccountMenuContent(
                        user = user,
                        onCopyOpds = { clipboard.setText(AnnotatedString(user.opdsPath)) },
                        onAccount = {
                            scope.launch { drawerState.close() }
                            onOpenAccount()
                        },
                        onLogout = {
                            scope.launch { drawerState.close() }
                            onLogout()
                        },
                    )
                } else {
                    // Section rows — web SectionList: icon + label, active gets
                    // bg-primary/10 + primary text + a 2dp left accent bar.
                    Column(modifier = Modifier.padding(8.dp)) {
                        Section.entries.forEach { s ->
                            val active = s == current
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(if (active) RenzoColors.Primary.copy(alpha = 0.10f) else RenzoColors.Background)
                                    .clickable {
                                        section = s.name
                                        scope.launch { drawerState.close() }
                                    },
                            ) {
                                if (active) {
                                    Box(
                                        modifier = Modifier
                                            .align(Alignment.CenterStart)
                                            .padding(vertical = 8.dp)
                                            .width(2.dp)
                                            .height(24.dp)
                                            .clip(RoundedCornerShape(1.dp))
                                            .background(RenzoColors.Primary),
                                    )
                                }
                                Row(
                                    verticalAlignment = Alignment.CenterVertically,
                                    modifier = Modifier.padding(horizontal = 12.dp, vertical = 11.dp),
                                ) {
                                    Icon(
                                        s.icon,
                                        contentDescription = null,
                                        tint = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                                        modifier = Modifier.size(20.dp),
                                    )
                                    Text(
                                        s.label,
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                                        modifier = Modifier.padding(start = 12.dp),
                                    )
                                }
                            }
                        }
                    }
                }
            }
        },
    ) {
        Column(modifier = Modifier.fillMaxSize().background(RenzoColors.Background).statusBarsPadding()) {
            // ── 56dp command bar ─────────────────────────────────────────
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().height(56.dp).padding(horizontal = 6.dp),
            ) {
                IconButton(onClick = {
                    drawerMode = "nav"
                    scope.launch { drawerState.open() }
                }) {
                    Icon(Icons.Filled.Menu, contentDescription = "Menu", tint = RenzoColors.Foreground)
                }
                if (!searchOpen) {
                    Image(
                        painter = painterResource(R.drawable.splash_icon),
                        contentDescription = null,
                        modifier = Modifier.size(28.dp),
                    )
                    Text(
                        "Renzo Shiori",
                        style = MaterialTheme.typography.titleSmall,
                        color = RenzoColors.Foreground,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
                Spacer(Modifier.weight(1f))
                if (searchOpen) {
                    androidx.compose.foundation.text.BasicTextField(
                        value = libraryState.searchTerm,
                        onValueChange = libraryVm::setSearch,
                        singleLine = true,
                        textStyle = MaterialTheme.typography.bodyMedium.copy(color = RenzoColors.Foreground),
                        cursorBrush = SolidColor(RenzoColors.Foreground),
                        decorationBox = { inner ->
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .width(220.dp)
                                    .height(36.dp)
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(RenzoColors.Muted.copy(alpha = 0.7f))
                                    .border(1.dp, RenzoColors.Border.copy(alpha = 0.6f), RoundedCornerShape(8.dp))
                                    .padding(horizontal = 10.dp),
                            ) {
                                Icon(
                                    Icons.Filled.Search,
                                    contentDescription = null,
                                    tint = RenzoColors.MutedForeground,
                                    modifier = Modifier.size(16.dp),
                                )
                                Box(modifier = Modifier.padding(start = 8.dp).weight(1f)) {
                                    if (libraryState.searchTerm.isEmpty()) {
                                        Text(
                                            "Search series...",
                                            style = MaterialTheme.typography.bodyMedium,
                                            color = RenzoColors.MutedForeground,
                                        )
                                    }
                                    inner()
                                }
                            }
                        },
                    )
                }
                IconButton(onClick = {
                    if (searchOpen && libraryState.searchTerm.isNotEmpty()) libraryVm.setSearch("")
                    searchOpen = !searchOpen
                }) {
                    Icon(
                        if (searchOpen) Icons.Filled.Close else Icons.Filled.Search,
                        contentDescription = if (searchOpen) "Close search" else "Search",
                        tint = RenzoColors.Foreground,
                    )
                }
                OnlineOfflinePill(
                    offline = libraryState.offlineMode,
                    onToggle = { libraryVm.setOfflineMode(!libraryState.offlineMode) },
                )
                // Initials avatar → the account menu, in the same drawer
                // popout the hamburger uses (user's preference over a small
                // anchored dropdown).
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .padding(start = 6.dp, end = 4.dp)
                        .size(32.dp)
                        .clip(CircleShape)
                        .background(RenzoColors.Primary.copy(alpha = 0.20f))
                        .border(1.dp, RenzoColors.Primary.copy(alpha = 0.30f), CircleShape)
                        .clickable {
                            drawerMode = "account"
                            scope.launch { drawerState.open() }
                        },
                ) {
                    Text(
                        user.username.take(2).uppercase(),
                        style = MaterialTheme.typography.labelSmall,
                        color = RenzoColors.Primary,
                    )
                }
            }
            HorizontalDivider(color = RenzoColors.Border.copy(alpha = 0.6f))

            // ── Active section ───────────────────────────────────────────
            Box(modifier = Modifier.weight(1f)) {
                when (current) {
                    Section.Library -> LibraryContent(
                        vm = libraryVm,
                        onOpenSeries = onOpenSeries,
                        onOpenOfflineSeries = onOpenOfflineSeries,
                    )
                    Section.Updates -> UpdatesScreen(onOpenSeries = onOpenSeries)
                    Section.Browse -> BrowseScreen()
                    Section.Queue -> QueueScreen()
                }
            }
        }
    }
}

/**
 * The web account menu's content (user-menu.tsx), rendered inside the same
 * drawer popout the section nav uses: username + role badge header, OPDS
 * path row with copy, grouped items with icons and dividers, Sign out. Web
 * actions with no native equivalent yet render disabled so the menu's
 * structure still matches 1:1.
 */
@Composable
private fun AccountMenuContent(
    user: UserDto,
    onCopyOpds: () -> Unit,
    onAccount: () -> Unit,
    onLogout: () -> Unit,
) {
    val roleLabel = when (user.level) {
        UserLevel.OWNER -> "Owner"
        UserLevel.ADMIN -> "Admin"
        UserLevel.MANAGER -> "Manager"
        else -> "User"
    }
    val roleColor = when (user.level) {
        UserLevel.OWNER -> RenzoColors.Primary
        UserLevel.ADMIN -> RenzoColors.Amber
        UserLevel.MANAGER -> androidx.compose.ui.graphics.Color(0xFFA855F7)
        else -> RenzoColors.Blue
    }

    Column(modifier = Modifier.padding(vertical = 4.dp)) {
        // Header — username left, role badge right.
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
        ) {
            Text(
                user.username,
                style = MaterialTheme.typography.titleSmall,
                color = RenzoColors.Foreground,
                modifier = Modifier.weight(1f),
            )
            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(roleColor.copy(alpha = 0.15f))
                    .padding(horizontal = 8.dp, vertical = 2.dp),
            ) {
                Text(roleLabel, style = MaterialTheme.typography.labelSmall, color = roleColor)
            }
        }
        HorizontalDivider(color = RenzoColors.Border)
        // OPDS path + copy.
        if (user.opdsPath.isNotEmpty()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
            ) {
                Text(
                    user.opdsPath,
                    style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    modifier = Modifier.weight(1f),
                )
                Icon(
                    Icons.Filled.ContentCopy,
                    contentDescription = "Copy OPDS path",
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(15.dp).clickable(onClick = onCopyOpds),
                )
            }
            HorizontalDivider(color = RenzoColors.Border)
        }

        MenuRow(Icons.Filled.Edit, "Edit…", enabled = false) {}
        MenuRow(Icons.Filled.Key, "Change password…", enabled = false) {}
        MenuRow(Icons.Filled.Sensors, "Trackers…", enabled = false) {}
        HorizontalDivider(color = RenzoColors.Border)
        if (user.level >= UserLevel.ADMIN) MenuRow(Icons.Filled.People, "Users", enabled = false) {}
        MenuRow(Icons.Filled.Key, "Account", enabled = true, onClick = onAccount)
        if (user.level >= UserLevel.OWNER) MenuRow(Icons.Filled.Settings, "Settings", enabled = false) {}
        HorizontalDivider(color = RenzoColors.Border)
        MenuRow(Icons.Filled.Palette, "Appearance", enabled = false) {}
        HorizontalDivider(color = RenzoColors.Border)
        MenuRow(Icons.AutoMirrored.Filled.Logout, "Sign out", enabled = true, onClick = onLogout)
    }
}

@Composable
private fun MenuRow(icon: ImageVector, label: String, enabled: Boolean, onClick: () -> Unit) {
    val alpha = if (enabled) 1f else 0.35f
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 9.dp),
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = RenzoColors.Foreground.copy(alpha = alpha),
            modifier = Modifier.size(16.dp),
        )
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = RenzoColors.Foreground.copy(alpha = alpha),
            modifier = Modifier.padding(start = 10.dp),
        )
    }
}
