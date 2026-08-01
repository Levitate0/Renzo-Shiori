package app.renzoshiori.client.ui.updates

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.UpdateFeedItemDto
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.library.formatChapter
import coil3.compose.AsyncImage

/**
 * Updates feed — mirrors the web /updates page: newest chapter releases and
 * series additions, thumbnail + title + "Ch. N · name" rows, read items dimmed.
 */
@Composable
fun UpdatesScreen(onOpenSeries: (String) -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    var items by remember { mutableStateOf<List<UpdateFeedItemDto>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        runCatching { app.network.currentApi()?.updates(0, 80) }
            .onSuccess { items = it ?: emptyList() }
            .onFailure { error = "Couldn't load updates." }
    }

    when {
        error != null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(error!!, color = MaterialTheme.colorScheme.error)
        }
        items == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        items!!.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("No recent updates.", color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.6f))
        }
        else -> LazyColumn(modifier = Modifier.fillMaxSize()) {
            items(items!!) { item ->
                UpdateRow(item, app.tokenStore.serverUrl ?: "", onOpenSeries)
                HorizontalDivider(color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.05f))
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
            .padding(horizontal = 14.dp, vertical = 8.dp),
    ) {
        Box(
            modifier = Modifier
                .width(40.dp)
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(5.dp))
                .background(MaterialTheme.colorScheme.surface),
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
        Column(modifier = Modifier.padding(start = 12.dp)) {
            Text(
                item.seriesTitle,
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onBackground.copy(alpha = if (dim) 0.45f else 1f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                if (item.kind == "seriesAdded") "Added to library"
                else buildString {
                    append("Ch. ${item.chapterNumber?.let { formatChapter(it) } ?: "?"}")
                    if (!item.chapterName.isNullOrEmpty()) append(" · ${item.chapterName}")
                },
                style = MaterialTheme.typography.bodySmall,
                color = if (item.kind == "seriesAdded") MaterialTheme.colorScheme.primary.copy(alpha = 0.9f)
                else MaterialTheme.colorScheme.onBackground.copy(alpha = if (dim) 0.3f else 0.6f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}
