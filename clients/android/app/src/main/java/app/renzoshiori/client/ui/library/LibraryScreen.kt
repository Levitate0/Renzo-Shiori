package app.renzoshiori.client.ui.library

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.data.model.SeriesStatus
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage

/**
 * The unified Library content — the same view serves the live server library
 * and the on-device offline one, switched by the Online/Offline pill hosted
 * in HomeShell's top bar (which owns this screen's ViewModel so the pill and
 * the grid share state).
 */
@Composable
fun LibraryContent(
    vm: LibraryViewModel,
    onOpenSeries: (seriesId: String) -> Unit,
    onOpenOfflineSeries: (seriesId: String) -> Unit,
) {
    val state by vm.state.collectAsState()

    var statusFilter by rememberSaveable { mutableStateOf("all") }
    var orderBy by rememberSaveable { mutableStateOf("title") }

    // Search lives in the shell's command bar (like the web app) — this view
    // only renders the ribbon + grid.
    Column(modifier = Modifier.fillMaxSize()) {
        // Filter ribbon — status pills + sort toggle, the web ribbon's core.
        if (!state.offlineMode) {
            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 12.dp, vertical = 2.dp),
            ) {
                listOf("all" to "All", "active" to "Active", "paused" to "Paused", "completed" to "Completed")
                    .forEach { (id, label) ->
                        val active = statusFilter == id
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(50))
                                .background(
                                    if (active) MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)
                                    else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.05f),
                                )
                                .clickable { statusFilter = id }
                                .padding(horizontal = 12.dp, vertical = 6.dp),
                        ) {
                            Text(
                                label,
                                style = MaterialTheme.typography.labelMedium,
                                color = if (active) MaterialTheme.colorScheme.primary
                                else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f),
                            )
                        }
                    }
                Spacer(Modifier.width(4.dp))
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .background(MaterialTheme.colorScheme.onBackground.copy(alpha = 0.05f))
                        .clickable { orderBy = if (orderBy == "title") "lastChange" else "title" }
                        .padding(horizontal = 12.dp, vertical = 6.dp),
                ) {
                    Text(
                        if (orderBy == "title") "A–Z" else "Last change",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f),
                    )
                }
            }
        }

        when {
            state.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            state.error != null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(state.error!!, color = MaterialTheme.colorScheme.error)
            }
            state.offlineMode -> OfflineGrid(state, onOpenOfflineSeries)
            else -> OnlineGrid(state, vm.baseUrl, statusFilter, orderBy, onOpenSeries)
        }
    }
}

@Composable
private fun OnlineGrid(
    state: LibraryUiState,
    baseUrl: String,
    statusFilter: String,
    orderBy: String,
    onOpenSeries: (String) -> Unit,
) {
    // Mirrors the web library's tab semantics (library/page.tsx filterFn).
    val filtered = state.series
        .filter { state.searchTerm.isBlank() || it.title.contains(state.searchTerm, ignoreCase = true) }
        .filter {
            when (statusFilter) {
                "completed" -> it.status == SeriesStatus.COMPLETED || it.status == SeriesStatus.PUBLISHING_FINISHED
                "active" -> it.status != SeriesStatus.COMPLETED && it.status != SeriesStatus.PUBLISHING_FINISHED &&
                    it.isActive && !it.pausedDownloads
                "paused" -> it.pausedDownloads
                else -> true
            }
        }
        .let { list ->
            if (orderBy == "lastChange") list.sortedByDescending { it.lastChangeUTC ?: "" }
            else list.sortedBy { it.title.lowercase() }
        }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 110.dp),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        items(filtered, key = { it.id }) { series ->
            SeriesCard(
                title = series.title,
                coverUrl = absoluteUrl(baseUrl, series.thumbnailUrl),
                subtitle = null,
                statusColor = statusStripColor(series),
                providerBadge = series.lastChangeProvider?.provider,
                lastChapter = series.lastChapter,
                paused = series.pausedDownloads,
                onClick = { onOpenSeries(series.id) },
            )
        }
    }
}

/** Status strip color — mirrors the web card's getStatusDisplay() 2px strip. */
private fun statusStripColor(s: app.renzoshiori.client.data.model.SeriesInfoDto): Color = when {
    !s.isActive -> Color(0xFF6B7280) // disabled → gray
    s.status == SeriesStatus.COMPLETED || s.status == SeriesStatus.PUBLISHING_FINISHED -> app.renzoshiori.client.ui.theme.RenzoColors.Blue
    s.status == SeriesStatus.ON_HIATUS -> app.renzoshiori.client.ui.theme.RenzoColors.Yellow
    s.status == SeriesStatus.CANCELLED -> app.renzoshiori.client.ui.theme.RenzoColors.Red
    s.status == SeriesStatus.ONGOING -> app.renzoshiori.client.ui.theme.RenzoColors.Green
    else -> Color(0xFF6B7280)
}

