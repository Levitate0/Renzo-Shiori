package app.renzoshiori.client.ui.queue

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.DownloadInfoDto
import app.renzoshiori.client.data.model.QueueStatus
import app.renzoshiori.client.ui.library.formatChapter

/**
 * Queue — the web /queue page's core: the server download queue filtered by
 * status pills (Running / Queued / Completed / Failed), auto-refreshing every
 * few seconds while open.
 */
@Composable
fun QueueScreen() {
    val app = LocalContext.current.applicationContext as RenzoApp
    var filter by remember { mutableStateOf(QueueStatus.RUNNING) }
    var items by remember { mutableStateOf<List<DownloadInfoDto>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var refreshTick by remember { mutableStateOf(0) }

    LaunchedEffect(filter, refreshTick) {
        runCatching { app.network.currentApi()?.downloads(filter, 100)?.downloads }
            .onSuccess { items = it ?: emptyList(); error = null }
            .onFailure { error = "Couldn't load the queue." }
        kotlinx.coroutines.delay(5000)
        refreshTick++
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Row(
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 12.dp, vertical = 6.dp),
        ) {
            FilterPill("Running", filter == QueueStatus.RUNNING) { filter = QueueStatus.RUNNING; items = null }
            FilterPill("Queued", filter == QueueStatus.WAITING) { filter = QueueStatus.WAITING; items = null }
            FilterPill("Completed", filter == QueueStatus.COMPLETED) { filter = QueueStatus.COMPLETED; items = null }
            FilterPill("Failed", filter == QueueStatus.FAILED) { filter = QueueStatus.FAILED; items = null }
        }
        when {
            error != null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(error!!, color = MaterialTheme.colorScheme.error)
            }
            items == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            items!!.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("Nothing here.", color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f))
            }
            else -> LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(items!!, key = { it.id }) { d ->
                    QueueRow(d)
                    HorizontalDivider(color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.05f))
                }
            }
        }
    }
}

@Composable
private fun FilterPill(label: String, active: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(
                if (active) MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)
                else MaterialTheme.colorScheme.onBackground.copy(alpha = 0.05f),
            )
            .clickable(onClick = onClick)
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

@Composable
private fun QueueRow(d: DownloadInfoDto) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
    ) {
        Column(modifier = Modifier.padding(end = 8.dp).weight(1f, fill = true)) {
            Text(
                d.title,
                style = MaterialTheme.typography.titleSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                buildString {
                    d.chapter?.let { append("Ch. ${formatChapter(it)}") }
                    if (d.provider.isNotEmpty()) append("  ·  ${d.provider}")
                    if (d.retries > 0) append("  ·  ${d.retries} retries")
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.55f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        StatusBadge(d.status)
    }
}

@Composable
private fun StatusBadge(status: Int) {
    val (label, color) = when (status) {
        QueueStatus.RUNNING -> "Running" to Color(0xFF10B981)
        QueueStatus.WAITING -> "Queued" to Color(0xFFF59E0B)
        QueueStatus.COMPLETED -> "Done" to Color(0xFF3B82F6)
        QueueStatus.FAILED -> "Failed" to Color(0xFFEF4444)
        else -> "?" to Color.Gray
    }
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(color.copy(alpha = 0.15f))
            .padding(horizontal = 10.dp, vertical = 4.dp),
    ) {
        Text(label, style = MaterialTheme.typography.labelSmall, color = color)
    }
}
