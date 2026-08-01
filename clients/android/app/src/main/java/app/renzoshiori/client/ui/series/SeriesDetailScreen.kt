package app.renzoshiori.client.ui.series

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.DownloadDone
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.data.model.ReaderChapterDto
import app.renzoshiori.client.data.model.SeriesInfoDto
import app.renzoshiori.client.data.model.SeriesStatus
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.library.formatChapter
import coil3.compose.AsyncImage

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SeriesDetailScreen(
    seriesId: String,
    onBack: () -> Unit,
    onReadChapter: (seriesId: String, chapterNumber: Double) -> Unit,
    vm: SeriesDetailViewModel = viewModel(
        key = "series-$seriesId",
        factory = SeriesDetailViewModel.factory(
            androidx.compose.ui.platform.LocalContext.current.applicationContext as android.app.Application,
            seriesId,
        ),
    ),
) {
    val state by vm.state.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(state.title.ifEmpty { "Series" }, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    // Save every not-yet-offline downloaded chapter for offline.
                    IconButton(onClick = { vm.saveOffline(state.chapters) }) {
                        Icon(Icons.Filled.Download, contentDescription = "Save all offline")
                    }
                },
            )
        },
    ) { padding ->
        when {
            state.loading -> Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            state.error != null -> Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                Text(state.error!!, color = MaterialTheme.colorScheme.error)
            }
            else -> LazyColumn(modifier = Modifier.fillMaxSize().padding(padding)) {
                if (state.info != null) {
                    item(key = "hero") { SeriesHero(state.info!!, vm.baseUrl) }
                }
                items(state.chapters, key = { it.number }) { ch ->
                    ChapterRow(
                        chapter = ch,
                        isOffline = chapterKey(seriesId, ch.number) in state.offlineKeys,
                        onClick = { onReadChapter(seriesId, ch.number) },
                        onToggleRead = { vm.markRead(listOf(ch.number), !ch.isCompleted) },
                        onSaveOffline = { vm.saveOffline(listOf(ch)) },
                    )
                    HorizontalDivider(color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.06f))
                }
            }
        }
    }
}

@Composable
private fun ChapterRow(
    chapter: ReaderChapterDto,
    isOffline: Boolean,
    onClick: () -> Unit,
    onToggleRead: () -> Unit,
    onSaveOffline: () -> Unit,
) {
    val dim = chapter.isCompleted
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 10.dp),
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "Ch. ${formatChapter(chapter.number)}",
                    style = MaterialTheme.typography.titleSmall,
                    color = if (dim) MaterialTheme.colorScheme.onBackground.copy(alpha = 0.4f)
                    else MaterialTheme.colorScheme.onBackground,
                )
                if (chapter.locked) {
                    Spacer(Modifier.size(6.dp))
                    Icon(
                        Icons.Filled.Lock,
                        contentDescription = "Locked",
                        modifier = Modifier.size(13.dp),
                        tint = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.5f),
                    )
                }
            }
            if (chapter.name.isNotEmpty()) {
                Text(
                    chapter.name,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onBackground.copy(alpha = if (dim) 0.3f else 0.6f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            if (!chapter.isCompleted && chapter.progress > 0f) {
                LinearProgressIndicator(
                    progress = { chapter.progress },
                    modifier = Modifier.fillMaxWidth().padding(top = 4.dp, end = 16.dp),
                )
            }
        }

        if (isOffline) {
            Icon(
                Icons.Filled.DownloadDone,
                contentDescription = "Saved offline",
                tint = Color_Emerald,
                modifier = Modifier.size(18.dp).padding(end = 2.dp),
            )
        } else if (chapter.filename != null) {
            IconButton(onClick = onSaveOffline) {
                Icon(
                    Icons.Filled.Download,
                    contentDescription = "Save offline",
                    modifier = Modifier.size(18.dp),
                    tint = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.5f),
                )
            }
        }

        IconButton(onClick = onToggleRead) {
            Icon(
                Icons.Filled.Check,
                contentDescription = if (chapter.isCompleted) "Mark unread" else "Mark read",
                tint = if (chapter.isCompleted) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.3f),
            )
        }
    }
}

private val Color_Emerald = androidx.compose.ui.graphics.Color(0xFF10B981)

/**
 * Hero block above the chapter list — cover, status pill, author, genre
 * chips, and a collapsible description. Mirrors the web series page's hero;
 * the expanded description caps its height and scrolls (same fix the web
 * hero needed for sources that ship walls of scraped SEO text).
 */
@Composable
private fun SeriesHero(info: SeriesInfoDto, baseUrl: String) {
    var expanded by remember { mutableStateOf(false) }

    Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
        Row {
            Box(
                modifier = Modifier
                    .width(110.dp)
                    .aspectRatio(2f / 3f)
                    .clip(RoundedCornerShape(8.dp))
                    .background(MaterialTheme.colorScheme.surface),
            ) {
                if (info.thumbnailUrl.isNotEmpty()) {
                    AsyncImage(
                        model = absoluteUrl(baseUrl, info.thumbnailUrl),
                        contentDescription = info.title,
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                }
            }
            Column(modifier = Modifier.padding(start = 14.dp).weight(1f)) {
                // Status pill, like the web hero's "● ONGOING" chip.
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.15f))
                        .padding(horizontal = 10.dp, vertical = 3.dp),
                ) {
                    Text(
                        SeriesStatus.label(info.status).uppercase(),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.primary,
                    )
                }
                Text(
                    info.title,
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.padding(top = 6.dp),
                )
                if (!info.author.isNullOrEmpty()) {
                    Text(
                        info.author,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f),
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
                Text(
                    "${info.chapterCount} chapters",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.5f),
                    modifier = Modifier.padding(top = 2.dp),
                )
                if (info.genre.isNotEmpty()) {
                    Text(
                        info.genre.take(4).joinToString("  ·  "),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.45f),
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = 6.dp),
                    )
                }
            }
        }
        if (!info.description.isNullOrBlank()) {
            val desc = info.description
            Text(
                desc,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.65f),
                maxLines = if (expanded) Int.MAX_VALUE else 3,
                overflow = TextOverflow.Ellipsis,
                modifier = if (expanded) {
                    Modifier
                        .padding(top = 12.dp)
                        .heightIn(max = 260.dp)
                        .verticalScroll(rememberScrollState())
                } else {
                    Modifier.padding(top = 12.dp)
                },
            )
            if (desc.length > 160) {
                Text(
                    if (expanded) "Show less" else "Read more",
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier
                        .padding(top = 4.dp)
                        .clickable { expanded = !expanded },
                )
            }
        }
    }
}
