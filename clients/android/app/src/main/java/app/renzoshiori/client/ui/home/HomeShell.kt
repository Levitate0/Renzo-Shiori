package app.renzoshiori.client.ui.home

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Explore
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Key
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.MonitorHeart
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.People
import androidx.compose.material.icons.filled.Power
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.Text
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.R
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.DownloadsMetricsDto
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

private enum class Section(val label: String, val icon: ImageVector, val enabled: Boolean = true) {
    Library("Library", Icons.AutoMirrored.Filled.LibraryBooks),
    Updates("Updates", Icons.Filled.Notifications),
    Browse("Browse", Icons.Filled.AutoAwesome),
    Queue("Queue", Icons.AutoMirrored.Filled.List),
    Status("Status", Icons.Filled.MonitorHeart, enabled = false), // native screen pending
    Sources("Sources", Icons.Filled.Power, enabled = false), // native screen pending
}

/**
 * Compose transliteration of the mobile web chrome (command-bar.tsx <lg):
 * 56dp bar — hamburger, torii logo, expanding search, Online pill, initials
 * avatar. The hamburger opens the nav drawer exactly as the web Sheet:
 * banner + X header, all six icon section rows (Queue with live dot + count
 * badge), and the footer stats pill (⬇ active · 🕐 queued · ⚠ failed) with
 * the project-links icon row. The avatar opens the account DROPDOWN (the
 * web user-menu card), anchored under it — not a drawer.
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
    var menuOpen by remember { mutableStateOf(false) }
    val current = Section.valueOf(section)
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val clipboard = LocalClipboardManager.current
    val context = LocalContext.current
    val app = context.applicationContext as RenzoApp

    // Queue metrics for the drawer badge + footer stats (refreshed each time
    // the drawer opens, mirroring the web's always-current badge).
    var metrics by remember { mutableStateOf(DownloadsMetricsDto()) }
    LaunchedEffect(drawerState.isOpen) {
        if (drawerState.isOpen) {
            runCatching { app.network.currentApi()?.downloadMetrics() }
                .getOrNull()?.let { metrics = it ?: metrics }
        }
    }

    val libraryVm: LibraryViewModel = viewModel(
        factory = LibraryViewModel.factory(context.applicationContext as android.app.Application),
    )
    val libraryState by libraryVm.state.collectAsState()

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet(
                drawerContainerColor = RenzoColors.Background,
                modifier = Modifier.width(300.dp),
            ) {
                // Banner header + X close, like the web drawer.
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
                ) {
                    Image(
                        painter = painterResource(R.drawable.renzo_login_banner),
                        contentDescription = "Renzo Shiori",
                        modifier = Modifier.height(30.dp).weight(1f, fill = false),
                    )
                    Spacer(Modifier.weight(1f))
                    IconButton(onClick = { scope.launch { drawerState.close() } }) {
                        Icon(
                            Icons.Filled.Close, contentDescription = "Close",
                            tint = RenzoColors.MutedForeground,
                            modifier = Modifier
                                .border(1.dp, RenzoColors.Border, CircleShape)
                                .padding(6.dp),
                        )
                    }
                }
                HorizontalDivider(color = RenzoColors.Border)

                Column(modifier = Modifier.padding(8.dp).weight(1f)) {
                    Section.entries.forEach { s ->
                        val active = s == current
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(8.dp))
                                .background(if (active) RenzoColors.Primary.copy(alpha = 0.10f) else Color.Transparent)
                                .clickable(enabled = s.enabled) {
                                    section = s.name
                                    scope.launch { drawerState.close() }
                                }
                                .alpha(if (s.enabled) 1f else 0.35f),
                        ) {
                            if (active) {
                                Box(
                                    modifier = Modifier
                                        .align(Alignment.CenterStart)
                                        .padding(vertical = 10.dp)
                                        .width(2.dp)
                                        .height(22.dp)
                                        .clip(RoundedCornerShape(1.dp))
                                        .background(RenzoColors.Primary),
                                )
                            }
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 12.dp),
                            ) {
                                Icon(
                                    s.icon, contentDescription = null,
                                    tint = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                                    modifier = Modifier.size(20.dp),
                                )
                                Text(
                                    s.label,
                                    style = MaterialTheme.typography.bodyLarge,
                                    color = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                                    modifier = Modifier.padding(start = 14.dp).weight(1f),
                                )
                                // Queue: live dot + count badge (web SectionList).
                                if (s == Section.Queue) {
                                    val count = metrics.downloads + metrics.failed
                                    if (metrics.downloads > 0) {
                                        Box(
                                            modifier = Modifier
                                                .padding(end = 8.dp)
                                                .size(7.dp)
                                                .clip(CircleShape)
                                                .background(RenzoColors.Primary),
                                        )
                                    }
                                    if (count > 0) {
                                        Box(
                                            contentAlignment = Alignment.Center,
                                            modifier = Modifier
                                                .clip(RoundedCornerShape(50))
                                                .background(RenzoColors.Primary)
                                                .padding(horizontal = 8.dp, vertical = 2.dp),
                                        ) {
                                            Text(
                                                if (count > 99) "99" else count.toString(),
                                                style = MaterialTheme.typography.labelSmall,
                                                color = RenzoColors.PrimaryForeground,
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Footer — stats pill + project links, like the web drawer.
                Column(modifier = Modifier.padding(16.dp)) {
                    Row(
                        horizontalArrangement = Arrangement.SpaceEvenly,
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(12.dp))
                            .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                            .padding(vertical = 10.dp),
                    ) {
                        StatChip(Icons.Filled.Download, metrics.downloads.toString(), RenzoColors.Blue)
                        StatChip(Icons.Filled.Schedule, metrics.queued.toString(), Color(0xFFEAB308))
                        StatChip(Icons.Filled.Warning, metrics.failed.toString(), RenzoColors.Red)
                    }
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(28.dp, Alignment.CenterHorizontally),
                        modifier = Modifier.fillMaxWidth().padding(top = 14.dp),
                    ) {
                        Icon(
                            Icons.Filled.Language,
                            contentDescription = "Project site",
                            tint = RenzoColors.MutedForeground,
                            modifier = Modifier.size(20.dp).clickable {
                                runCatching {
                                    context.startActivity(
                                        Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/Levitate0/Renzo-Shiori")),
                                    )
                                }
                            },
                        )
                        Icon(
                            Icons.Filled.Explore, contentDescription = "Open server",
                            tint = RenzoColors.MutedForeground,
                            modifier = Modifier.size(20.dp).clickable {
                                app.tokenStore.serverUrl?.let {
                                    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(it))) }
                                }
                            },
                        )
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
                IconButton(onClick = { scope.launch { drawerState.open() } }) {
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
                                    .width(210.dp)
                                    .height(36.dp)
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(RenzoColors.Muted.copy(alpha = 0.7f))
                                    .border(1.dp, RenzoColors.Border.copy(alpha = 0.6f), RoundedCornerShape(8.dp))
                                    .padding(horizontal = 10.dp),
                            ) {
                                Icon(
                                    Icons.Filled.Search, contentDescription = null,
                                    tint = RenzoColors.MutedForeground, modifier = Modifier.size(16.dp),
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
                Box {
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .padding(start = 6.dp, end = 4.dp)
                            .size(32.dp)
                            .clip(CircleShape)
                            .background(RenzoColors.Primary.copy(alpha = 0.20f))
                            .border(1.dp, RenzoColors.Primary.copy(alpha = 0.30f), CircleShape)
                            .clickable { menuOpen = true },
                    ) {
                        Text(
                            user.username.take(2).uppercase(),
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.Primary,
                        )
                    }
                    AccountDropdown(
                        expanded = menuOpen,
                        onDismiss = { menuOpen = false },
                        user = user,
                        onCopyOpds = { clipboard.setText(AnnotatedString(user.opdsPath)) },
                        onAccount = { menuOpen = false; onOpenAccount() },
                        onLogout = { menuOpen = false; onLogout() },
                    )
                }
            }
            HorizontalDivider(color = RenzoColors.Border.copy(alpha = 0.6f))

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
                    else -> {}
                }
            }
        }
    }
}

@Composable
private fun StatChip(icon: ImageVector, value: String, color: Color) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(16.dp))
        Text(
            value,
            style = MaterialTheme.typography.labelMedium,
            color = color,
            modifier = Modifier.padding(start = 5.dp),
        )
    }
}

/**
 * The web account dropdown (user-menu.tsx), anchored under the avatar —
 * matching the mobile web screenshot exactly: username + role badge, OPDS
 * copy row, Edit/Change password/Trackers/Import Suwayomi Backup, divider,
 * Users/Account/Settings/Import Series, divider, Appearance/Take a
 * tour/Adult toggle, divider, Sign out, icon footer. Items without native
 * functionality yet render dimmed.
 */
