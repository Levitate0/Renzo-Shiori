package app.renzoshiori.client.ui.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.browse.BrowseScreen
import app.renzoshiori.client.ui.library.LibraryContent
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.OnlineOfflinePill
import app.renzoshiori.client.ui.queue.QueueScreen
import app.renzoshiori.client.ui.updates.UpdatesScreen
import androidx.compose.runtime.collectAsState
import androidx.lifecycle.viewmodel.compose.viewModel

private enum class Section(val label: String) {
    Library("Library"),
    Updates("Updates"),
    Browse("Browse"),
    Queue("Queue"),
}

/**
 * The app shell, matching the web frontend's command bar: a top row with the
 * app name, the Online/Offline pill, and the account avatar, then the section
 * pill bar (Library · Updates · Browse · Queue — active pill filled with the
 * brand primary exactly like section-pills.tsx). Sections render below;
 * series/reader/account push onto the nav stack above this shell.
 */
@Composable
fun HomeShell(
    onOpenSeries: (String) -> Unit,
    onOpenOfflineSeries: (String) -> Unit,
    onOpenAccount: () -> Unit,
) {
    var section by rememberSaveable { mutableStateOf(Section.Library.name) }
    val current = Section.valueOf(section)

    val libraryVm: LibraryViewModel = viewModel(
        factory = LibraryViewModel.factory(
            androidx.compose.ui.platform.LocalContext.current.applicationContext as android.app.Application,
        ),
    )
    val libraryState by libraryVm.state.collectAsState()

    Column(modifier = Modifier.fillMaxSize().statusBarsPadding()) {
        // ── Top bar: brand · pill · avatar ──────────────────────────────
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 4.dp),
        ) {
            Text(
                "Renzo Shiori",
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onBackground,
            )
            Spacer(Modifier.weight(1f))
            OnlineOfflinePill(
                offline = libraryState.offlineMode,
                onToggle = { libraryVm.setOfflineMode(!libraryState.offlineMode) },
            )
            IconButton(onClick = onOpenAccount) {
                Icon(
                    Icons.Filled.AccountCircle,
                    contentDescription = "Account",
                    tint = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.8f),
                )
            }
        }

        // ── Section pills (web section-pills.tsx equivalent) ───────────
        Row(
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 12.dp, vertical = 6.dp),
        ) {
            Section.entries.forEach { s ->
                val active = s == current
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .background(
                            if (active) MaterialTheme.colorScheme.primary
                            else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.05f),
                        )
                        .clickable { section = s.name }
                        .padding(horizontal = 14.dp, vertical = 7.dp),
                ) {
                    Text(
                        s.label,
                        style = MaterialTheme.typography.labelLarge,
                        color = if (active) MaterialTheme.colorScheme.onPrimary
                        else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f),
                    )
                }
            }
        }

        // ── Active section ───────────────────────────────────────────────
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
