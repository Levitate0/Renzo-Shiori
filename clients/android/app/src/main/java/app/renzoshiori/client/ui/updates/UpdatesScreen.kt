package app.renzoshiori.client.ui.updates

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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.AddBox
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.UpdateFeedItemDto
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.library.formatChapter
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.ChronoUnit

/**
 * Updates — transliterated from the mobile web page: "Updates <count>" header
 * with My library + "⟳ Update now" (rose), TODAY/YESTERDAY/EARLIER THIS WEEK/
 * EARLIER small-caps buckets, rows with thumb, one-line title, 📖/🔒/＋
 * "Chapter N · Source" (or rose "Added to library") subtitle, right-aligned
 * relative time; read rows dimmed with a ✓.
 */
@Composable
fun UpdatesScreen(onOpenSeries: (String) -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    var items by remember { mutableStateOf<List<UpdateFeedItemDto>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var scanning by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        runCatching { app.network.currentApi()?.updates(0, 100) }
            .onSuccess { items = it ?: emptyList() }
            .onFailure { error = "Couldn't load updates." }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // Header row — Updates · count · My library · Update now.
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
        ) {
            Text("Updates", style = MaterialTheme.typography.headlineSmall, color = RenzoColors.Foreground)
            Text(
                items?.size?.toString() ?: "",
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(start = 10.dp).weight(1f),
            )
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(50))
                    .clickable(enabled = !scanning) {
                        scanning = true
                        scope.launch {
                            runCatching { app.network.currentApi()?.scanAll() }
                            scanning = false
                        }
                    }
                    .padding(horizontal = 12.dp, vertical = 7.dp),
            ) {
                Icon(
                    Icons.Filled.Refresh, contentDescription = null,
                    tint = RenzoColors.Primary, modifier = Modifier.size(14.dp),
                )
                Text(
                    if (scanning) "Updating…" else "Update now",
                    style = MaterialTheme.typography.labelMedium,
                    color = RenzoColors.Primary,
                    modifier = Modifier.padding(start = 6.dp),
                )
            }
        }

        when {
            error != null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(error!!, color = MaterialTheme.colorScheme.error)
            }
            items == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            items!!.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("No recent updates.", color = RenzoColors.MutedForeground)
            }
            else -> {
                val buckets = remember(items) { bucketize(items!!) }
                LazyColumn(modifier = Modifier.fillMaxSize()) {
                    buckets.forEach { (label, rows) ->
                        item(key = "hdr-$label") {
                            Text(
                                label.uppercase(),
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.MutedForeground,
                                letterSpacing = androidx.compose.ui.unit.TextUnit(1.5f, androidx.compose.ui.unit.TextUnitType.Sp),
                                modifier = Modifier.padding(start = 16.dp, top = 18.dp, bottom = 6.dp),
                            )
                        }
                        items(rows) { item ->
                            UpdateRow(item, app.tokenStore.serverUrl ?: "", onOpenSeries)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun UpdateRow(item: UpdateFeedItemDto, baseUrl: String, onOpenSeries: (String) -> Unit) {
    val dim = item.read
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clickable { onOpenSeries(item.seriesId) }
            .padding(horizontal = 16.dp, vertical = 8.dp)
            .alpha(if (dim) 0.45f else 1f),
    ) {
        Box(
            modifier = Modifier
                .width(44.dp)
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(6.dp))
                .background(RenzoColors.Card),
        ) {
            if (item.thumbnailUrl != null) {
                AsyncImage(
                    model = absoluteUrl(baseUrl, item.thumbnailUrl),
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }
        Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
            Text(
                item.seriesTitle,
                style = MaterialTheme.typography.titleSmall,
                color = RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                if (item.kind == "seriesAdded") {
                    Icon(
                        Icons.Filled.AddBox, contentDescription = null,
                        tint = RenzoColors.Primary, modifier = Modifier.size(14.dp),
                    )
                    Text(
                        "Added to library",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.Primary,
                        modifier = Modifier.padding(start = 6.dp),
                    )
                } else {
                    Icon(
                        if (item.chapterName?.contains("🔒") == true) Icons.Filled.Lock else Icons.AutoMirrored.Filled.MenuBook,
                        contentDescription = null,
                        tint = RenzoColors.MutedForeground, modifier = Modifier.size(14.dp),
                    )
                    Text(
                        buildString {
                            append("Chapter ${item.chapterNumber?.let { formatChapter(it) } ?: "?"}")
                            if (!item.provider.isNullOrEmpty()) append(" · ${item.provider}")
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }
            }
        }
        if (dim) {
            Icon(
                Icons.Filled.Check, contentDescription = "Read",
                tint = RenzoColors.MutedForeground, modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(
            relativeTime(item.timestamp),
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
        )
    }
}

private fun bucketize(items: List<UpdateFeedItemDto>): List<Pair<String, List<UpdateFeedItemDto>>> {
    val today = LocalDate.now()
    val groups = LinkedHashMap<String, MutableList<UpdateFeedItemDto>>()
    for (item in items) {
        val date = parseDate(item.timestamp) ?: today
        val label = when {
            date == today -> "Today"
            date == today.minusDays(1) -> "Yesterday"
            ChronoUnit.DAYS.between(date, today) < 7 -> "Earlier this week"
            else -> "Earlier"
        }
        groups.getOrPut(label) { mutableListOf() }.add(item)
    }
    return groups.map { it.key to it.value }
}

private fun parseDate(iso: String): LocalDate? = runCatching {
    // Backend DateTimes are UTC, sometimes without an explicit offset.
    val normalized = if (iso.endsWith("Z") || iso.contains("+")) iso else iso + "Z"
    Instant.parse(normalized).atZone(ZoneId.systemDefault()).toLocalDate()
}.getOrNull() ?: runCatching { LocalDate.parse(iso.take(10)) }.getOrNull()

private fun relativeTime(iso: String): String {
    val instant = runCatching {
        val normalized = if (iso.endsWith("Z") || iso.contains("+")) iso else iso + "Z"
        Instant.parse(normalized)
    }.getOrNull() ?: return ""
    val mins = ChronoUnit.MINUTES.between(instant, Instant.now())
    return when {
        mins < 1 -> "just now"
        mins < 60 -> "${mins}m ago"
        mins < 60 * 24 -> "${mins / 60}h ago"
        mins < 60 * 48 -> "yesterday"
        else -> "${mins / (60 * 24)}d ago"
    }
}
