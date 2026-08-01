package app.renzoshiori.client.ui.updates

import android.app.Application
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.LibraryAdd
import androidx.compose.material.icons.filled.Refresh
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.TextUnitType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.UpdateFeedItemDto
import app.renzoshiori.client.data.network.UpdatesApi
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.components.RibbonToggleChip
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.formatChapter
import app.renzoshiori.client.ui.queue.DateBucket
import app.renzoshiori.client.ui.queue.formatRelativeTime
import app.renzoshiori.client.ui.queue.getDateBucket
import app.renzoshiori.client.ui.queue.parseUtcMillis
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// ---------------------------------------------------------------------------
// Constants (updates/page.tsx)
// ---------------------------------------------------------------------------

/** Pull a deep feed so batch releases and older missed updates are all visible. */
private const val FETCH_LIMIT = 1000

/** A run of this many consecutive "newChapter" rows for one series collapses. */
private const val STACK_THRESHOLD = 5

private val BUCKET_ORDER = listOf(
    DateBucket.TODAY, DateBucket.YESTERDAY, DateBucket.THIS_WEEK, DateBucket.EARLIER,
)

private data class FeedRow(
    val key: String,
    val item: UpdateFeedItemDto,
    val sortTime: Long,
    val displayTime: String,
)

private sealed interface DisplayGroup {
    data class Single(val row: FeedRow) : DisplayGroup
    data class Stack(
        val key: String,
        val seriesId: String,
        val seriesTitle: String,
        val thumbnailUrl: String?,
        val provider: String?,
        val rows: List<FeedRow>,
        val minChapter: Double?,
        val maxChapter: Double?,
        val displayTime: String,
    ) : DisplayGroup
}

/**
 * Updates — 1:1 port of RenzoFrontend src/app/updates/page.tsx: the
 * "Updates <count>" header with the Owner-only My-library toggle and the
 * "Update now" scan button, the live scan-progress bar, the four date buckets,
 * and the feed rows — including collapsing a batch release of 5+ consecutive
 * chapters for one series into a single expandable stack.
 */