@Composable
private fun OfflineGrid(state: LibraryUiState, onOpenOfflineSeries: (String) -> Unit) {
    val filtered = state.offlineSeries.filter {
        state.searchTerm.isBlank() || it.title.contains(state.searchTerm, ignoreCase = true)
    }
    if (filtered.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(
                "Nothing saved offline yet.\nOpen a series and download chapters first.",
                color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f),
            )
        }
        return
    }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 110.dp),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        items(filtered, key = { it.seriesId }) { series ->
            SeriesCard(
                title = series.title,
                coverUrl = null, // cover bytes render via manifest path in detail; grid uses placeholder
                subtitle = "${series.chapterCount} ch offline",
                statusColor = null,
                providerBadge = null,
                lastChapter = null,
                paused = false,
                onClick = { onOpenOfflineSeries(series.seriesId) },
            )
        }
    }
}

/**
 * Web ListSeries card, cloned: 2/3 cover with rounded-md corners, a 2px
 * status strip across the top edge, provider badge top-left (black/70),
 * last-chapter badge bottom-right, title bar (black/60, centered, semibold)
 * along the bottom, yellow pause dot when downloads are paused.
 */
@Composable
private fun SeriesCard(
    title: String,
    coverUrl: String?,
    subtitle: String?,
    statusColor: Color?,
    providerBadge: String?,
    lastChapter: Double?,
    paused: Boolean,
    onClick: () -> Unit,
) {
    Column(modifier = Modifier.clickable(onClick = onClick)) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(8.dp))
                .background(MaterialTheme.colorScheme.surface),
        ) {
            if (coverUrl != null) {
                AsyncImage(
                    model = coverUrl,
                    contentDescription = title,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
            // 2px status strip across the top edge (web: getStatusDisplay color).
            if (statusColor != null) {
                Box(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .fillMaxWidth()
                        .height(2.dp)
                        .background(statusColor),
                )
            }
            // Provider badge — top-left (web: bg-black/70 rounded px-2).
            if (providerBadge != null) {
                Text(
                    providerBadge,
                    style = MaterialTheme.typography.labelSmall,
                    color = Color.White,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier
                        .align(Alignment.TopStart)
                        .padding(4.dp)
                        .clip(RoundedCornerShape(4.dp))
                        .background(Color.Black.copy(alpha = 0.7f))
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
            // Last-chapter badge — bottom-right, above the title bar.
            if (lastChapter != null) {
                Text(
                    "Ch. ${formatChapter(lastChapter)}",
                    style = MaterialTheme.typography.labelSmall,
                    color = Color.White,
                    modifier = Modifier
                        .align(Alignment.BottomEnd)
                        .padding(end = 4.dp, bottom = 28.dp)
                        .clip(RoundedCornerShape(4.dp))
                        .background(Color.Black.copy(alpha = 0.7f))
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
            // Pause dot — yellow, top of the title bar's right corner (web:
            // yellow circle with a pause glyph riding the title strip).
            if (paused) {
                Box(
                    modifier = Modifier
                        .align(Alignment.BottomEnd)
                        .padding(end = 4.dp, bottom = 20.dp)
                        .size(14.dp)
                        .clip(CircleShape)
                        .background(RenzoColors.Yellow),
                )
            }
            // Title strip along the bottom (web: bg-black/60, centered, semibold).
            Text(
                title,
                style = MaterialTheme.typography.labelSmall,
                color = Color.White,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .background(Color.Black.copy(alpha = 0.6f))
                    .padding(horizontal = 6.dp, vertical = 4.dp),
            )
        }
        if (subtitle != null) {
            Text(
                subtitle,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.55f),
                maxLines = 1,
                modifier = Modifier.padding(top = 2.dp),
            )
        }
    }
}

@Composable
fun OnlineOfflinePill(offline: Boolean, onToggle: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .padding(end = 4.dp)
            .clip(RoundedCornerShape(50))
            .background(
                if (offline) Color(0x26F59E0B) else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.06f),
            )
            .clickable(onClick = onToggle)
            .padding(horizontal = 10.dp, vertical = 6.dp),
    ) {
        Box(
            modifier = Modifier
                .size(7.dp)
                .clip(CircleShape)
                .background(if (offline) Color(0xFFF59E0B) else Color(0xFF10B981)),
        )
        Spacer(Modifier.width(6.dp))
        Text(
            if (offline) "Offline" else "Online",
            style = MaterialTheme.typography.labelMedium,
            color = if (offline) Color(0xFFFBBF24) else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.7f),
        )
    }
}

fun formatChapter(n: Double): String =
    if (n == n.toLong().toDouble()) n.toLong().toString() else n.toString()
