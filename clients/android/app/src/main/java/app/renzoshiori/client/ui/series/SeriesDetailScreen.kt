package app.renzoshiori.client.ui.series

import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Smartphone
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.outlined.Circle
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
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
            else -> LazyColumn(
                modifier = Modifier.fillMaxSize().padding(padding),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
                verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp),
            ) {
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
                }
            }
        }
    }
}

/**
 * Transliteration of the web ChapterRow (chapter-row.tsx): a bordered
 * card-tinted row — status icon (downloaded ✓ emerald / locked 🔒 violet /
 * missing ⚠ amber), number + name + progress% chip + bookmark, then the
 * "from <source> · <date>" / "Locked · purchase on source" / "Missing"
 * subtitle line, with the save-offline and mark-read icon actions on the
 * right. Read chapters grey the text block at 50% like the web row.
 */
@Composable
private fun ChapterRow(
    chapter: ChapterRowUi,
    isOffline: Boolean,
    onClick: () -> Unit,
    onToggleRead: () -> Unit,
    onSaveOffline: () -> Unit,
) {
    val muted = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.64f)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.5f))
            .androidBorder()
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        // Status icon — same trio/colors as the web row.
        when {
            chapter.downloaded -> Icon(
                Icons.Filled.CheckCircle, contentDescription = "Downloaded",
                tint = Color_Emerald, modifier = Modifier.size(16.dp),
            )
            chapter.locked -> Icon(
                Icons.Filled.Lock, contentDescription = "Locked",
                tint = Color_Violet, modifier = Modifier.size(16.dp),
            )
            else -> Icon(
                Icons.Filled.Warning, contentDescription = "Missing",
                tint = Color_Amber, modifier = Modifier.size(16.dp),
            )
        }

        Column(
            modifier = Modifier
                .weight(1f)
                .padding(start = 12.dp)
                .let { if (chapter.isCompleted) it.alpha(0.5f) else it },
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    formatChapter(chapter.number),
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onBackground,
                )
                if (chapter.name.isNotEmpty()) {
                    Text(
                        chapter.name,
                        style = MaterialTheme.typography.bodyMedium,
                        color = muted,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 8.dp).weight(1f, fill = false),
                    )
                }
                if (chapter.bookmarked) {
                    Icon(
                        Icons.Filled.Bookmark, contentDescription = "Bookmarked",
                        tint = Color_Pink, modifier = Modifier.padding(start = 6.dp).size(12.dp),
                    )
                }
                if (!chapter.isCompleted && chapter.progress > 0f) {
                    Text(
                        "${(chapter.progress * 100).toInt()}%",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier
                            .padding(start = 6.dp)
                            .clip(RoundedCornerShape(4.dp))
                            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.15f))
                            .padding(horizontal = 4.dp, vertical = 1.dp),
                    )
                }
            }
            // Subtitle line: "from <source>" / "Locked · purchase on source" / "Missing", " · date".
            Row(modifier = Modifier.padding(top = 2.dp)) {
                val subtitleStyle = MaterialTheme.typography.labelSmall
                when {
                    chapter.downloaded -> Text(
                        buildAnnotatedString {
                            append("from ")
                            withStyle(SpanStyle(color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.8f))) {
                                append(chapter.sourceProviderName ?: "unknown source")
                            }
                        },
                        style = subtitleStyle, color = muted, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                    chapter.locked -> Text("Locked · purchase on source", style = subtitleStyle, color = Color_Violet)
                    else -> Text("Missing", style = subtitleStyle, color = Color_Amber)
                }
                formatUploadDate(chapter.uploadDate)?.let {
                    Text(" · $it", style = subtitleStyle, color = muted.copy(alpha = 0.7f), maxLines = 1)
                }
            }
        }

        // Save-offline action (native-only concept — phone icon when saved).
        if (isOffline) {
            Icon(
                Icons.Filled.Smartphone, contentDescription = "Saved on this device",
                tint = Color_Emerald, modifier = Modifier.padding(horizontal = 8.dp).size(16.dp),
            )
        } else if (chapter.downloaded) {
            IconButton(onClick = onSaveOffline, modifier = Modifier.size(32.dp)) {
                Icon(
                    Icons.Filled.CloudDownload, contentDescription = "Save offline",
                    tint = muted, modifier = Modifier.size(16.dp),
                )
            }
        }

        // Mark read/unread toggle — circle / filled check circle, like the web.
        IconButton(onClick = onToggleRead, modifier = Modifier.size(32.dp)) {
            if (chapter.isCompleted) {
                Icon(
                    Icons.Filled.CheckCircle, contentDescription = "Mark unread",
                    tint = Color_Emerald, modifier = Modifier.size(16.dp),
                )
            } else {
                Icon(
                    Icons.Outlined.Circle, contentDescription = "Mark read",
                    tint = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.35f),
                    modifier = Modifier.size(16.dp),
                )
            }
        }
    }
}

/** Web row border: border-border/40. */
@Composable
private fun Modifier.androidBorder(): Modifier =
    this.then(
        Modifier.border(
            1.dp,
            MaterialTheme.colorScheme.outline.copy(alpha = 0.4f),
            RoundedCornerShape(8.dp),
        ),
    )

/** "Jun 8, 2026" — the web row's short upload-date label. */
private fun formatUploadDate(iso: String?): String? {
    if (iso.isNullOrEmpty()) return null
    return runCatching {
        val date = java.time.LocalDate.parse(iso.take(10))
        date.format(java.time.format.DateTimeFormatter.ofPattern("MMM d, yyyy"))
    }.getOrNull()
}

private val Color_Emerald = androidx.compose.ui.graphics.Color(0xFF10B981)
private val Color_Violet = androidx.compose.ui.graphics.Color(0xFFA78BFA) // web violet-400
private val Color_Amber = androidx.compose.ui.graphics.Color(0xFFF59E0B) // web amber-500
private val Color_Pink = androidx.compose.ui.graphics.Color(0xFFEC4899) // web pink-500

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
