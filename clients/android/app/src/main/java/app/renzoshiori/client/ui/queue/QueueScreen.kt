package app.renzoshiori.client.ui.queue

import android.app.Application
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.TextUnitType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.DownloadInfoDto
import app.renzoshiori.client.data.model.QueueStatus
import app.renzoshiori.client.data.network.ErrorDownloadAction
import app.renzoshiori.client.data.network.QueueApi
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.components.RibbonToggleChip
import app.renzoshiori.client.ui.components.SegmentedPills
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.formatChapter
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// ---------------------------------------------------------------------------
// Constants (queue/page.tsx)
// ---------------------------------------------------------------------------

private const val LIST_FETCH_LIMIT = 5000
private const val MAX_VISIBLE = 500

private val BUCKET_ORDER = listOf(
    DateBucket.TODAY, DateBucket.YESTERDAY, DateBucket.THIS_WEEK, DateBucket.EARLIER,
)

private val FILTER_LABELS = listOf("All", "Completed", "Failed", "Queued")

private enum class RowStatus { DOWNLOADING, QUEUED, COMPLETED, FAILED }

private data class QueueRowItem(
    val id: String,
    val status: RowStatus,
    val seriesTitle: String,
    val chapterLabel: String,
    val thumbnailUrl: String?,
    val provider: String?,
    val scanlator: String?,
    val url: String?,
    val sortTime: Long,
    val displayTime: String,
    val hasRetry: Boolean,
    val retries: Int = 0,
    val progress: Int? = null,
)

/** Status dot colors, verbatim from queue-row.tsx DOT_STYLES. */
private fun dotColor(status: RowStatus): Color = when (status) {
    RowStatus.COMPLETED -> Color(0xFF31C46B)     // hsl(142 60% 48%)
    RowStatus.FAILED -> Color(0xFFD84343)        // hsl(0 68% 55%)
    RowStatus.DOWNLOADING -> Color(0xFF3B8FEE)   // hsl(210 85% 58%)
    RowStatus.QUEUED -> Color(0xFFEEA51F)        // hsl(38 88% 55%)
}

/**
 * Queue — 1:1 port of RenzoFrontend src/app/queue/page.tsx: the All /
 * Completed / Failed / Queued segmented ribbon, the "Queue <count>" header with
 * the Owner-only My-library toggle and the Jobs dialog, the four date buckets,
 * and the queue rows with their status dot, retry counter, inline Retry link,
 * open-source / retry / remove actions and the downloading progress bar.
 *
 * The web gets live "downloading" rows over SignalR; natively we poll the
 * Running queue on the same 5s cadence the web polls the other three lists.
 */
