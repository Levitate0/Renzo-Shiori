package app.renzoshiori.client.ui.browse

import android.app.Application
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.LocalOffer
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.InLibraryStatus
import app.renzoshiori.client.data.model.LatestGenreDto
import app.renzoshiori.client.data.model.LatestSeriesRowDto
import app.renzoshiori.client.data.model.SearchSourceDto
import app.renzoshiori.client.data.network.BrowseApi
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.components.RibbonSelect
import app.renzoshiori.client.ui.components.SelectOption
import app.renzoshiori.client.ui.library.LibraryViewModel
import app.renzoshiori.client.ui.library.formatChapter
import app.renzoshiori.client.ui.library.getStatusDisplay
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.util.AdultFilter
import coil3.compose.AsyncImage

// ---------------------------------------------------------------------------
// Constants (cloud-latest/page.tsx)
// ---------------------------------------------------------------------------

private const val MAX_VISIBLE_GENRES = 200

private data class BrowseCardSize(val value: String, val label: String, val width: Dp, val title: TextUnit)

private val BROWSE_CARD_SIZES = listOf(
    BrowseCardSize("w-20", "XS", 80.dp, 6.4.sp),
    BrowseCardSize("w-32", "S", 128.dp, 12.sp),
    BrowseCardSize("w-45", "M", 180.dp, 14.sp),
    BrowseCardSize("w-58", "L", 232.dp, 16.sp),
    BrowseCardSize("w-70", "XL", 280.dp, 18.sp),
)

private fun browseCardSizeOf(value: String) =
    BROWSE_CARD_SIZES.firstOrNull { it.value == value } ?: BROWSE_CARD_SIZES[1]

/** Items fetched per page — the web computes this from the viewport; a phone
 *  comfortably fits two screens' worth at 40, the web's own floor. */
private const val ITEMS_PER_PAGE = 40

/**
 * Browse — 1:1 port of RenzoFrontend src/app/cloud-latest/page.tsx: the source
 * picker + tag-filter popover + card-size ribbon, the cinematic spotlight hero
 * over the first page's not-yet-in-library picks, the selected-tag chips with
 * Clear, and the infinite-scrolling catalogue grid whose cards carry the status
 * strip, provider badge, latest-chapter badge and in-library heart. Tapping a
 * card opens the details sheet with Add to Library / View Source.
 */
