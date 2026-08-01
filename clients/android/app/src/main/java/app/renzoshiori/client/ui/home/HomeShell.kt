package app.renzoshiori.client.ui.home

import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.LibraryBooks
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.MonitorHeart
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.MilitaryTech
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.People
import androidx.compose.material.icons.filled.Power
import androidx.compose.material.icons.filled.Route
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.VpnKey
import androidx.compose.material.icons.filled.Warning
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.painter.Painter
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.R
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.DownloadsMetricsDto
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.ui.browse.BrowseScreen
import app.renzoshiori.client.ui.downloads.DownloadsScreen
import app.renzoshiori.client.ui.library.LibraryContent
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.OnlineOfflinePill
import app.renzoshiori.client.ui.onboarding.TourAnchors
import app.renzoshiori.client.ui.onboarding.WalkthroughOverlay
import app.renzoshiori.client.ui.onboarding.tourAnchor
import app.renzoshiori.client.ui.queue.QueueScreen
import app.renzoshiori.client.ui.sources.SourcesScreen
import app.renzoshiori.client.ui.status.StatusScreen
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.updates.UpdatesScreen
import app.renzoshiori.client.ui.util.rememberHideAdult
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Everything the account menu can do. The shell raises these; MainActivity
 * routes them to the matching screen or dialog — 1:1 with the web
 * user-menu.tsx item set.
 */
sealed interface AccountAction {
    data object EditProfile : AccountAction
    data object ChangePassword : AccountAction
    data object Trackers : AccountAction
    data object ImportBackup : AccountAction
    data object Users : AccountAction
    data object Account : AccountAction
    data object ServerSettings : AccountAction
    data class ImportSeries(val titleOnly: Boolean) : AccountAction
    data object Appearance : AccountAction
    data object Tour : AccountAction
    data object SignOut : AccountAction
}

/**
 * Nav sections — the web's useSections() list, in its order, including the
 * native-only Downloads entry (section-pills.tsx gates it on `useIsNative()`,
 * which is always true here).
 */
private enum class Section(val label: String, val icon: ImageVector) {
    Library("Library", Icons.AutoMirrored.Filled.LibraryBooks),
    Updates("Updates", Icons.Filled.Notifications),
    Browse("Browse", Icons.Filled.AutoAwesome),
    Queue("Queue", Icons.AutoMirrored.Filled.List),
    Status("Status", Icons.Filled.MonitorHeart),
    Sources("Sources", Icons.Filled.Power),
    Downloads("Downloads", Icons.Filled.Download),
}

/**
 * The app chrome, transliterated from command-bar.tsx (the <lg branch):
 * a 56dp bar with hamburger, logo + wordmark, expanding search, the
 * Online/Offline pill and the avatar. The hamburger opens the section drawer
 * from the LEFT (the web's Sheet side="left"); the avatar opens the account
 * panel from the RIGHT.
 */