@Composable
private fun AccountDropdown(
    expanded: Boolean,
    onDismiss: () -> Unit,
    user: UserDto,
    onCopyOpds: () -> Unit,
    onAccount: () -> Unit,
    onLogout: () -> Unit,
) {
    val roleLabel = when (user.level) {
        UserLevel.OWNER -> "Owner"; UserLevel.ADMIN -> "Admin"; UserLevel.MANAGER -> "Manager"; else -> "User"
    }
    val roleColor = when (user.level) {
        UserLevel.OWNER -> RenzoColors.Primary
        UserLevel.ADMIN -> RenzoColors.Amber
        UserLevel.MANAGER -> Color(0xFFA855F7)
        else -> RenzoColors.Blue
    }

    DropdownMenu(
        expanded = expanded,
        onDismissRequest = onDismiss,
        containerColor = RenzoColors.Popover,
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.width(272.dp),
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 8.dp),
        ) {
            Text(
                user.username,
                style = MaterialTheme.typography.titleSmall,
                color = RenzoColors.Foreground,
                modifier = Modifier.weight(1f),
            )
            Text("🏅 $roleLabel", style = MaterialTheme.typography.labelMedium, color = roleColor)
        }
        if (user.opdsPath.isNotEmpty()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 6.dp),
            ) {
                Icon(
                    Icons.Filled.Sensors, contentDescription = null,
                    tint = RenzoColors.MutedForeground, modifier = Modifier.size(15.dp),
                )
                Text(
                    user.opdsPath,
                    style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    modifier = Modifier.padding(start = 8.dp).weight(1f),
                )
                Icon(
                    Icons.Filled.ContentCopy, contentDescription = "Copy OPDS path",
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(15.dp).clickable(onClick = onCopyOpds),
                )
            }
        }
        HorizontalDivider(color = RenzoColors.Border)
        MenuRow(Icons.Filled.Edit, "Edit…", enabled = false) {}
        MenuRow(Icons.Filled.Key, "Change password…", enabled = false) {}
        MenuRow(Icons.Filled.Sensors, "Trackers…", enabled = false) {}
        MenuRow(Icons.Filled.FolderOpen, "Import Suwayomi Backup…", enabled = false) {}
        HorizontalDivider(color = RenzoColors.Border)
        if (user.level >= UserLevel.ADMIN) MenuRow(Icons.Filled.People, "Users", enabled = false) {}
        MenuRow(Icons.Filled.Key, "Account", enabled = true, onClick = onAccount)
        if (user.level >= UserLevel.OWNER) MenuRow(Icons.Filled.Settings, "Settings", enabled = false) {}
        MenuRow(Icons.Filled.CloudDownload, "Import Series", enabled = false) {}
        HorizontalDivider(color = RenzoColors.Border)
        MenuRow(Icons.Filled.Palette, "Appearance", enabled = false) {}
        MenuRow(Icons.Filled.Explore, "Take a tour", enabled = false) {}
        MenuRow(Icons.Filled.VisibilityOff, "Adult (18+): Hidden", enabled = false) {}
        HorizontalDivider(color = RenzoColors.Border)
        MenuRow(Icons.AutoMirrored.Filled.Logout, "Sign out", enabled = true, onClick = onLogout)
        HorizontalDivider(color = RenzoColors.Border)
        Row(
            horizontalArrangement = Arrangement.spacedBy(26.dp, Alignment.CenterHorizontally),
            modifier = Modifier.fillMaxWidth().padding(vertical = 10.dp),
        ) {
            Icon(Icons.Filled.Language, contentDescription = null, tint = RenzoColors.MutedForeground, modifier = Modifier.size(18.dp))
            Icon(Icons.Filled.Explore, contentDescription = null, tint = RenzoColors.MutedForeground, modifier = Modifier.size(18.dp))
        }
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
            .padding(horizontal = 14.dp, vertical = 10.dp),
    ) {
        Icon(
            icon, contentDescription = null,
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