@Composable
fun BrowseScreen() {
    val context = LocalContext.current
    val renzoApp = context.applicationContext as RenzoApp
    val uriHandler = LocalUriHandler.current

    // Shared search context — the shell's command bar writes into LibraryViewModel.
    val libraryVm: LibraryViewModel = viewModel(
        factory = LibraryViewModel.factory(context.applicationContext as Application),
    )
    val libraryState by libraryVm.state.collectAsState()
    val searchTerm = libraryState.searchTerm.trim()

    val api = remember { renzoApp.network.currentServiceOf<BrowseApi>() }
    val baseUrl = renzoApp.tokenStore.serverUrl ?: ""
    val hideAdult = AdultFilter.isHidden(context)

    var selectedSourceId by remember { mutableStateOf("__ALL__") }
    var cardWidth by remember { mutableStateOf("w-32") }
    val selectedGenres = remember { mutableStateListOf<String>() }

    var sources by remember { mutableStateOf<List<SearchSourceDto>>(emptyList()) }
    var genresData by remember { mutableStateOf<List<LatestGenreDto>?>(null) }

    var items by remember { mutableStateOf<List<LatestSeriesRowDto>>(emptyList()) }
    var firstPageItems by remember { mutableStateOf<List<LatestSeriesRowDto>>(emptyList()) }
    var currentPage by remember { mutableStateOf(0) }
    var hasMore by remember { mutableStateOf(true) }
    var isLoading by remember { mutableStateOf(true) }
    var isLoadingMore by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    var tagPopoverOpen by remember { mutableStateOf(false) }
    var detailsItem by remember { mutableStateOf<LatestSeriesRowDto?>(null) }
    var addSeriesTitle by remember { mutableStateOf<String?>(null) }
    var addSeriesOpen by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        runCatching { api?.searchSources() }.getOrNull()?.let {
            sources = it.sortedBy { s -> s.provider.lowercase() }
        }
        runCatching { api?.latestGenres() }.getOrNull()?.let { genresData = it }
    }

    val genreSignature = selectedGenres.sorted().joinToString("|")

    // Reset pagination when filters change.
    LaunchedEffect(searchTerm, selectedSourceId, genreSignature) {
        items = emptyList()
        firstPageItems = emptyList()
        currentPage = 0
        hasMore = true
    }

    LaunchedEffect(searchTerm, selectedSourceId, genreSignature, currentPage) {
        if (currentPage == 0) isLoading = true else isLoadingMore = true
        error = null
        runCatching {
            api?.latest(
                start = currentPage * ITEMS_PER_PAGE,
                count = ITEMS_PER_PAGE,
                sourceId = selectedSourceId.takeIf { it != "__ALL__" },
                keyword = searchTerm.ifBlank { null },
                genre = selectedGenres.toList().takeIf { it.isNotEmpty() },
            )
        }
            .onSuccess { page ->
                val rows = page.orEmpty()
                if (currentPage == 0) {
                    items = rows
                    // The spotlight pool comes from the FIRST page only, so it
                    // stays stable while the user pages through the grid.
                    firstPageItems = rows
                } else {
                    items = items + rows
                }
                hasMore = rows.size >= ITEMS_PER_PAGE
            }
            .onFailure { error = it.message ?: "Error loading latest series" }
        isLoading = false
        isLoadingMore = false
    }

    // Spotlight pool — first-page rows not already in the library, shuffled,
    // capped at 7. Re-shuffles only when the filters refresh the first page.
    val spotlightItems = remember(firstPageItems, hideAdult) {
        firstPageItems
            .filter { it.inLibrary == InLibraryStatus.NOT_IN_LIBRARY }
            .filter { !hideAdult || !AdultFilter.isAdultItem(it.isNsfw.takeIf { flag -> flag }, it.genre) }
            .shuffled()
            .take(7)
            .map { s ->
                SpotlightItem(
                    id = s.seriesId ?: s.mihonId,
                    title = s.title,
                    author = s.author,
                    description = s.description,
                    thumbnailUrl = s.thumbnailUrl?.let { absoluteUrl(baseUrl, it) },
                    status = s.status,
                    genres = s.genre,
                    availableChapters = s.chapterCount,
                    sourceName = s.provider,
                )
            }
    }

    // Temporary 18+ view filter — purely client-side, pages stay intact.
    val visibleItems = remember(items, hideAdult) {
        if (hideAdult) {
            items.filter { !AdultFilter.isAdultItem(it.isNsfw.takeIf { flag -> flag }, it.genre) }
        } else {
            items
        }
    }

    val size = browseCardSizeOf(cardWidth)
    val gridState = rememberLazyGridState()

    // Infinite scroll — load the next page as the tail comes into view.
    val shouldLoadMore by remember {
        derivedStateOf {
            val lastVisible = gridState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            val total = gridState.layoutInfo.totalItemsCount
            total > 0 && lastVisible >= total - 6
        }
    }
    LaunchedEffect(shouldLoadMore, hasMore, isLoading, isLoadingMore) {
        if (shouldLoadMore && hasMore && !isLoading && !isLoadingMore) {
            currentPage += 1
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // ── Ribbon ───────────────────────────────────────────────────────
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 8.dp, vertical = 6.dp),
        ) {
            // Source picker.
            RibbonSelect(
                options = listOf(SelectOption("__ALL__", "All Sources", icon = Icons.Filled.Language)) +
                    sources.filter { it.mihonProviderId.isNotBlank() }.map {
                        SelectOption(it.mihonProviderId, it.provider, icon = Icons.Filled.Language)
                    },
                value = selectedSourceId,
                onChange = { selectedSourceId = it },
                placeholder = "All Sources",
            )

            // Tag popover trigger.
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .height(32.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                    .background(RenzoColors.Card)
                    .clickable { tagPopoverOpen = !tagPopoverOpen }
                    .padding(horizontal = 10.dp),
            ) {
                Icon(
                    Icons.Filled.LocalOffer,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
                Text(
                    when (selectedGenres.size) {
                        0 -> "Tags"
                        1 -> "Tag: ${selectedGenres[0]}"
                        else -> "Tags · ${selectedGenres.size}"
                    },
                    style = MaterialTheme.typography.labelMedium,
                    color = RenzoColors.Foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(start = 8.dp),
                )
                if (selectedGenres.isNotEmpty()) {
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .padding(start = 6.dp)
                            .clip(RoundedCornerShape(50))
                            .background(RenzoColors.Primary)
                            .padding(horizontal = 6.dp, vertical = 1.dp),
                    ) {
                        Text(
                            selectedGenres.size.toString(),
                            style = MaterialTheme.typography.labelSmall.copy(
                                fontSize = 10.sp,
                                fontWeight = FontWeight.SemiBold,
                            ),
                            color = RenzoColors.PrimaryForeground,
                        )
                    }
                }
            }

            Spacer(Modifier.width(4.dp))

            // Card size.
            RibbonSelect(
                options = BROWSE_CARD_SIZES.map { SelectOption(it.value, it.label) },
                value = cardWidth,
                onChange = { cardWidth = it },
                placeholder = "Card Size",
                maxTriggerWidth = 32.dp,
            )
        }

        // Selected-tag chips + Clear.
        if (selectedGenres.isNotEmpty()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 8.dp, vertical = 4.dp),
            ) {
                selectedGenres.toList().forEach { name ->
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .clip(RoundedCornerShape(50))
                            .background(RenzoColors.Secondary.copy(alpha = 0.7f))
                            .padding(start = 10.dp, end = 6.dp, top = 2.dp, bottom = 2.dp),
                    ) {
                        Text(
                            name,
                            style = MaterialTheme.typography.labelMedium,
                            color = RenzoColors.Foreground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Icon(
                            Icons.Filled.Close,
                            contentDescription = "Remove $name",
                            tint = RenzoColors.MutedForeground,
                            modifier = Modifier
                                .padding(start = 4.dp)
                                .size(14.dp)
                                .clickable { selectedGenres.remove(name) },
                        )
                    }
                }
                Text(
                    "Clear",
                    style = MaterialTheme.typography.labelMedium,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .clickable { selectedGenres.clear() }
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
        }

        // ── Body ─────────────────────────────────────────────────────────
        when {
            error != null -> Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        "Error loading latest series",
                        style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.SemiBold),
                        color = RenzoColors.Red,
                    )
                    Text(
                        error!!,
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        textAlign = TextAlign.Center,
                    )
                }
            }
            isLoading && currentPage == 0 -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = RenzoColors.Primary)
            }
            visibleItems.isEmpty() -> Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        "No series found",
                        style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.SemiBold),
                        color = RenzoColors.Foreground,
                    )
                    Text(
                        "Try adjusting your search or source filter",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                    )
                }
            }
            else -> LazyVerticalGrid(
                state = gridState,
                columns = GridCells.Adaptive(minSize = size.width),
                contentPadding = PaddingValues(start = 8.dp, end = 8.dp, top = 8.dp, bottom = 64.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                // Cinematic spotlight, spanning the full grid width.
                if (spotlightItems.isNotEmpty()) {
                    item(key = "spotlight", span = { GridItemSpan(maxLineSpan) }) {
                        Box(modifier = Modifier.padding(bottom = 16.dp)) {
                            SpotlightHero(
                                items = spotlightItems,
                                eyebrow = "DISCOVER · From your sources",
                                ctaLabel = "Add to library",
                                onCtaClick = { spot ->
                                    addSeriesTitle = spot.title
                                    addSeriesOpen = true
                                },
                            )
                        }
                    }
                }

                items(visibleItems, key = { "${it.mihonId}-${it.provider}" }) { row ->
                    CloudLatestCard(row, baseUrl, size) { detailsItem = row }
                }

                item(key = "tail", span = { GridItemSpan(maxLineSpan) }) {
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
                    ) {
                        when {
                            isLoadingMore -> Row(verticalAlignment = Alignment.CenterVertically) {
                                CircularProgressIndicator(
                                    color = RenzoColors.MutedForeground,
                                    strokeWidth = 1.5.dp,
                                    modifier = Modifier.size(16.dp),
                                )
                                Text(
                                    "Loading more...",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = RenzoColors.MutedForeground,
                                    modifier = Modifier.padding(start = 8.dp),
                                )
                            }
                            hasMore -> Text(
                                "Scroll to load more",
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.MutedForeground,
                            )
                            else -> Text(
                                "No more results",
                                style = MaterialTheme.typography.bodySmall,
                                color = RenzoColors.MutedForeground,
                            )
                        }
                    }
                }
            }
        }
    }

    // ── Tag filter popover ───────────────────────────────────────────────
    if (tagPopoverOpen) {
        TagFilterDialog(
            genres = genresData,
            hideAdult = hideAdult,
            selected = selectedGenres,
            onDismiss = { tagPopoverOpen = false },
        )
    }

    // ── Details sheet ────────────────────────────────────────────────────
    val details = detailsItem
    if (details != null) {
        CloudLatestDetailsSheet(
            item = details,
            baseUrl = baseUrl,
            canAddSeries = libraryState.canAddSeries,
            onDismiss = { detailsItem = null },
            onViewSource = { url -> runCatching { uriHandler.openUri(url) } },
            onAddSeries = {
                detailsItem = null
                addSeriesTitle = details.title
                addSeriesOpen = true
            },
        )
    }

    if (addSeriesOpen) {
        AddSeriesSheet(
            initialTitle = addSeriesTitle,
            canAddSeries = libraryState.canAddSeries,
            onDismiss = { addSeriesOpen = false; addSeriesTitle = null },
            onAdded = {
                addSeriesOpen = false
                addSeriesTitle = null
                libraryVm.refresh()
            },
        )
    }
}