@Composable
fun UpdatesScreen(onOpenSeries: (String) -> Unit) {
    val context = LocalContext.current
    val renzoApp = context.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()

    // The command bar's search box is bound to LibraryViewModel — the web's
    // updates page reads that very same shared search context.
    val libraryVm: LibraryViewModel = viewModel(
        factory = LibraryViewModel.factory(context.applicationContext as Application),
    )
    val libraryState by libraryVm.state.collectAsState()
    val search = libraryState.searchTerm.trim()

    val api = remember { renzoApp.network.currentServiceOf<UpdatesApi>() }

    var items by remember { mutableStateOf<List<UpdateFeedItemDto>?>(null) }
    var isLoading by remember { mutableStateOf(true) }
    var viewAllLibraries by remember { mutableStateOf(false) }
    var scanning by remember { mutableStateOf(false) }
    var scanRemaining by remember { mutableStateOf(0) }
    var scanMax by remember { mutableStateOf(0) }

    LaunchedEffect(viewAllLibraries) {
        isLoading = true
        runCatching { api?.updates(0, FETCH_LIMIT, viewAllLibraries) }
            .onSuccess { items = it ?: emptyList() }
            .onFailure { items = emptyList() }
        isLoading = false
    }

    // Live scan progress: remaining per-provider chapter checks in the queue.
    // scanMax anchors the bar's 100% at the largest backlog seen this scan.
    // Polls fast while a scan is active, lazily when idle.
    LaunchedEffect(Unit) {
        while (true) {
            runCatching { api?.scanStatus() }.getOrNull()?.let { s ->
                val remaining = s.waiting + s.running
                if (remaining > scanMax) scanMax = remaining
                if (remaining == 0) scanMax = 0
                scanRemaining = remaining
            }
            delay(if (scanMax > 0) 4000L else 15000L)
        }
    }

    val scanPct = if (scanMax > 0) {
        minOf(100, Math.round(((scanMax - scanRemaining).toFloat() / scanMax) * 100f))
    } else {
        0
    }

    val rows = remember(items, search) {
        val source = items ?: emptyList()
        val out = ArrayList<FeedRow>()
        source.forEachIndexed { i, item ->
            if (search.isNotEmpty() && !item.seriesTitle.contains(search, ignoreCase = true)) return@forEachIndexed
            val sortTime = parseUtcMillis(item.timestamp) ?: System.currentTimeMillis()
            out.add(
                FeedRow(
                    key = "${item.seriesId}-${item.kind}-${item.chapterNumber ?: ""}-$i",
                    item = item,
                    sortTime = sortTime,
                    displayTime = formatRelativeTime(sortTime),
                ),
            )
        }
        out
    }

    val buckets = remember(rows) { rows.groupBy { getDateBucket(it.sortTime) } }
    val baseUrl = renzoApp.tokenStore.serverUrl ?: ""

    Column(modifier = Modifier.fillMaxSize()) {
        // ── Header ───────────────────────────────────────────────────────
        Column(modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 24.dp, bottom = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Text(
                    "Updates",
                    style = MaterialTheme.typography.titleLarge.copy(
                        fontSize = 22.sp,
                        fontWeight = FontWeight.SemiBold,
                    ),
                    color = RenzoColors.Foreground,
                )
                Text(
                    rows.size.toString(),
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
                // "Update now" — scan every source for new chapters now.
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .height(32.dp)
                        .clip(RoundedCornerShape(50))
                        .background(RenzoColors.Primary.copy(alpha = 0.10f))
                        .clickable(enabled = !scanning) {
                            scanning = true
                            scope.launch {
                                runCatching { api?.scanAll() }
                                // Brief lockout so double-taps don't queue twice.
                                delay(4000)
                                scanning = false
                            }
                        }
                        .padding(horizontal = 12.dp),
                ) {
                    if (scanning) {
                        CircularProgressIndicator(
                            color = RenzoColors.Primary,
                            strokeWidth = 1.5.dp,
                            modifier = Modifier.size(14.dp),
                        )
                    } else {
                        Icon(
                            Icons.Filled.Refresh,
                            contentDescription = null,
                            tint = RenzoColors.Primary,
                            modifier = Modifier.size(14.dp),
                        )
                    }
                    Text(
                        "Update now",
                        style = MaterialTheme.typography.labelMedium,
                        color = RenzoColors.Primary,
                        maxLines = 1,
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }
            }

            // Scan progress — visible while per-provider checks are queued.
            if (scanRemaining > 0) {
                Column(modifier = Modifier.fillMaxWidth().padding(top = 16.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                        CircularProgressIndicator(
                            color = RenzoColors.Primary,
                            strokeWidth = 1.5.dp,
                            modifier = Modifier.size(12.dp),
                        )
                        Text(
                            "Checking sources for new chapters…",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.padding(start = 6.dp).weight(1f),
                        )
                        Text(
                            "$scanRemaining source check${if (scanRemaining == 1) "" else "s"} remaining · $scanPct%",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                        )
                    }
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 6.dp)
                            .height(6.dp)
                            .clip(RoundedCornerShape(50))
                            .background(RenzoColors.Foreground.copy(alpha = 0.08f)),
                    ) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth(maxOf(3, scanPct) / 100f)
                                .height(6.dp)
                                .clip(RoundedCornerShape(50))
                                .background(RenzoColors.Primary),
                        )
                    }
                }
            }
        }

        // ── Body ─────────────────────────────────────────────────────────
        when {
            isLoading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("Loading…", style = MaterialTheme.typography.bodySmall, color = RenzoColors.MutedForeground)
            }
            rows.isEmpty() -> Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
                Text(
                    if (search.isNotEmpty()) {
                        "No updates matching \"$search\"."
                    } else {
                        "Nothing here yet — new chapters and added series will show up as they arrive."
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    textAlign = TextAlign.Center,
                )
            }
            else -> LazyColumn(
                contentPadding = PaddingValues(bottom = 64.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                BUCKET_ORDER.forEach { bucket ->
                    val bucketRows = buckets[bucket].orEmpty()
                    if (bucketRows.isNotEmpty()) {
                        item(key = "hdr-${bucket.name}") {
                            Text(
                                bucket.label.uppercase(),
                                style = MaterialTheme.typography.labelSmall.copy(fontSize = 11.sp),
                                color = RenzoColors.MutedForeground,
                                letterSpacing = TextUnit(0.88f, TextUnitType.Sp),
                                modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 24.dp, bottom = 8.dp),
                            )
                        }
                        groupConsecutiveRows(bucketRows, bucket.name).forEach { group ->
                            when (group) {
                                is DisplayGroup.Single -> item(key = group.row.key) {
                                    UpdateRow(group.row, baseUrl, onOpenSeries)
                                }
                                is DisplayGroup.Stack -> item(key = group.key) {
                                    UpdateStack(group, baseUrl, onOpenSeries)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Grouping
// ---------------------------------------------------------------------------

/**
 * Collapses runs of STACK_THRESHOLD-or-more consecutive "newChapter" entries
 * for the same series into a single stack. Rows arrive latest-first overall,
 * but within a stack we sort strictly by chapter number descending so the
 * expanded view is guaranteed largest/latest-first even if a batch's
 * timestamps and chapter numbers aren't perfectly aligned.
 */
private fun groupConsecutiveRows(rows: List<FeedRow>, bucketKey: String): List<DisplayGroup> {
    val groups = ArrayList<DisplayGroup>()
    var i = 0
    while (i < rows.size) {
        val row = rows[i]
        if (row.item.kind == "newChapter") {
            var j = i + 1
            while (j < rows.size && rows[j].item.kind == "newChapter" && rows[j].item.seriesId == row.item.seriesId) {
                j++
            }
            val run = rows.subList(i, j)
            if (run.size >= STACK_THRESHOLD) {
                val chapterNumbers = run.mapNotNull { it.item.chapterNumber }
                val sortedRows = run.sortedByDescending { it.item.chapterNumber ?: Double.NEGATIVE_INFINITY }
                val latestTime = run.maxOf { it.sortTime }
                groups.add(
                    DisplayGroup.Stack(
                        key = "stack-$bucketKey-${row.item.seriesId}-$i",
                        seriesId = row.item.seriesId,
                        seriesTitle = row.item.seriesTitle,
                        thumbnailUrl = row.item.thumbnailUrl,
                        provider = row.item.provider,
                        rows = sortedRows,
                        minChapter = chapterNumbers.minOrNull(),
                        maxChapter = chapterNumbers.maxOrNull(),
                        displayTime = formatRelativeTime(latestTime),
                    ),
                )
                i = j
                continue
            }
        }
        groups.add(DisplayGroup.Single(row))
        i++
    }
    return groups
}

// ---------------------------------------------------------------------------
// Rows
// ---------------------------------------------------------------------------

@Composable
private fun UpdateRow(
    row: FeedRow,
    baseUrl: String,
    onOpen: (String) -> Unit,
    indentStart: Boolean = false,
) {
    val item = row.item
    val isAdded = item.kind == "seriesAdded"
    // Finished chapters stay in the feed (and stay clickable) but read greyed-out.
    val isRead = !isAdded && item.read

    val chapterLabel = if (isAdded) {
        "Added to library"
    } else {
        item.chapterName?.takeIf { it.isNotBlank() }
            ?: item.chapterNumber?.let { "Chapter ${formatChapter(it)}" }
            ?: "New chapter"
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clickable { onOpen(item.seriesId) }
            .padding(start = if (indentStart) 36.dp else 12.dp, end = 12.dp, top = 8.dp, bottom = 8.dp)
            .alpha(if (isRead) 0.45f else 1f),
    ) {
        FeedCover(item.thumbnailUrl, baseUrl, item.seriesTitle)
        Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
            Text(
                item.seriesTitle,
                style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                color = RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                Icon(
                    if (isAdded) Icons.Filled.LibraryAdd else Icons.AutoMirrored.Filled.MenuBook,
                    contentDescription = null,
                    tint = if (isAdded) RenzoColors.Primary.copy(alpha = 0.8f) else RenzoColors.MutedForeground,
                    modifier = Modifier.size(14.dp),
                )
                Text(
                    chapterLabel + if (!isAdded && !item.provider.isNullOrBlank()) " · ${item.provider}" else "",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(start = 6.dp),
                )
            }
        }
        if (isRead) {
            Icon(
                Icons.Filled.Check,
                contentDescription = "Read",
                tint = RenzoColors.Primary.copy(alpha = 0.7f),
                modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(
            row.displayTime,
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground.copy(alpha = 0.7f),
            maxLines = 1,
        )
    }
}

@Composable
private fun UpdateStack(
    group: DisplayGroup.Stack,
    baseUrl: String,
    onOpen: (String) -> Unit,
) {
    var isOpen by remember(group.key) { mutableStateOf(false) }
    val count = group.rows.size
    val minChapter = group.minChapter
    val maxChapter = group.maxChapter
    val rangeLabel = when {
        minChapter != null && maxChapter != null && minChapter == maxChapter ->
            "Chapter ${formatChapter(minChapter)}"
        minChapter != null && maxChapter != null ->
            "Chapters ${formatChapter(minChapter)}-${formatChapter(maxChapter)}"
        else -> "$count new chapters"
    }

    Column(modifier = Modifier.fillMaxWidth()) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .fillMaxWidth()
                .clickable { isOpen = !isOpen }
                .padding(horizontal = 12.dp, vertical = 8.dp),
        ) {
            FeedCover(group.thumbnailUrl, baseUrl, group.seriesTitle)
            Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                Text(
                    group.seriesTitle,
                    style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                    color = RenzoColors.Foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                    Icon(
                        Icons.AutoMirrored.Filled.MenuBook,
                        contentDescription = null,
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.size(14.dp),
                    )
                    Text(
                        "$rangeLabel · $count new" +
                            if (!group.provider.isNullOrBlank()) " · ${group.provider}" else "",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }
            }
            Text(
                group.displayTime,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground.copy(alpha = 0.7f),
                maxLines = 1,
            )
            Icon(
                if (isOpen) Icons.Filled.ExpandMore else Icons.Filled.ChevronRight,
                contentDescription = null,
                tint = RenzoColors.MutedForeground.copy(alpha = 0.7f),
                modifier = Modifier.padding(start = 8.dp).size(14.dp),
            )
        }
        if (isOpen) {
            HorizontalDivider(color = RenzoColors.Foreground.copy(alpha = 0.04f))
            group.rows.forEach { row ->
                UpdateRow(row, baseUrl, onOpen, indentStart = true)
            }
        }
    }
}

/** The feed row's 40×56 rounded cover. */
@Composable
private fun FeedCover(thumbnailUrl: String?, baseUrl: String, alt: String) {
    Box(
        modifier = Modifier
            .width(40.dp)
            .height(56.dp)
            .clip(RoundedCornerShape(6.dp))
            .background(RenzoColors.Foreground.copy(alpha = 0.04f)),
    ) {
        if (!thumbnailUrl.isNullOrBlank()) {
            AsyncImage(
                model = absoluteUrl(baseUrl, thumbnailUrl),
                contentDescription = alt,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}
