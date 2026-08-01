package app.renzoshiori.client.ui.home

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
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.BasicAlertDialog
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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.R
import app.renzoshiori.client.ui.browse.BrowseScreen
import app.renzoshiori.client.ui.library.LibraryContent
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.OnlineOfflinePill
import app.renzoshiori.client.ui.queue.QueueScreen
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.updates.UpdatesScreen
import kotlinx.coroutines.launch

private enum class Section(val label: String) {
    Library("Library"),
    Updates("Updates"),
    Browse("Browse"),
    Queue("Queue"),
}

/**
 * 1:1 Compose clone of the web frontend's mobile chrome (command-bar.tsx at
 * the <lg breakpoint): a 56dp top bar — hamburger, torii logo + wordmark,
 * expanding search, Online/Offline pill, initials avatar — with the section
 * list in a left nav drawer headed by the banner art, exactly like the web
 * Sheet drawer. Sections render below the bar; series/reader/account push
 * onto the nav stack above this shell.
 */
@Composable
fun HomeShell(
    username: String,
    onOpenSeries: (String) -> Unit,
    onOpenOfflineSeries: (String) -> Unit,
    onOpenAccount: () -> Unit,
) {
    var section by rememberSaveable { mutableStateOf(Section.Library.name) }
    var searchOpen by rememberSaveable { mutableStateOf(false) }
    val current = Section.valueOf(section)
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()

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
                // Banner header, like the web drawer's top block.
                Box(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
                    Image(
                        painter = painterResource(R.drawable.renzo_login_banner),
                        contentDescription = "Renzo Shiori",
                        modifier = Modifier.height(32.dp),
                    )
                }
                HorizontalDivider(color = RenzoColors.Border)
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
                                }
                                .padding(horizontal = 12.dp, vertical = 11.dp),
                        ) {
                            Text(
                                s.label,
                                style = MaterialTheme.typography.bodyMedium,
                                color = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                            )
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
                    // Expanding inline search — web: w-44 h-9 rounded-lg bg-muted/70.
                    androidx.compose.foundation.text.BasicTextField(
                        value = libraryState.searchTerm,
                        onValueChange = libraryVm::setSearch,
                        singleLine = true,
                        textStyle = MaterialTheme.typography.bodyMedium.copy(color = RenzoColors.Foreground),
                        cursorBrush = androidx.compose.ui.graphics.SolidColor(RenzoColors.Foreground),
                        decorationBox = { inner ->
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .weight(1f, fill = false)
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
                // Initials avatar — web: rounded-full bg-primary/20 border-primary/30.
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .padding(start = 6.dp, end = 4.dp)
                        .size(32.dp)
                        .clip(CircleShape)
                        .background(RenzoColors.Primary.copy(alpha = 0.20f))
                        .border(1.dp, RenzoColors.Primary.copy(alpha = 0.30f), CircleShape)
                        .clickable(onClick = onOpenAccount),
                ) {
                    Text(
                        username.take(2).uppercase(),
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