// ---------------------------------------------------------------------------
// Card (cloud-latest-grid.tsx)
// ---------------------------------------------------------------------------

@Composable
private fun CloudLatestCard(
    item: LatestSeriesRowDto,
    baseUrl: String,
    size: BrowseCardSize,
    onClick: () -> Unit,
) {
    val statusColor = getStatusDisplay(item.status).color
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .aspectRatio(2f / 3f)
            .clip(RoundedCornerShape(6.dp))
            .background(RenzoColors.Muted)
            .clickable(onClick = onClick),
    ) {
        if (!item.thumbnailUrl.isNullOrBlank()) {
            AsyncImage(
                model = absoluteUrl(baseUrl, item.thumbnailUrl),
                contentDescription = item.title,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }

        // 2px status strip across the top edge.
        Box(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
                .height(2.dp)
                .background(statusColor),
        )

        // Provider badge — top-left.
        Text(
            item.provider,
            style = MaterialTheme.typography.labelSmall.copy(fontWeight = FontWeight.SemiBold),
            color = Color.White,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier
                .align(Alignment.TopStart)
                .padding(4.dp)
                .clip(RoundedCornerShape(4.dp))
                .background(Color.Black.copy(alpha = 0.7f))
                .padding(horizontal = 8.dp, vertical = 2.dp),
        )

        // Latest-chapter badge — top-right, status-colored.
        if (item.latestChapter != null) {
            Text(
                formatChapter(item.latestChapter),
                style = MaterialTheme.typography.labelSmall.copy(fontWeight = FontWeight.SemiBold),
                color = Color.White,
                maxLines = 1,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(4.dp)
                    .clip(RoundedCornerShape(4.dp))
                    .background(statusColor)
                    .padding(horizontal = 8.dp, vertical = 2.dp),
            )
        }

        // In-library heart — red when in library, yellow when disabled there.
        if (item.inLibrary != InLibraryStatus.NOT_IN_LIBRARY) {
            Icon(
                Icons.Filled.Favorite,
                contentDescription = "In library",
                tint = if (item.inLibrary == InLibraryStatus.IN_LIBRARY) Color(0xFFEF4444) else Color(0xFFEAB308),
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(top = 28.dp, end = 4.dp)
                    .size(28.dp),
            )
        }

        Text(
            item.title,
            style = MaterialTheme.typography.labelSmall.copy(
                fontSize = size.title,
                fontWeight = FontWeight.SemiBold,
            ),
            color = Color.White,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .background(Color.Black.copy(alpha = 0.6f))
                .padding(horizontal = 8.dp, vertical = 4.dp),
        )
    }
}

