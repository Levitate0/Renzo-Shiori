package app.renzoshiori.client.ui.reader

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.ui.library.formatChapter
import coil3.compose.AsyncImage

/**
 * The reader. Two modes:
 *  - Paged: HorizontalPager, one page per swipe.
 *  - Webtoon/continuous: LazyColumn strip. The current page derives from
 *    LazyListState's visible-items info against a probe line a third of the
 *    way down the viewport — the same tracking rule the web reader uses, but
 *    Compose hands us visibility info directly instead of DOM rect math.
 *    The end-of-chapter block is a real list item, so reaching it drives
 *    completion exactly like the web reader's sentinel page.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ReaderScreen(
    seriesId: String,
    chapterNumber: Double,
    onExit: () -> Unit,
    vm: ReaderViewModel = viewModel(
        key = "reader-$seriesId-$chapterNumber",
        factory = ReaderViewModel.factory(
            androidx.compose.ui.platform.LocalContext.current.applicationContext as android.app.Application,
            seriesId,
            chapterNumber,
        ),
    ),
) {
    val state by vm.state.collectAsState()
    var chromeVisible by remember { mutableStateOf(true) }

    Box(modifier = Modifier.fillMaxSize().background(Color.Black)) {
        when {
            state.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            state.error != null -> Column(
                Modifier.fillMaxSize().padding(24.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(state.error!!, color = Color.White, textAlign = TextAlign.Center)
                OutlinedButton(onClick = onExit, modifier = Modifier.padding(top = 16.dp)) { Text("Back") }
            }
            state.webtoon -> WebtoonStrip(
                state = state,
                onPageViewed = vm::onPageViewed,
                onTap = { chromeVisible = !chromeVisible },
                onNext = vm::nextChapter,
                onPrev = vm::prevChapter,
                onExit = onExit,
            )
            else -> PagedReader(
                state = state,
                onPageViewed = vm::onPageViewed,
                onTap = { chromeVisible = !chromeVisible },
            )
        }

        if (chromeVisible && !state.loading) {
            TopAppBar(
                title = { Text("Ch. ${formatChapter(state.chapterNumber)}") },
                navigationIcon = {
                    IconButton(onClick = onExit) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    IconButton(onClick = vm::toggleMode) {
                        Icon(Icons.Filled.SwapVert, contentDescription = "Toggle reading mode")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = Color.Black.copy(alpha = 0.75f),
                    titleContentColor = Color.White,
                    navigationIconContentColor = Color.White,
                    actionIconContentColor = Color.White,
                ),
            )
        }
    }
}

@Composable
private fun PagedReader(
    state: ReaderUiState,
    onPageViewed: (Int) -> Unit,
    onTap: () -> Unit,
) {
    val pagerState = rememberPagerState(initialPage = state.resumePage) { state.pages.size }

    LaunchedEffect(pagerState) {
        snapshotFlow { pagerState.currentPage }.collect { onPageViewed(it) }
    }

    HorizontalPager(state = pagerState, modifier = Modifier.fillMaxSize()) { page ->
        Box(
            modifier = Modifier.fillMaxSize().clickable(onClick = onTap),
            contentAlignment = Alignment.Center,
        ) {
            AsyncImage(
                model = state.pages[page],
                contentDescription = "Page ${page + 1}",
                contentScale = ContentScale.Fit,
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}

@Composable
private fun WebtoonStrip(
    state: ReaderUiState,
    onPageViewed: (Int) -> Unit,
    onTap: () -> Unit,
    onNext: () -> Unit,
    onPrev: () -> Unit,
    onExit: () -> Unit,
) {
    val listState: LazyListState = rememberLazyListState(
        initialFirstVisibleItemIndex = state.resumePage,
    )
    val pageCount = state.pages.size

    // Current page = the item under a probe line 1/3 down the viewport
    // (same rule as the web reader). The end block is item index == pageCount,
    // and reaching it reports the final page — the sentinel-page behavior.
    val currentItem by remember {
        derivedStateOf {
            val info = listState.layoutInfo
            val probe = info.viewportStartOffset + info.viewportSize.height / 3
            info.visibleItemsInfo.lastOrNull { it.offset <= probe }?.index ?: 0
        }
    }

    LaunchedEffect(listState) {
        snapshotFlow { currentItem }.collect { idx ->
            // Reaching the end block (index == pageCount) reports the final
            // page, which the ViewModel treats as completion.
            if (pageCount > 0) onPageViewed(idx.coerceAtMost(pageCount - 1))
        }
    }

    LazyColumn(state = listState, modifier = Modifier.fillMaxSize().clickable(onClick = onTap)) {
        itemsIndexed(state.pages) { index, page ->
            AsyncImage(
                model = page,
                contentDescription = "Page ${index + 1}",
                contentScale = ContentScale.FillWidth,
                modifier = Modifier.fillMaxWidth(),
            )
        }
        // End-of-chapter block — fills the screen (the web reader's sentinel
        // behavior): reaching it is unambiguous completion.
        item {
            Column(
                modifier = Modifier.fillMaxWidth().fillMaxHeight().padding(24.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text("FINISHED", style = MaterialTheme.typography.labelSmall, color = Color.White.copy(alpha = 0.4f))
                Text(
                    "Ch. ${formatChapter(state.chapterNumber)}",
                    style = MaterialTheme.typography.titleMedium,
                    color = Color.White.copy(alpha = 0.85f),
                    modifier = Modifier.padding(top = 4.dp, bottom = 20.dp),
                )
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    if (state.hasPrev) {
                        OutlinedButton(onClick = onPrev) { Text("Previous") }
                    }
                    if (state.hasNext) {
                        Button(onClick = onNext) { Text("Next chapter") }
                    } else {
                        OutlinedButton(onClick = onExit) { Text("Back to series") }
                    }
                }
            }
        }
    }
}