@Composable
fun QueueScreen() {
    val context = LocalContext.current
    val renzoApp = context.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    val uriHandler = LocalUriHandler.current

    // Shared search context — the shell's command bar writes into LibraryViewModel.
    val libraryVm: LibraryViewModel = viewModel(
        factory = LibraryViewModel.factory(context.applicationContext as Application),
    )
    val libraryState by libraryVm.state.collectAsState()
    val search = libraryState.searchTerm.trim()

    val api = remember { renzoApp.network.currentServiceOf<QueueApi>() }
    val baseUrl = renzoApp.tokenStore.serverUrl ?: ""

    var filterIndex by remember { mutableStateOf(0) }
    var viewAllLibraries by remember { mutableStateOf(false) }
    var jobsOpen by remember { mutableStateOf(false) }
    var isLoading by remember { mutableStateOf(true) }
    var refreshTick by remember { mutableStateOf(0) }

    var running by remember { mutableStateOf<List<DownloadInfoDto>>(emptyList()) }
    var waiting by remember { mutableStateOf<List<DownloadInfoDto>>(emptyList()) }
    var completed by remember { mutableStateOf<List<DownloadInfoDto>>(emptyList()) }
    var failed by remember { mutableStateOf<List<DownloadInfoDto>>(emptyList()) }

    val effectiveViewAll = libraryState.canOwner && viewAllLibraries
    val keyword = search.ifBlank { null }

    LaunchedEffect(effectiveViewAll, keyword, refreshTick) {
        runCatching {
            running = api?.downloads(QueueStatus.RUNNING, LIST_FETCH_LIMIT, keyword, effectiveViewAll)?.downloads.orEmpty()
            waiting = api?.downloads(QueueStatus.WAITING, LIST_FETCH_LIMIT, keyword, effectiveViewAll)?.downloads.orEmpty()
            completed = api?.downloads(QueueStatus.COMPLETED, LIST_FETCH_LIMIT, keyword, effectiveViewAll)?.downloads.orEmpty()
            failed = api?.downloads(QueueStatus.FAILED, LIST_FETCH_LIMIT, keyword, effectiveViewAll)?.downloads.orEmpty()
        }
        isLoading = false
        delay(5000)
        refreshTick++
    }

    fun reload() {
        refreshTick++
    }

    val allItems = remember(running, waiting, completed, failed) {
        val items = ArrayList<QueueRowItem>()

        // Active — always "today".
        running.forEach { d ->
            items.add(
                QueueRowItem(
                    id = d.id,
                    status = RowStatus.DOWNLOADING,
                    seriesTitle = d.title,
                    chapterLabel = chapterLabelOf(d),
                    thumbnailUrl = d.thumbnailUrl,
                    provider = d.provider,
                    scanlator = d.scanlator,
                    url = d.url,
                    sortTime = 0L,
                    displayTime = "downloading",
                    hasRetry = false,
                ),
            )
        }
        // Waiting / queued — queued items go to today.
        waiting.forEach { d ->
            items.add(
                QueueRowItem(
                    id = d.id,
                    status = RowStatus.QUEUED,
                    seriesTitle = d.title,
                    chapterLabel = chapterLabelOf(d),
                    thumbnailUrl = d.thumbnailUrl,
                    provider = d.provider,
                    scanlator = d.scanlator,
                    url = d.url,
                    sortTime = 0L,
                    displayTime = "queued",
                    hasRetry = false,
                    retries = d.retries,
                ),
            )
        }
        // Completed.
        completed.forEach { d ->
            val parsed = d.downloadDateUTC?.let(::parseUtcMillis)
            items.add(
                QueueRowItem(
                    id = d.id,
                    status = RowStatus.COMPLETED,
                    seriesTitle = d.title,
                    chapterLabel = chapterLabelOf(d),
                    thumbnailUrl = d.thumbnailUrl,
                    provider = d.provider,
                    scanlator = d.scanlator,
                    url = d.url,
                    sortTime = parsed ?: 0L,
                    displayTime = parsed?.let(::formatRelativeTime) ?: "completed",
                    hasRetry = false,
                ),
            )
        }
        // Failed.
        failed.forEach { d ->
            val parsed = d.downloadDateUTC?.let(::parseUtcMillis)
            items.add(
                QueueRowItem(
                    id = d.id,
                    status = RowStatus.FAILED,
                    seriesTitle = d.title,
                    chapterLabel = chapterLabelOf(d),
                    thumbnailUrl = d.thumbnailUrl,
                    provider = d.provider,
                    scanlator = d.scanlator,
                    url = d.url,
                    sortTime = parsed ?: 0L,
                    displayTime = parsed?.let(::formatRelativeTime) ?: "failed",
                    hasRetry = true,
                    retries = d.retries,
                ),
            )
        }
        items
    }

    val filteredItems = when (filterIndex) {
        1 -> allItems.filter { it.status == RowStatus.COMPLETED }
        2 -> allItems.filter { it.status == RowStatus.FAILED }
        3 -> allItems.filter { it.status == RowStatus.QUEUED || it.status == RowStatus.DOWNLOADING }
        else -> allItems
    }

    // Newest first; sortTime == 0 (active/queued) always floats to the top.
    val sortedItems = filteredItems.sortedWith(
        compareBy<QueueRowItem> { if (it.sortTime == 0L) 0 else 1 }.thenByDescending { it.sortTime },
    )
    val visibleItems = sortedItems.take(MAX_VISIBLE)
    val totalAfterFilter = sortedItems.size
    val buckets = visibleItems.groupBy { getDateBucket(it.sortTime) }

    Column(modifier = Modifier.fillMaxSize()) {
        // ── Ribbon: filter pills ─────────────────────────────────────────
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
        ) {
            SegmentedPills(
                labels = FILTER_LABELS,
                selectedIndex = filterIndex,
                onSelect = { filterIndex = it },
            )
        }

        // ── Header ───────────────────────────────────────────────────────
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        ) {
            Text(
                "Queue",
                style = MaterialTheme.typography.titleLarge.copy(fontSize = 22.sp, fontWeight = FontWeight.SemiBold),
                color = RenzoColors.Foreground,
            )
            Text(
                allItems.size.toString(),
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.MutedForeground.copy(alpha = 0.7f),
                modifier = Modifier.padding(start = 12.dp).weight(1f),
            )
            if (libraryState.canOwner) {
                RibbonToggleChip(
                    label = if (viewAllLibraries) "All libraries" else "My library",
                    active = viewAllLibraries,
                    onClick = { viewAllLibraries = !viewAllLibraries },
                    modifier = Modifier.padding(end = 8.dp),
                )
            }
            Text(
                "Jobs",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier
                    .clip(RoundedCornerShape(6.dp))
                    .clickable { jobsOpen = true }
                    .padding(horizontal = 10.dp, vertical = 6.dp),
            )
        }

        // ── Body ─────────────────────────────────────────────────────────
        when {
            isLoading && running.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("Loading…", style = MaterialTheme.typography.bodySmall, color = RenzoColors.MutedForeground)
            }
            visibleItems.isEmpty() -> Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
                Text(
                    if (filterIndex == 0) {
                        "That's everything from the last 7 days."
                    } else {
                        "Nothing to show for \"${FILTER_LABELS[filterIndex]}\"."
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                )
            }
            else -> LazyColumn(
                contentPadding = PaddingValues(bottom = 64.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                BUCKET_ORDER.forEach { bucket ->
                    val bucketItems = buckets[bucket].orEmpty()
                    if (bucketItems.isNotEmpty()) {
                        item(key = "hdr-${bucket.name}") {
                            Text(
                                bucket.label.uppercase(),
                                style = MaterialTheme.typography.labelSmall.copy(fontSize = 11.sp),
                                color = RenzoColors.MutedForeground,
                                letterSpacing = TextUnit(0.88f, TextUnitType.Sp),
                                modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 24.dp, bottom = 8.dp),
                            )
                        }
                        items(bucketItems.size) { index ->
                            val item = bucketItems[index]
                            if (index > 0) {
                                HorizontalDivider(color = RenzoColors.Foreground.copy(alpha = 0.04f))
                            }
                            QueueRow(
                                item = item,
                                baseUrl = baseUrl,
                                onRetry = {
                                    scope.launch {
                                        runCatching { api?.manageDownload(item.id, ErrorDownloadAction.RETRY) }
                                        reload()
                                    }
                                },
                                onRemove = {
                                    scope.launch {
                                        runCatching { api?.manageDownload(item.id, ErrorDownloadAction.DELETE) }
                                        reload()
                                    }
                                },
                                onOpen = { url -> runCatching { uriHandler.openUri(url) } },
                            )
                        }
                    }
                }
                if (totalAfterFilter > MAX_VISIBLE) {
                    item(key = "cap") {
                        Text(
                            "Showing $MAX_VISIBLE of $totalAfterFilter",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.fillMaxWidth().padding(vertical = 16.dp),
                        )
                    }
                }
            }
        }
    }

    if (jobsOpen) {
        JobsDialog(onDismiss = { jobsOpen = false })
    }
}

