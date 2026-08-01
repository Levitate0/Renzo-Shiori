package app.renzoshiori.client.ui.series

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.offline.OfflineRepository
import app.renzoshiori.client.ui.library.formatChapter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Chapter list for a series in OFFLINE mode — everything on this screen comes
 * from the on-device manifest, no network at all. Rows open the reader (which
 * prefers offline page bytes automatically); the trash icon deletes a saved
 * chapter and reclaims its space.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun OfflineSeriesScreen(
    seriesId: String,
    onBack: () -> Unit,
    onReadChapter: (seriesId: String, chapterNumber: Double) -> Unit,
) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var chapters by remember { mutableStateOf<List<OfflineRepository.OfflineChapter>>(emptyList()) }
    var title by remember { mutableStateOf("") }
    var reloadTick by remember { mutableStateOf(0) }

    LaunchedEffect(seriesId, reloadTick) {
        withContext(Dispatchers.IO) {
            chapters = app.offline.listChapters(seriesId)
            title = chapters.firstOrNull()?.seriesTitle ?: ""
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(title.ifEmpty { "Offline chapters" }, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
            )
        },
    ) { padding ->
        LazyColumn(modifier = Modifier.fillMaxSize().padding(padding)) {
            items(chapters, key = { it.chapterKey }) { ch ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { onReadChapter(ch.seriesId, ch.chapterNumber) }
                        .padding(horizontal = 16.dp, vertical = 12.dp),
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text("Ch. ${formatChapter(ch.chapterNumber)}", style = MaterialTheme.typography.titleSmall)
                        Text(
                            "${ch.pageCount} pages · ${formatBytes(ch.bytes)}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.55f),
                        )
                    }
                    IconButton(onClick = {
                        scope.launch(Dispatchers.IO) {
                            app.offline.deleteChapter(ch.chapterKey)
                            withContext(Dispatchers.Main) { reloadTick++ }
                        }
                    }) {
                        Icon(
                            Icons.Filled.Delete,
                            contentDescription = "Delete download",
                            tint = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.5f),
                        )
                    }
                }
                HorizontalDivider(color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.06f))
            }
        }
    }
}

private fun formatBytes(n: Long): String = when {
    n < 1024 -> "$n B"
    n < 1024 * 1024 -> "${n / 1024} KB"
    n < 1024L * 1024 * 1024 -> "%.1f MB".format(n / (1024.0 * 1024))
    else -> "%.2f GB".format(n / (1024.0 * 1024 * 1024))
}
