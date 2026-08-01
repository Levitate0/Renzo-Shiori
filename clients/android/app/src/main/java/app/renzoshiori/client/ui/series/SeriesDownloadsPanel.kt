package app.renzoshiori.client.ui.series

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.data.model.DownloadInfoDto
import app.renzoshiori.client.data.model.QueueStatus
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage

/**
 * downloads-panel.tsx + download-item.tsx — "Latest Downloads": the newest
 * five queue entries for this series with their status disc, and the
 * active/queued/failed footer summary. Polls every 10s from the ViewModel.
 */
@Composable
fun SeriesDownloadsPanel(state: SeriesDetailUiState, baseUrl: String) {
    val sorted = state.downloads
    val visible = sorted.take(5)
    val activeCount = sorted.count { it.status == QueueStatus.RUNNING }
    val queuedCount = sorted.count { it.status == QueueStatus.WAITING }
    val failedCount = sorted.count { it.status == QueueStatus.FAILED }

    SectionCard(modifier = Modifier.padding(horizontal = 16.dp)) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
        ) {
            Text(
                "Latest Downloads",
                style = MaterialTheme.typography.titleSmall,
                color = RenzoColors.Foreground,
            )
            Text(
                "${sorted.size}",
                fontSize = 11.sp,
                fontWeight = FontWeight.Medium,
                color = Muted,
                modifier = Modifier
                    .clip(PillShape)
                    .background(ForegroundFaint10)
                    .padding(horizontal = 6.dp, vertical = 2.dp),
            )
        }
        HairLine()

        when {
            state.downloadsError -> EmptyDownloads("Failed to load downloads.")
            state.downloadsLoading && sorted.isEmpty() -> Box(
                Modifier.fillMaxWidth().padding(vertical = 40.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text("Loading downloads…", style = MaterialTheme.typography.bodyMedium, color = Muted)
            }
            visible.isEmpty() -> EmptyDownloads("No downloads yet for this series.")
            else -> Column {
                visible.forEachIndexed { index, download ->
                    if (index > 0) HairLine(Border40)
                    DownloadRow(download, baseUrl)
                }
            }
        }

        if (sorted.isNotEmpty()) {
            HairLine(Border40)
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            ) {
                Row(modifier = Modifier.weight(1f)) {
                    if (activeCount > 0) {
                        Text(
                            "$activeCount active",
                            fontSize = 11.sp,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    }
                    if (activeCount > 0 && (queuedCount > 0 || failedCount > 0)) {
                        Text("  ·  ", fontSize = 11.sp, color = Muted)
                    }
                    if (queuedCount > 0) {
                        Text("$queuedCount queued", fontSize = 11.sp, color = Amber500)
                    }
                    if (queuedCount > 0 && failedCount > 0) {
                        Text("  ·  ", fontSize = 11.sp, color = Muted)
                    }
                    if (failedCount > 0) {
                        Text("$failedCount failed", fontSize = 11.sp, color = DestructiveText)
                    }
                    if (activeCount == 0 && queuedCount == 0 && failedCount == 0) {
                        Text("All caught up", fontSize = 11.sp, color = Muted)
                    }
                }
            }
        }
    }
}

@Composable
private fun EmptyDownloads(message: String) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier.fillMaxWidth().padding(vertical = 40.dp),
    ) {
        Icon(
            Icons.Filled.Download,
            contentDescription = null,
            tint = Muted.copy(alpha = 0.6f),
            modifier = Modifier.size(20.dp),
        )
        Text(
            message,
            fontSize = 12.sp,
            color = Muted,
            modifier = Modifier.padding(top = 8.dp),
        )
    }
}

@Composable
private fun DownloadRow(download: DownloadInfoDto, baseUrl: String) {
    val displayIso = download.downloadDateUTC ?: download.scheduledDateUTC
    val chapterLabel = download.chapter?.let { "Ch. ${formatNumber(it)}" } ?: "Chapter"
    // A WAITING job whose scheduled time is still in the future reads as
    // "scheduled", with the calendar icon — exactly like download-item.tsx.
    val futureScheduled = download.status == QueueStatus.WAITING &&
        downloadRelativeTime(displayIso).startsWith("in ")
    val (icon, tint) = downloadStatusIcon(download.status, futureScheduled)

    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        Box(
            modifier = Modifier
                .width(28.dp)
                .height(42.dp)
                .clip(MaterialTheme.shapes.extraSmall)
                .background(RenzoColors.Muted),
        ) {
            download.thumbnailUrl?.takeIf { it.isNotEmpty() }?.let {
                AsyncImage(
                    model = absoluteUrl(baseUrl, it),
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }

        Column(Modifier.weight(1f)) {
            Text(
                chapterLabel + (download.chapterTitle?.let { " · $it" } ?: ""),
                fontSize = 13.sp,
                fontWeight = FontWeight.Medium,
                color = RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Row(Modifier.padding(top = 2.dp)) {
                Text(download.provider, fontSize = 11.sp, color = Muted, maxLines = 1)
                if (!download.scanlator.isNullOrEmpty() && download.scanlator != download.provider) {
                    Text(" · ${download.scanlator}", fontSize = 11.sp, color = Muted, maxLines = 1)
                }
                Text(" · ${downloadRelativeTime(displayIso)}", fontSize = 11.sp, color = Muted, maxLines = 1)
                if (download.retries > 0) {
                    Text(" · retry ${download.retries}", fontSize = 11.sp, color = Amber500, maxLines = 1)
                }
            }
        }

        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(24.dp)
                .clip(PillShape)
                .background(tint.copy(alpha = 0.15f)),
        ) {
            Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(14.dp))
        }
    }
}

/** status-icon.tsx's mapping. */
private fun downloadStatusIcon(status: Int, futureScheduled: Boolean): Pair<ImageVector, Color> {
    if (futureScheduled) return Icons.Filled.CalendarMonth to Yellow500
    return when (status) {
        QueueStatus.RUNNING -> Icons.Filled.Download to Blue500
        QueueStatus.COMPLETED -> Icons.Filled.CheckCircle to Green500
        QueueStatus.FAILED -> Icons.Filled.Warning to Red500
        QueueStatus.WAITING -> Icons.Filled.Schedule to Yellow500
        else -> Icons.Filled.Schedule to Gray500
    }
}