/** `d.chapterTitle || (d.chapter !== undefined ? "Ch. N" : "")`. */
private fun chapterLabelOf(d: DownloadInfoDto): String =
    d.chapterTitle?.takeIf { it.isNotBlank() }
        ?: d.chapter?.let { "Ch. ${formatChapter(it)}" }
        ?: ""

// ---------------------------------------------------------------------------
// Row
// ---------------------------------------------------------------------------

/**
 * queue-row.tsx, transliterated. The web reveals the action icons on hover;
 * a phone has no hover, so the icons sit inline next to the time label —
 * every action stays reachable with one tap and nothing is dimmed.
 */
@Composable
private fun QueueRow(
    item: QueueRowItem,
    baseUrl: String,
    onRetry: () -> Unit,
    onRemove: () -> Unit,
    onOpen: (String) -> Unit,
) {
    val isDownloading = item.status == RowStatus.DOWNLOADING
    val progressPct = item.progress?.coerceIn(0, 100) ?: 0

    Box(modifier = Modifier.fillMaxWidth()) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 12.dp),
        ) {
            // Status dot.
            Box(
                modifier = Modifier
                    .size(7.dp)
                    .clip(CircleShape)
                    .background(dotColor(item.status)),
            )

            // Thumbnail.
            Box(
                modifier = Modifier
                    .padding(start = 12.dp)
                    .size(36.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .background(RenzoColors.Foreground.copy(alpha = 0.04f)),
            ) {
                if (!item.thumbnailUrl.isNullOrBlank()) {
                    AsyncImage(
                        model = absoluteUrl(baseUrl, item.thumbnailUrl),
                        contentDescription = item.seriesTitle,
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                }
            }

            // Text content.
            Column(modifier = Modifier.padding(start = 12.dp, end = 8.dp).weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        item.seriesTitle,
                        style = MaterialTheme.typography.bodyMedium.copy(
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                        ),
                        color = RenzoColors.Foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    if (item.chapterLabel.isNotEmpty()) {
                        Text(
                            item.chapterLabel,
                            style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                            color = RenzoColors.MutedForeground,
                            maxLines = 1,
                            modifier = Modifier.padding(start = 8.dp),
                        )
                    }
                    if (item.retries > 0) {
                        Text(
                            "×${item.retries}",
                            style = MaterialTheme.typography.bodySmall.copy(fontSize = 12.sp),
                            color = RenzoColors.MutedForeground.copy(alpha = 0.7f),
                            modifier = Modifier.padding(start = 4.dp),
                        )
                    }
                    if (item.hasRetry) {
                        Text(
                            "Retry",
                            style = MaterialTheme.typography.bodySmall.copy(
                                fontSize = 12.sp,
                                fontWeight = FontWeight.Medium,
                                textDecoration = TextDecoration.Underline,
                            ),
                            color = RenzoColors.Red.copy(alpha = 0.8f),
                            modifier = Modifier.padding(start = 6.dp).clickable(onClick = onRetry),
                        )
                    }
                }
                val providerScanlator = listOfNotNull(
                    item.provider?.takeIf { it.isNotBlank() },
                    item.scanlator?.takeIf { it.isNotBlank() },
                ).joinToString(" · ")
                if (providerScanlator.isNotEmpty()) {
                    Text(
                        providerScanlator,
                        style = MaterialTheme.typography.bodySmall.copy(fontSize = 12.sp),
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
            }

            // Right-edge slot: time / percentage, then the actions.
            Text(
                if (isDownloading && item.progress != null) "$progressPct%" else item.displayTime,
                style = MaterialTheme.typography.bodySmall.copy(fontSize = 12.sp),
                color = if (isDownloading) Color(0xFF83B9F5) else RenzoColors.MutedForeground,
                maxLines = 1,
            )

            // Downloading rows are read-only (server-driven) — no actions.
            if (!isDownloading) {
                if (!item.url.isNullOrBlank()) {
                    RowIcon(Icons.AutoMirrored.Filled.ArrowForward, "Open source") { onOpen(item.url) }
                }
                if (item.status == RowStatus.FAILED) {
                    RowIcon(Icons.Filled.Refresh, "Retry download", onRetry)
                }
                if (item.status == RowStatus.QUEUED) {
                    RowIcon(Icons.Filled.Close, "Cancel", onRemove)
                } else {
                    RowIcon(Icons.Filled.Delete, "Remove from history", onRemove)
                }
            }
        }

        // Thin progress bar across the very bottom of downloading rows.
        if (isDownloading) {
            Box(
                modifier = Modifier
                    .align(Alignment.BottomStart)
                    .fillMaxWidth(progressPct / 100f)
                    .height(2.dp)
                    .background(Color(0xFF3B8FEE)),
            )
        }
    }
}

@Composable
private fun RowIcon(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    onClick: () -> Unit,
) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .padding(start = 4.dp)
            .size(28.dp)
            .clip(RoundedCornerShape(6.dp))
            .clickable(onClick = onClick),
    ) {
        Icon(icon, contentDescription = label, tint = RenzoColors.MutedForeground, modifier = Modifier.size(14.dp))
    }
}