@Composable
fun HomeShell(
    user: UserDto,
    onOpenSeries: (String) -> Unit,
    onOpenOfflineSeries: (String) -> Unit,
    onAccountAction: (AccountAction) -> Unit,
    showTour: Boolean = false,
    onTourFinish: () -> Unit = {},
) {
    var section by rememberSaveable { mutableStateOf(Section.Library.name) }
    var searchOpen by rememberSaveable { mutableStateOf(false) }
    var menuOpen by remember { mutableStateOf(false) }
    val current = Section.valueOf(section)
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val app = context.applicationContext as RenzoApp

    // Queue metrics drive the drawer's Queue badge/live dot and the footer
    // download-status row (web: useDownloadsMetrics).
    var metrics by remember { mutableStateOf(DownloadsMetricsDto()) }
    LaunchedEffect(drawerState.isOpen, current) {
        while (true) {
            runCatching { app.network.currentApi()?.downloadMetrics() }
                .getOrNull()?.let { metrics = it }
            delay(10_000)
        }
    }

    // externalDomain for the full OPDS URL the account panel copies.
    var externalDomain by remember { mutableStateOf("") }
    var importFolder by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        runCatching { app.network.currentApi()?.shellSettings() }.getOrNull()?.let {
            externalDomain = it.externalDomain
            importFolder = it.importFolder
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
                modifier = Modifier.width(288.dp),
            ) {
                NavDrawerContent(
                    current = current,
                    metrics = metrics,
                    offline = libraryState.offlineMode,
                    onToggleOffline = { libraryVm.setOfflineMode(!libraryState.offlineMode) },
                    onSelect = { s ->
                        section = s.name
                        scope.launch { drawerState.close() }
                    },
                    onClose = { scope.launch { drawerState.close() } },
                )
            }
        },
    ) {
        Box(modifier = Modifier.fillMaxSize().background(RenzoColors.Background)) {
            Column(modifier = Modifier.fillMaxSize().statusBarsPadding()) {
                // ── 56dp command bar ─────────────────────────────────────
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().height(56.dp).padding(horizontal = 6.dp),
                ) {
                    IconButton(
                        onClick = { scope.launch { drawerState.open() } },
                        modifier = Modifier.tourAnchor(TourAnchors.NAV),
                    ) {
                        Icon(Icons.Filled.Menu, contentDescription = "Open navigation menu", tint = RenzoColors.Foreground)
                    }
                    Image(
                        painter = painterResource(R.drawable.splash_icon),
                        contentDescription = "Renzo Shiori home",
                        modifier = Modifier.size(28.dp),
                    )
                    if (!searchOpen) {
                        Text(
                            "Renzo Shiori",
                            style = MaterialTheme.typography.titleSmall,
                            color = RenzoColors.Foreground,
                            modifier = Modifier.padding(start = 8.dp),
                        )
                    }
                    Spacer(Modifier.weight(1f))
                    if (searchOpen) {
                        BasicTextField(
                            value = libraryState.searchTerm,
                            onValueChange = libraryVm::setSearch,
                            singleLine = true,
                            textStyle = MaterialTheme.typography.bodyMedium.copy(color = RenzoColors.Foreground),
                            cursorBrush = SolidColor(RenzoColors.Foreground),
                            decorationBox = { inner ->
                                Row(
                                    verticalAlignment = Alignment.CenterVertically,
                                    modifier = Modifier
                                        .width(176.dp)
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
                                                searchPlaceholder(current),
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
                    IconButton(
                        onClick = {
                            if (searchOpen && libraryState.searchTerm.isNotEmpty()) libraryVm.setSearch("")
                            searchOpen = !searchOpen
                        },
                        modifier = Modifier.tourAnchor(TourAnchors.SEARCH),
                    ) {
                        Icon(
                            if (searchOpen) Icons.Filled.Close else Icons.Filled.Search,
                            contentDescription = if (searchOpen) "Close search" else "Open search",
                            tint = RenzoColors.Foreground,
                        )
                    }
                    OnlineOfflinePill(
                        offline = libraryState.offlineMode,
                        onToggle = { libraryVm.setOfflineMode(!libraryState.offlineMode) },
                    )
                    UserAvatar(
                        user = user,
                        size = 32.dp,
                        modifier = Modifier.padding(start = 6.dp, end = 4.dp).tourAnchor(TourAnchors.ACCOUNT),
                        onClick = { menuOpen = true },
                    )
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
                        Section.Status -> StatusScreen(onOpenSeries = onOpenSeries)
                        Section.Sources -> SourcesScreen()
                        Section.Downloads -> DownloadsScreen()
                    }
                }
            }

            // ── Account panel — slides in from the RIGHT ─────────────────
            AccountPanel(
                visible = menuOpen,
                user = user,
                externalDomain = externalDomain,
                importFolderConfigured = importFolder.isNotBlank(),
                onDismiss = { menuOpen = false },
                onAction = { action ->
                    menuOpen = false
                    onAccountAction(action)
                },
            )

            // The walkthrough spotlights real chrome, so it lives over the
            // shell rather than on a route of its own.
            if (showTour) {
                WalkthroughOverlay(onFinish = onTourFinish)
            }
        }
    }
}

private fun searchPlaceholder(section: Section): String = when (section) {
    Section.Library -> "Search series..."
    Section.Sources -> "Search sources..."
    Section.Queue -> "Search queue..."
    else -> "Search..."
}

/* ─── Nav drawer (web: Sheet side="left" + SectionList + footer) ────────── */

@Composable
private fun NavDrawerContent(
    current: Section,
    metrics: DownloadsMetricsDto,
    offline: Boolean,
    onToggleOffline: () -> Unit,
    onSelect: (Section) -> Unit,
    onClose: () -> Unit,
) {
    val context = LocalContext.current
    Column(modifier = Modifier.fillMaxSize().statusBarsPadding()) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 10.dp, bottom = 10.dp),
        ) {
            Image(
                painter = painterResource(R.drawable.renzo_login_banner),
                contentDescription = "Renzo Shiori",
                modifier = Modifier.height(32.dp),
            )
            Spacer(Modifier.weight(1f))
            IconButton(onClick = onClose) {
                Icon(
                    Icons.Filled.Close, contentDescription = "Close",
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.border(1.dp, RenzoColors.Border, CircleShape).padding(6.dp),
                )
            }
        }
        HorizontalDivider(color = RenzoColors.Border)

        // SectionList — rounded-lg rows, active = bg-primary/10 + primary text
        // + a 2px left accent bar, live dot and count badge on Queue.
        Column(
            modifier = Modifier.padding(8.dp).weight(1f).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Section.entries.forEach { s ->
                val active = s == current
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp))
                        .background(if (active) RenzoColors.Primary.copy(alpha = 0.10f) else Color.Transparent)
                        .clickable { onSelect(s) },
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
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp),
                    ) {
                        Icon(
                            s.icon, contentDescription = null,
                            tint = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                            modifier = Modifier.size(20.dp),
                        )
                        Text(
                            s.label,
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                            modifier = Modifier.padding(start = 12.dp).weight(1f),
                        )
                        if (s == Section.Queue) {
                            if (metrics.downloads > 0) {
                                Box(
                                    modifier = Modifier
                                        .padding(end = 8.dp)
                                        .size(6.dp)
                                        .clip(CircleShape)
                                        .background(RenzoColors.Primary),
                                )
                            }
                            val badge = metrics.downloads + metrics.failed
                            if (badge > 0) {
                                Box(
                                    contentAlignment = Alignment.Center,
                                    modifier = Modifier
                                        .clip(RoundedCornerShape(50))
                                        .background(RenzoColors.Primary)
                                        .padding(horizontal = 7.dp, vertical = 2.dp),
                                ) {
                                    Text(
                                        if (badge > 99) "99+" else badge.toString(),
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

        // Drawer footer — view-mode pill, download status, project links.
        HorizontalDivider(color = RenzoColors.Border)
        Column(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 12.dp).navigationBarsPadding(),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            OnlineOfflinePill(offline = offline, onToggle = onToggleOffline)
            Row(
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border.copy(alpha = 0.6f), RoundedCornerShape(8.dp))
                    .background(RenzoColors.Muted.copy(alpha = 0.3f))
                    .padding(vertical = 8.dp),
            ) {
                StatChip(Icons.Filled.Download, metrics.downloads.toString(), RenzoColors.Blue)
                StatChip(Icons.Filled.Schedule, metrics.queued.toString(), RenzoColors.Yellow)
                StatChip(Icons.Filled.Warning, metrics.failed.toString(), RenzoColors.Red)
            }
            ExternalLinksRow(context = context)
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
            modifier = Modifier.padding(start = 6.dp),
        )
    }
}

/** external-links.tsx — GitHub / Discord / Website, verbatim hrefs. */
@Composable
private fun ExternalLinksRow(context: android.content.Context) {
    val links = listOf(
        Triple("GitHub", "https://github.com/Levitate0/Renzo", R.drawable.ic_github),
        Triple("Discord", "https://discord.gg/AvhtPPV8", R.drawable.ic_discord),
        Triple("Website", "https://www.renzo.net", R.drawable.ic_globe),
    )
    Row(
        horizontalArrangement = Arrangement.spacedBy(4.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth(),
    ) {
        links.forEach { (name, href, res) ->
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .size(32.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .clickable {
                        runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(href))) }
                    },
            ) {
                Icon(
                    painterResource(res), contentDescription = name,
                    tint = RenzoColors.MutedForeground, modifier = Modifier.size(16.dp),
                )
            }
        }
    }
}