// ---------------------------------------------------------------------------
// Tag filter popover
// ---------------------------------------------------------------------------

@Composable
private fun TagFilterDialog(
    genres: List<LatestGenreDto>?,
    hideAdult: Boolean,
    selected: MutableList<String>,
    onDismiss: () -> Unit,
) {
    var tagSearch by remember { mutableStateOf("") }

    val filtered = remember(genres, tagSearch, hideAdult) {
        var list = genres.orEmpty()
        // Keep adult rating tags out of the picker when the filter is on.
        if (hideAdult) list = list.filter { !AdultFilter.isAdultItem(null, listOf(it.name)) }
        val term = tagSearch.trim().lowercase()
        (if (term.isEmpty()) list else list.filter { it.name.lowercase().contains(term) })
            .take(MAX_VISIBLE_GENRES)
    }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(
            modifier = Modifier
                .fillMaxWidth(0.92f)
                .heightIn(max = 560.dp)
                .clip(RoundedCornerShape(8.dp))
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                .background(RenzoColors.Popover),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 10.dp),
            ) {
                Icon(
                    Icons.Filled.Search,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
                BasicTextField(
                    value = tagSearch,
                    onValueChange = { tagSearch = it },
                    singleLine = true,
                    textStyle = MaterialTheme.typography.bodyMedium.copy(color = RenzoColors.Foreground),
                    cursorBrush = SolidColor(RenzoColors.Foreground),
                    decorationBox = { inner ->
                        Box(modifier = Modifier.padding(start = 8.dp)) {
                            if (tagSearch.isEmpty()) {
                                Text(
                                    "Search tags…",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = RenzoColors.MutedForeground,
                                )
                            }
                            inner()
                        }
                    },
                    modifier = Modifier.weight(1f),
                )
            }
            HorizontalDivider(color = RenzoColors.Border.copy(alpha = 0.6f))

            Box(modifier = Modifier.fillMaxWidth().weight(1f, fill = false)) {
                when {
                    genres == null -> Text(
                        "Loading tags…",
                        style = MaterialTheme.typography.bodyMedium,
                        color = RenzoColors.MutedForeground,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth().padding(24.dp),
                    )
                    filtered.isEmpty() -> Text(
                        if (genres.isEmpty()) "No tags available yet" else "No tags match your search",
                        style = MaterialTheme.typography.bodyMedium,
                        color = RenzoColors.MutedForeground,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth().padding(24.dp),
                    )
                    else -> LazyColumn(modifier = Modifier.heightIn(max = 380.dp)) {
                        items(filtered, key = { it.name }) { g ->
                            val isChecked = selected.contains(g.name)
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (isChecked) selected.remove(g.name) else selected.add(g.name)
                                    }
                                    .padding(horizontal = 10.dp, vertical = 8.dp),
                            ) {
                                Box(
                                    contentAlignment = Alignment.Center,
                                    modifier = Modifier
                                        .size(16.dp)
                                        .clip(RoundedCornerShape(3.dp))
                                        .border(
                                            1.dp,
                                            if (isChecked) RenzoColors.Primary else RenzoColors.Border,
                                            RoundedCornerShape(3.dp),
                                        )
                                        .background(if (isChecked) RenzoColors.Primary else Color.Transparent),
                                ) {
                                    if (isChecked) {
                                        Icon(
                                            Icons.Filled.Check,
                                            contentDescription = null,
                                            tint = RenzoColors.PrimaryForeground,
                                            modifier = Modifier.size(11.dp),
                                        )
                                    }
                                }
                                Text(
                                    g.name,
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = RenzoColors.Foreground,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                    modifier = Modifier.padding(start = 8.dp).weight(1f),
                                )
                                Text(
                                    g.count.toString(),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = RenzoColors.MutedForeground,
                                )
                            }
                        }
                    }
                }
            }

            if (selected.isNotEmpty()) {
                HorizontalDivider(color = RenzoColors.Border.copy(alpha = 0.6f))
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 8.dp),
                ) {
                    Text(
                        "${selected.size} selected",
                        style = MaterialTheme.typography.labelSmall,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier.weight(1f),
                    )
                    Text(
                        "Clear all",
                        style = MaterialTheme.typography.labelMedium,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier
                            .clip(RoundedCornerShape(6.dp))
                            .clickable { selected.clear() }
                            .padding(horizontal = 8.dp, vertical = 4.dp),
                    )
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Details sheet (cloud-latest-details-modal.tsx, the drawer variant)
// ---------------------------------------------------------------------------

@Composable
private fun CloudLatestDetailsSheet(
    item: LatestSeriesRowDto,
    baseUrl: String,
    canAddSeries: Boolean,
    onDismiss: () -> Unit,
    onViewSource: (String) -> Unit,
    onAddSeries: () -> Unit,
) {
    val statusDisplay = getStatusDisplay(item.status)
    val byline = listOfNotNull(
        item.author?.takeIf { it.isNotBlank() }?.let { "by $it" },
        item.artist?.takeIf { it.isNotBlank() && it != item.author }?.let { "art by $it" },
    ).joinToString(" · ")
    val chapters = item.chapterCount ?: item.latestChapter?.toInt()

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(
            modifier = Modifier
                .fillMaxWidth(0.94f)
                .heightIn(max = 620.dp)
                .clip(RoundedCornerShape(12.dp))
                .background(RenzoColors.Card),
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f, fill = false)
                    .verticalScroll(rememberScrollState()),
            ) {
                // Cover section.
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .width(130.dp)
                            .aspectRatio(2f / 3f)
                            .clip(RoundedCornerShape(12.dp))
                            .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                            .background(RenzoColors.Muted),
                    ) {
                        if (!item.thumbnailUrl.isNullOrBlank()) {
                            AsyncImage(
                                model = absoluteUrl(baseUrl, item.thumbnailUrl),
                                contentDescription = item.title,
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxSize(),
                            )
                        }
                    }
                    Text(
                        item.title,
                        style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Bold),
                        color = RenzoColors.Foreground,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.padding(top = 10.dp),
                    )
                    if (byline.isNotEmpty()) {
                        Text(
                            byline,
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                            textAlign = TextAlign.Center,
                            modifier = Modifier.padding(top = 4.dp),
                        )
                    }
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                        modifier = Modifier.padding(top = 6.dp),
                    ) {
                        Text(
                            statusDisplay.text,
                            style = MaterialTheme.typography.labelSmall,
                            color = Color.White,
                            modifier = Modifier
                                .clip(RoundedCornerShape(4.dp))
                                .background(statusDisplay.color)
                                .padding(horizontal = 8.dp, vertical = 2.dp),
                        )
                        if (chapters != null) {
                            Text(
                                "$chapters chapters",
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.Foreground,
                                modifier = Modifier
                                    .clip(RoundedCornerShape(4.dp))
                                    .background(RenzoColors.Secondary)
                                    .padding(horizontal = 8.dp, vertical = 2.dp),
                            )
                        }
                    }
                }
                HorizontalDivider(color = RenzoColors.Border)

                // Tags section.
                if (item.genre.isNotEmpty()) {
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                        modifier = Modifier
                            .fillMaxWidth()
                            .horizontalScroll(rememberScrollState())
                            .padding(horizontal = 14.dp, vertical = 10.dp),
                    ) {
                        item.genre.forEach { g ->
                            Text(
                                g,
                                style = MaterialTheme.typography.labelSmall.copy(fontSize = 10.sp),
                                color = RenzoColors.MutedForeground,
                                maxLines = 1,
                                modifier = Modifier
                                    .clip(RoundedCornerShape(50))
                                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(50))
                                    .background(RenzoColors.Muted)
                                    .padding(horizontal = 8.dp, vertical = 2.dp),
                            )
                        }
                    }
                    HorizontalDivider(color = RenzoColors.Border)
                }

                // Description section.
                Text(
                    item.description?.takeIf { it.isNotBlank() } ?: "No description available",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
                )
                HorizontalDivider(color = RenzoColors.Border)

                // Source badge section.
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 8.dp),
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .clip(RoundedCornerShape(4.dp))
                            .border(1.dp, RenzoColors.Border, RoundedCornerShape(4.dp))
                            .background(RenzoColors.Secondary)
                            .then(
                                if (!item.url.isNullOrBlank()) {
                                    Modifier.clickable { onViewSource(item.url) }
                                } else {
                                    Modifier
                                },
                            )
                            .padding(horizontal = 8.dp, vertical = 2.dp),
                    ) {
                        Text(
                            item.language.uppercase(),
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                        )
                        Text(
                            item.provider,
                            style = MaterialTheme.typography.bodySmall.copy(fontWeight = FontWeight.Medium),
                            color = RenzoColors.Foreground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(start = 6.dp),
                        )
                        if (!item.url.isNullOrBlank()) {
                            Icon(
                                Icons.Filled.OpenInNew,
                                contentDescription = null,
                                tint = RenzoColors.MutedForeground,
                                modifier = Modifier.padding(start = 6.dp).size(12.dp),
                            )
                        }
                    }
                }
            }

            // Footer.
            HorizontalDivider(color = RenzoColors.Border)
            Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
                if (item.inLibrary == InLibraryStatus.NOT_IN_LIBRARY) {
                    SheetButton(
                        label = if (canAddSeries) "Add to Library" else "Request Series",
                        icon = Icons.Filled.Add,
                        primary = true,
                        onClick = onAddSeries,
                    )
                    Spacer(Modifier.height(6.dp))
                }
                if (!item.url.isNullOrBlank()) {
                    SheetButton(
                        label = "View Source",
                        icon = Icons.Filled.OpenInNew,
                        primary = false,
                        onClick = { onViewSource(item.url) },
                    )
                    Spacer(Modifier.height(6.dp))
                }
                SheetButton(
                    label = "Close",
                    icon = Icons.Filled.Close,
                    primary = false,
                    onClick = onDismiss,
                )
            }
        }
    }
}

@Composable
private fun SheetButton(
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    primary: Boolean,
    onClick: () -> Unit,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
        modifier = Modifier
            .fillMaxWidth()
            .height(40.dp)
            .clip(RoundedCornerShape(8.dp))
            .then(
                if (primary) {
                    Modifier.background(RenzoColors.Primary)
                } else {
                    Modifier.border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                },
            )
            .clickable(onClick = onClick),
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = if (primary) RenzoColors.PrimaryForeground else RenzoColors.Foreground,
            modifier = Modifier.size(16.dp),
        )
        Text(
            label,
            style = MaterialTheme.typography.labelLarge,
            color = if (primary) RenzoColors.PrimaryForeground else RenzoColors.Foreground,
            modifier = Modifier.padding(start = 8.dp),
        )
    }
}