// ---------------------------------------------------------------------------
// Jobs dialog (jobs-panel.tsx)
// ---------------------------------------------------------------------------

@Composable
private fun JobsDialog(onDismiss: () -> Unit) {
    val renzoApp = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    var running by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }

    Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .clip(RoundedCornerShape(12.dp))
                .background(RenzoColors.Card)
                .padding(16.dp),
        ) {
            Text(
                "Jobs",
                style = MaterialTheme.typography.titleMedium,
                color = RenzoColors.Foreground,
            )
            Spacer(Modifier.height(16.dp))
            Button(
                onClick = {
                    running = true
                    message = null
                    scope.launch {
                        val ok = runCatching {
                            renzoApp.network.currentServiceOf<QueueApi>()?.updateAllSeries()
                        }.isSuccess
                        running = false
                        message = if (ok) {
                            "Update All Series completed successfully!"
                        } else {
                            "Failed to start Update All Series."
                        }
                    }
                },
                shape = MaterialTheme.shapes.small,
                enabled = !running,
            ) {
                if (running) {
                    CircularProgressIndicator(
                        color = RenzoColors.PrimaryForeground,
                        strokeWidth = 1.5.dp,
                        modifier = Modifier.size(16.dp),
                    )
                } else {
                    Icon(Icons.Filled.Description, contentDescription = null, modifier = Modifier.size(16.dp))
                }
                Text("Update All Series", modifier = Modifier.padding(start = 8.dp))
            }
            Spacer(Modifier.height(12.dp))
            Text(
                "Applies the selected title to the entire series. This process updates titles for " +
                    "consistency, rewrites the ComicInfo.xml, and sets the series cover to the one you selected.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
            )
            Text(
                "⚠️ Note: This may also rename files and convert your series into .cbz archives, but only " +
                    "if the ComicInfo metadata has been updated.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(top = 8.dp),
            )
            val msg = message
            if (msg != null) {
                Text(
                    msg,
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.Primary,
                    modifier = Modifier.padding(top = 12.dp),
                )
            }
            Spacer(Modifier.height(16.dp))
            Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                Text(
                    "Close",
                    style = MaterialTheme.typography.labelLarge,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable(onClick = onDismiss)
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                )
            }
        }
    }
}