/* ─── Account panel (web: UserAvatarDropdown content) ───────────────────── */

@Composable
private fun UserAvatar(
    user: UserDto,
    size: androidx.compose.ui.unit.Dp,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val avatar: Painter? = remember(user.avatarBase64) {
        user.avatarBase64?.takeIf { it.isNotBlank() }?.let { b64 ->
            runCatching {
                val bytes = android.util.Base64.decode(b64, android.util.Base64.DEFAULT)
                val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                androidx.compose.ui.graphics.painter.BitmapPainter(bmp.asImageBitmap())
            }.getOrNull()
        }
    }
    Box(
        contentAlignment = Alignment.Center,
        modifier = modifier
            .size(size)
            .clip(CircleShape)
            .background(RenzoColors.Primary.copy(alpha = 0.20f))
            .border(1.dp, RenzoColors.Primary.copy(alpha = 0.30f), CircleShape)
            .clickable(onClick = onClick),
    ) {
        if (avatar != null) {
            Image(
                painter = avatar,
                contentDescription = user.username,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize().clip(CircleShape),
            )
        } else {
            Text(
                user.username.take(2).uppercase(),
                style = MaterialTheme.typography.labelSmall,
                color = RenzoColors.Primary,
            )
        }
    }
}

@Composable
private fun BoxScope.AccountPanel(
    visible: Boolean,
    user: UserDto,
    externalDomain: String,
    importFolderConfigured: Boolean,
    onDismiss: () -> Unit,
    onAction: (AccountAction) -> Unit,
) {
    val context = LocalContext.current
    val clipboard = LocalClipboardManager.current
    val hideAdult = rememberHideAdult(context)
    var copied by remember { mutableStateOf(false) }
    var importPickerOpen by remember { mutableStateOf(false) }

    LaunchedEffect(copied) {
        if (copied) { delay(2000); copied = false }
    }

    val domain = externalDomain.ifBlank { "http://localhost:9833" }
    val fullOpdsUrl = "${domain.trimEnd('/')}/${user.opdsPath}"

    AnimatedVisibility(visible = visible, enter = fadeIn(), exit = fadeOut()) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.55f))
                .clickable(
                    interactionSource = remember { MutableInteractionSource() },
                    indication = null,
                    onClick = onDismiss,
                ),
        )
    }
    AnimatedVisibility(
        visible = visible,
        enter = slideInHorizontally(initialOffsetX = { it }),
        exit = slideOutHorizontally(targetOffsetX = { it }),
        modifier = Modifier.align(Alignment.CenterEnd),
    ) {
        Column(
            modifier = Modifier
                .width(300.dp)
                .fillMaxHeight()
                .background(RenzoColors.Popover)
                .statusBarsPadding()
                .verticalScroll(rememberScrollState())
                .navigationBarsPadding(),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 10.dp, bottom = 10.dp),
            ) {
                UserAvatar(user = user, size = 28.dp, onClick = {})
                Text(
                    "Account",
                    style = MaterialTheme.typography.titleSmall,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.padding(start = 10.dp).weight(1f),
                )
                IconButton(onClick = onDismiss) {
                    Icon(
                        Icons.Filled.Close, contentDescription = "Close",
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.border(1.dp, RenzoColors.Border, CircleShape).padding(6.dp),
                    )
                }
            }
            HorizontalDivider(color = RenzoColors.Border)

            // Username + role badge (LEVEL_LABEL / LEVEL_BADGE).
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
            ) {
                Text(
                    user.username,
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.Foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                val (roleLabel, roleColor) = when (user.level) {
                    UserLevel.OWNER -> "Owner" to RenzoColors.Primary
                    UserLevel.ADMIN -> "Admin" to Color(0xFFFCD34D)
                    UserLevel.MANAGER -> "Manager" to Color(0xFFD8B4FE)
                    else -> "User" to Color(0xFF93C5FD)
                }
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .background(roleColor.copy(alpha = 0.15f))
                        .padding(horizontal = 8.dp, vertical = 2.dp),
                ) {
                    Icon(
                        Icons.Filled.MilitaryTech, contentDescription = null,
                        tint = roleColor, modifier = Modifier.size(12.dp),
                    )
                    Text(
                        roleLabel,
                        style = MaterialTheme.typography.labelSmall,
                        color = roleColor,
                        modifier = Modifier.padding(start = 4.dp),
                    )
                }
            }
            HorizontalDivider(color = RenzoColors.Border)

            // OPDS path + copy-the-full-URL button.
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
            ) {
                Icon(
                    Icons.Filled.Route, contentDescription = null,
                    tint = RenzoColors.MutedForeground, modifier = Modifier.size(16.dp),
                )
                Text(
                    user.opdsPath,
                    style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(horizontal = 8.dp).weight(1f),
                )
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .size(24.dp)
                        .clip(RoundedCornerShape(6.dp))
                        .clickable {
                            clipboard.setText(AnnotatedString(fullOpdsUrl))
                            copied = true
                        },
                ) {
                    Icon(
                        if (copied) Icons.Filled.Check else Icons.Filled.ContentCopy,
                        contentDescription = "Copy OPDS URL",
                        tint = if (copied) RenzoColors.Green else RenzoColors.MutedForeground,
                        modifier = Modifier.size(14.dp),
                    )
                }
            }
            HorizontalDivider(color = RenzoColors.Border)

            MenuRow(Icons.Filled.Edit, "Edit...") { onAction(AccountAction.EditProfile) }
            if (user.hasPassword) {
                MenuRow(Icons.Filled.VpnKey, "Change password...") { onAction(AccountAction.ChangePassword) }
            }
            MenuRow(Icons.Filled.Sensors, "Trackers...") { onAction(AccountAction.Trackers) }
            MenuRow(Icons.Filled.Download, "Import Suwayomi Backup...") { onAction(AccountAction.ImportBackup) }

            HorizontalDivider(color = RenzoColors.Border)

            if (user.level >= UserLevel.ADMIN) {
                MenuRow(Icons.Filled.People, "Users") { onAction(AccountAction.Users) }
            }
            MenuRow(Icons.Filled.VpnKey, "Account") { onAction(AccountAction.Account) }
            if (user.level >= UserLevel.OWNER) {
                MenuRow(Icons.Filled.Settings, "Settings") { onAction(AccountAction.ServerSettings) }
            }
            if (user.level >= UserLevel.MANAGER) {
                MenuRow(Icons.Filled.Download, "Import Series") { importPickerOpen = true }
                HorizontalDivider(color = RenzoColors.Border)
            }

            MenuRow(Icons.Filled.Palette, "Appearance") { onAction(AccountAction.Appearance) }
            MenuRow(Icons.Filled.Route, "Take a tour") { onAction(AccountAction.Tour) }
            MenuRow(
                if (hideAdult.hidden.value) Icons.Filled.VisibilityOff else Icons.Filled.Visibility,
                "Adult (18+): ${if (hideAdult.hidden.value) "Hidden" else "Shown"}",
            ) { hideAdult.toggle() }

            HorizontalDivider(color = RenzoColors.Border)
            MenuRow(Icons.AutoMirrored.Filled.Logout, "Sign out") { onAction(AccountAction.SignOut) }
            HorizontalDivider(color = RenzoColors.Border)
            Box(modifier = Modifier.padding(horizontal = 8.dp, vertical = 6.dp)) {
                ExternalLinksRow(context = context)
            }
        }
    }

    if (importPickerOpen) {
        ImportSeriesPicker(
            importFolderConfigured = importFolderConfigured,
            onDismiss = { importPickerOpen = false },
            onPick = { titleOnly ->
                importPickerOpen = false
                onAction(AccountAction.ImportSeries(titleOnly))
            },
        )
    }
}

