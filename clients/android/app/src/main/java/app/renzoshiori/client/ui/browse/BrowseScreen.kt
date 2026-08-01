package app.renzoshiori.client.ui.browse

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.LatestSeriesDto
import app.renzoshiori.client.data.network.absoluteUrl
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay

/**
 * Browse — the web /cloud-latest page's core: the cached cross-source
 * "latest" catalogue as a cover grid with keyword search (server-side
 * keyword param, debounced). Adding to library from here is a later pass —
 * rows are read-only previews for now.
 */
@Composable
fun BrowseScreen() {
    val app = LocalContext.current.applicationContext as RenzoApp
    var keyword by remember { mutableStateOf("") }
    var items by remember { mutableStateOf<List<LatestSeriesDto>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(keyword) {
        if (keyword.isNotEmpty()) delay(400) // debounce typing
        runCatching {
            app.network.currentApi()?.latest(0, 60, keyword = keyword.ifBlank { null })
        }
            .onSuccess { items = it ?: emptyList(); error = null }
            .onFailure { error = "Couldn't load the catalogue." }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = keyword,
            onValueChange = { keyword = it },
            placeholder = { Text("Search the catalogue…") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
        )
        when {
            error != null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(error!!, color = MaterialTheme.colorScheme.error)
            }
            items == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            else -> LazyVerticalGrid(
                columns = GridCells.Adaptive(minSize = 110.dp),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(items!!, key = { it.mihonId + it.provider }) { row ->
                    BrowseCard(row, app.tokenStore.serverUrl ?: "")
                }
            }
        }
    }
}

@Composable
private fun BrowseCard(row: LatestSeriesDto, baseUrl: String) {
    Column {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(8.dp))
                .background(MaterialTheme.colorScheme.surface),
        ) {
            if (row.thumbnailUrl != null) {
                AsyncImage(
                    model = absoluteUrl(baseUrl, row.thumbnailUrl),
                    contentDescription = row.title,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
            // Provider badge, top-left — matches the web card.
            Text(
                row.provider,
                style = MaterialTheme.typography.labelSmall,
                color = Color.White,
                maxLines = 1,
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(4.dp)
                    .clip(RoundedCornerShape(4.dp))
                    .background(Color.Black.copy(alpha = 0.7f))
                    .padding(horizontal = 6.dp, vertical = 2.dp),
            )
            Text(
                row.title,
                style = MaterialTheme.typography.labelSmall,
                color = Color.White,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .background(Color.Black.copy(alpha = 0.6f))
                    .padding(horizontal = 6.dp, vertical = 4.dp),
            )
        }
    }
}