@Composable
private fun MenuRow(icon: ImageVector, label: String, onClick: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 11.dp),
    ) {
        Icon(icon, contentDescription = null, tint = RenzoColors.Foreground, modifier = Modifier.size(16.dp))
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = RenzoColors.Foreground,
            modifier = Modifier.padding(start = 10.dp),
        )
    }
}

/**
 * The web's "Import Series" ResponsiveModal — two option cards. The
 * titles-only option is only offered when the server actually has an import
 * folder mounted (the web disables it; we hide it instead of dimming).
 */
@Composable
private fun ImportSeriesPicker(
    importFolderConfigured: Boolean,
    onDismiss: () -> Unit,
    onPick: (Boolean) -> Unit,
) {
    Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(RenzoColors.Popover)
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text("Import Series", style = MaterialTheme.typography.titleMedium, color = RenzoColors.Foreground)
            Text(
                "Choose how to scan your library folder.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
            )
            ImportOption(
                icon = Icons.Filled.Download,
                title = "Regular Import",
                description = "Scan the library folder for existing archives (CBZ/CBR).",
                onClick = { onPick(false) },
            )
            if (importFolderConfigured) {
                ImportOption(
                    icon = Icons.Filled.Download,
                    title = "Import Titles Only (e.g. from Suwayomi)",
                    description = "Register bare titles from a folder with no archives yet " +
                        "(e.g. loose-image chapters), then auto-match them online.",
                    onClick = { onPick(true) },
                )
            }
        }
    }
}

@Composable
private fun ImportOption(icon: ImageVector, title: String, description: String, onClick: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, contentDescription = null, tint = RenzoColors.Foreground, modifier = Modifier.size(16.dp))
            Text(
                title,
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.Foreground,
                modifier = Modifier.padding(start = 8.dp),
            )
        }
        Text(description, style = MaterialTheme.typography.bodySmall, color = RenzoColors.MutedForeground)
    }
}
