package app.renzoshiori.client.ui.library

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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.AddCircle
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlaylistAddCheck
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.SeriesStatus
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.data.offline.OfflineRepository
import app.renzoshiori.client.ui.browse.AddSeriesSheet
import app.renzoshiori.client.ui.components.RibbonSelect
import app.renzoshiori.client.ui.components.RibbonToggleChip
import app.renzoshiori.client.ui.components.SelectOption
import app.renzoshiori.client.ui.components.TvSearchBar
import app.renzoshiori.client.ui.queue.parseUtcMillis
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable
import app.renzoshiori.client.ui.util.AdultFilter
import coil3.compose.AsyncImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

// ---------------------------------------------------------------------------
// Card sizes — the web's cardWidthOptions (XS/S/M/L/XL): same order, same
// labels, same widths (CSS px → dp) and the same per-size text scale.
// ---------------------------------------------------------------------------

private data class CardSize(
    val value: String,
    val label: String,
    val width: Dp,
    val title: TextUnit,
    val badge: TextUnit,
    val pauseDot: Dp,
    val pauseGlyph: Dp,
)

private val CARD_SIZES = listOf(
    CardSize("w-20", "XS", 80.dp, 6.4.sp, 6.4.sp, 12.dp, 6.dp),
    CardSize("w-32", "S", 128.dp, 12.sp, 12.sp, 16.dp, 8.dp),
    CardSize("w-45", "M", 180.dp, 14.sp, 12.sp, 20.dp, 10.dp),
    CardSize("w-58", "L", 232.dp, 16.sp, 14.sp, 24.dp, 12.dp),
    CardSize("w-70", "XL", 280.dp, 18.sp, 16.sp, 28.dp, 14.dp),
)

/** getResponsiveCardDefault() — anything under 1024px wide defaults to "S". */
private const val DEFAULT_CARD_SIZE = "w-32"

/** Couch distance: a television opens on L, not the phone's S. */
private const val TV_CARD_SIZE = "w-58"

private fun cardSizeOf(value: String): CardSize =
    CARD_SIZES.firstOrNull { it.value == value } ?: CARD_SIZES[1]

/**
 * The unified Library content — the same view serves the live server library
 * and the on-device offline one, switched by the Online/Offline pill hosted
 * in HomeShell's top bar (which owns this screen's ViewModel so the pill and
 * the grid share state).
 *
 * Transliterated from RenzoFrontend src/app/library/page.tsx: the whole
 * contextual ribbon (status / categories / favourites / genres / sources /
 * My-library / sort / card size / Track all / Add Series) plus the ListSeries
 * card grid, with the desktop ribbon row becoming a horizontally scrollable
 * strip on the phone.
 */
@Composable
fun LibraryContent(
    vm: LibraryViewModel,
    onOpenSeries: (seriesId: String) -> Unit,
    onOpenOfflineSeries: (seriesId: String) -> Unit,
) {
    val state by vm.state.collectAsState()
    val context = LocalContext.current
    val isTv = LocalIsTv.current

    var statusFilter by rememberSaveable { mutableStateOf("all") }
    var selectedGenre by rememberSaveable { mutableStateOf("__ALL__") }
    var selectedProvider by rememberSaveable { mutableStateOf("__ALL__") }
    var selectedCategory by rememberSaveable { mutableStateOf("__ALL__") }
    var selectedFavList by rememberSaveable { mutableStateOf("__ALL__") }
    var orderBy by rememberSaveable { mutableStateOf("title") }
    // A phone default (S = 128dp) is unreadable across a room, so a television
    // starts at L. It's still the same ribbon control, so it can be changed.
    var cardWidth by rememberSaveable { mutableStateOf(if (isTv) TV_CARD_SIZE else DEFAULT_CARD_SIZE) }
    var addSeriesOpen by rememberSaveable { mutableStateOf(false) }

    val hideAdult = AdultFilter.isHidden(context)

    // Search lives in the shell's command bar (like the web app) — this view
    // only renders the ribbon + grid.
    //
    // Except on TV: the command bar's 176dp field is a poor D-pad target and
    // sits behind the shell's chrome, so the library gets its own full-width
    // search row (with voice, where the set has a recogniser). It commits on the
    // IME Search action rather than per keystroke — a remote's IME makes every
    // character expensive, and re-filtering mid-word is just noise.
    Column(modifier = Modifier.fillMaxSize()) {
        if (isTv) {
            var draft by rememberSaveable { mutableStateOf(state.searchTerm) }
            // The shell's command bar writes the same term; follow it so the two
            // fields never disagree about what's being searched.
            LaunchedEffect(state.searchTerm) {
                if (state.searchTerm != draft.trim()) draft = state.searchTerm
            }
            TvSearchBar(
                value = draft,
                onValueChange = { draft = it },
                onSubmit = { vm.setSearch(it.trim()) },
                placeholder = "Search your library…",
                voicePrompt = "Say a series title",
            )
        }

        if (!state.offlineMode) {
            LibraryRibbon(
                state = state,
                statusFilter = statusFilter,
                onStatusFilter = { statusFilter = it },
                selectedGenre = selectedGenre,
                onGenre = { selectedGenre = it },
                selectedProvider = selectedProvider,
                onProvider = { selectedProvider = it },
                selectedCategory = selectedCategory,
                onCategory = { selectedCategory = it },
                selectedFavList = selectedFavList,
                onFavList = { selectedFavList = it },
                orderBy = orderBy,
                onOrderBy = { orderBy = it },
                cardWidth = cardWidth,
                onCardWidth = { cardWidth = it },
                onToggleViewAll = { vm.setViewAllLibraries(!state.viewAllLibraries) },
                onTrackAll = vm::trackAll,
                onAddSeries = { addSeriesOpen = true },
                hideAdult = hideAdult,
            )
        }

        // Track-all confirmation / failure line (the web's sonner toast).
        val toast = state.toast
        if (toast != null) {
            LaunchedEffect(toast) {
                delay(4000)
                vm.clearToast()
            }
            Text(
                toast,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.Primary,
                modifier = Modifier
                    .fillMaxWidth()
                    .background(RenzoColors.Primary.copy(alpha = 0.10f))
                    .padding(horizontal = 12.dp, vertical = 8.dp),
            )
        }

        when {
            state.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = RenzoColors.Primary)
            }
            state.error != null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(state.error!!, color = MaterialTheme.colorScheme.error)
            }
            state.offlineMode -> OfflineGrid(state, cardWidth, onOpenOfflineSeries)
            else -> OnlineGrid(
                state = state,
                baseUrl = vm.baseUrl,
                statusFilter = statusFilter,
                selectedGenre = selectedGenre,
                selectedProvider = selectedProvider,
                selectedCategory = selectedCategory,
                favoriteFilterIds = favoriteFilterIds(state, selectedFavList),
                orderBy = orderBy,
                cardWidth = cardWidth,
                hideAdult = hideAdult,
                onOpenSeries = onOpenSeries,
            )
        }
    }

    if (addSeriesOpen) {
        AddSeriesSheet(
            initialTitle = null,
            canAddSeries = state.canAddSeries,
            onDismiss = { addSeriesOpen = false },
            onAdded = {
                addSeriesOpen = false
                vm.refresh()
            },
        )
    }
}

// ---------------------------------------------------------------------------
// Ribbon
// ---------------------------------------------------------------------------

/** Membership set for the currently selected favourites entry (page.tsx). */
private fun favoriteFilterIds(state: LibraryUiState, selected: String): Set<String>? {
    if (selected == "__ALL__") return null
    val list = state.favoriteLists.firstOrNull { it.id == selected } ?: return null
    val ids = list.seriesIds.toMutableSet()
    if (list.parentId == null) {
        state.favoriteLists.filter { it.parentId == list.id }.forEach { ids.addAll(it.seriesIds) }
    }
    return ids
}

@Composable
private fun LibraryRibbon(
    state: LibraryUiState,
    statusFilter: String,
    onStatusFilter: (String) -> Unit,
    selectedGenre: String,
    onGenre: (String) -> Unit,
    selectedProvider: String,
    onProvider: (String) -> Unit,
    selectedCategory: String,
    onCategory: (String) -> Unit,
    selectedFavList: String,
    onFavList: (String) -> Unit,
    orderBy: String,
    onOrderBy: (String) -> Unit,
    cardWidth: String,
    onCardWidth: (String) -> Unit,
    onToggleViewAll: () -> Unit,
    onTrackAll: () -> Unit,
    onAddSeries: () -> Unit,
    hideAdult: Boolean,
) {
    val favIds = favoriteFilterIds(state, selectedFavList)

    // Live counts per status tab with genre/provider/category/favourites
    // already applied — the web's baseFilter, verbatim.
    val counts = remember(state.series, selectedGenre, selectedProvider, selectedCategory, selectedFavList) {
        val base = state.series.filter { s ->
            (selectedGenre == "__ALL__" || s.genre.contains(selectedGenre)) &&
                (selectedProvider == "__ALL__" || s.providers.any { it.provider == selectedProvider }) &&
                (selectedCategory == "__ALL__" || s.category == selectedCategory) &&
                (favIds == null || favIds.contains(s.id))
        }
        mapOf(
            "all" to base.size,
            "active" to base.count {
                it.status != SeriesStatus.COMPLETED && it.status != SeriesStatus.PUBLISHING_FINISHED &&
                    it.isActive && !it.pausedDownloads
            },
            "paused" to base.count { it.pausedDownloads },
            "unassigned" to base.count { it.hasUnknown },
            "completed" to base.count {
                it.status == SeriesStatus.COMPLETED || it.status == SeriesStatus.PUBLISHING_FINISHED
            },
        )
    }

    val genres = remember(state.series, hideAdult) {
        state.series.flatMap { it.genre }
            .filter { it.isNotBlank() }
            .filter { !hideAdult || !AdultFilter.isAdultItem(null, listOf(it)) }
            .distinct()
            .sortedBy { it.lowercase() }
    }
    val providers = remember(state.series) {
        state.series.flatMap { it.providers }.map { it.provider }
            .filter { it.isNotBlank() }.distinct().sortedBy { it.lowercase() }
    }
    val categories = remember(state.settings) {
        (state.settings?.categories ?: emptyList()).filter { it.isNotBlank() }.sortedBy { it.lowercase() }
    }

    // Favourites entries: each top-level tab followed by its indented sub-lists;
    // a tab's count aggregates its own series plus every sub-list's.
    val favoriteOptions = remember(state.favoriteLists) {
        val lists = state.favoriteLists
        val out = ArrayList<SelectOption>()
        lists.filter { it.parentId == null }.sortedBy { it.sortOrder }.forEach { tab ->
            val children = lists.filter { it.parentId == tab.id }.sortedBy { it.sortOrder }
            val aggregate = HashSet(tab.seriesIds)
            children.forEach { aggregate.addAll(it.seriesIds) }
            out.add(SelectOption(tab.id, tab.name, count = aggregate.size))
            children.forEach { out.add(SelectOption(it.id, it.name, count = it.seriesIds.size, indented = true)) }
        }
        out
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 8.dp, vertical = 6.dp),
    ) {
        // Status filter — status-colored dots + live count badges.
        RibbonSelect(
            options = listOf(
                SelectOption("all", "All", dotColor = Color.White.copy(alpha = 0.6f), count = counts["all"]),
                SelectOption("active", "Active", dotColor = Color(0xFF22C55E), count = counts["active"]),
                SelectOption("paused", "Paused", dotColor = Color(0xFFEAB308), count = counts["paused"]),
                SelectOption("unassigned", "Unassigned", dotColor = Color(0xFFF59E0B), count = counts["unassigned"]),
                SelectOption("completed", "Completed", dotColor = Color(0xFF3B82F6), count = counts["completed"]),
            ),
            value = statusFilter,
            onChange = onStatusFilter,
        )

        // Categories — only when categorized folders are enabled in settings.
        if (state.settings?.categorizedFolders == true) {
            RibbonSelect(
                options = listOf(SelectOption("__ALL__", "All Categories")) +
                    categories.map { SelectOption(it, it) },
                value = selectedCategory,
                onChange = onCategory,
                placeholder = "All Categories",
            )
        }

        // Favourites — hidden until the user creates their first list.
        if (favoriteOptions.isNotEmpty()) {
            RibbonSelect(
                options = listOf(SelectOption("__ALL__", "Favourites: All")) + favoriteOptions,
                value = selectedFavList,
                onChange = onFavList,
                placeholder = "Favourites",
            )
        }

        // Genres.
        RibbonSelect(
            options = listOf(SelectOption("__ALL__", "All Genres")) + genres.map { SelectOption(it, it) },
            value = selectedGenre,
            onChange = onGenre,
            placeholder = "All Genres",
        )

        // Sources.
        RibbonSelect(
            options = listOf(SelectOption("__ALL__", "All Sources")) + providers.map { SelectOption(it, it) },
            value = selectedProvider,
            onChange = onProvider,
            placeholder = "All Sources",
        )

        // Right cluster: My library / sort / card size / Track all / Add Series.
        if (state.canOwner) {
            RibbonToggleChip(
                label = if (state.viewAllLibraries) "All libraries" else "My library",
                active = state.viewAllLibraries,
                onClick = onToggleViewAll,
            )
        }

        RibbonSelect(
            options = listOf(
                SelectOption("title", "Alphabetical"),
                SelectOption("lastChange", "Last Change"),
            ),
            value = orderBy,
            onChange = onOrderBy,
        )

        RibbonSelect(
            options = CARD_SIZES.map { SelectOption(it.value, it.label) },
            value = cardWidth,
            onChange = onCardWidth,
            placeholder = "Card Size",
            maxTriggerWidth = 32.dp,
        )

        // Track all — self-hides when no tracker is connected, like the web.
        if (state.connectedTrackers.isNotEmpty()) {
            RibbonToggleChip(
                label = "Track all",
                active = state.trackingAll,
                onClick = onTrackAll,
                icon = Icons.Filled.PlaylistAddCheck,
            )
        }

        // Add Series — relabelled "Request Series" below Manager, exactly as the
        // web relabels the same always-available button.
        val isTv = LocalIsTv.current
        val addFocus = rememberFocusState()
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .height(if (isTv) 40.dp else 32.dp)
                .clip(RoundedCornerShape(8.dp))
                .background(RenzoColors.Primary)
                .then(
                    if (isTv) {
                        Modifier
                            .focusRing(addFocus.focused, 8.dp)
                            .tvClickable(onFocused = addFocus::set, onClick = onAddSeries)
                    } else {
                        Modifier.clickable(onClick = onAddSeries)
                    },
                )
                .padding(horizontal = 12.dp),
        ) {
            Icon(
                Icons.Filled.AddCircle,
                contentDescription = null,
                tint = RenzoColors.PrimaryForeground,
                modifier = Modifier.size(16.dp),
            )
            Text(
                if (state.canAddSeries) "Add Series" else "Request Series",
                style = MaterialTheme.typography.labelMedium,
                color = RenzoColors.PrimaryForeground,
                maxLines = 1,
                modifier = Modifier.padding(start = 6.dp),
            )
        }
    }
}

// ---------------------------------------------------------------------------
// Online grid
// ---------------------------------------------------------------------------

@Composable
private fun OnlineGrid(
    state: LibraryUiState,
    baseUrl: String,
    statusFilter: String,
    selectedGenre: String,
    selectedProvider: String,
    selectedCategory: String,
    favoriteFilterIds: Set<String>?,
    orderBy: String,
    cardWidth: String,
    hideAdult: Boolean,
    onOpenSeries: (String) -> Unit,
) {
    val size = cardSizeOf(cardWidth)
    val search = state.searchTerm.trim()

    // ListSeries' own filtering (adult + search) then the page's filterFn/sortFn.
    val filtered = remember(
        state.series, search, statusFilter, selectedGenre, selectedProvider,
        selectedCategory, favoriteFilterIds, orderBy, hideAdult,
    ) {
        state.series
            .filter { !hideAdult || !AdultFilter.isAdultItem(it.isNsfw.takeIf { flag -> flag }, it.genre) }
            .filter { search.isEmpty() || it.title.contains(search, ignoreCase = true) }
            .filter { s ->
                val matchesTab = when (statusFilter) {
                    "completed" -> s.status == SeriesStatus.COMPLETED || s.status == SeriesStatus.PUBLISHING_FINISHED
                    "active" -> s.status != SeriesStatus.COMPLETED && s.status != SeriesStatus.PUBLISHING_FINISHED &&
                        s.isActive && !s.pausedDownloads
                    "paused" -> s.pausedDownloads
                    "unassigned" -> s.hasUnknown
                    else -> true
                }
                val matchesGenre = selectedGenre == "__ALL__" || s.genre.contains(selectedGenre)
                val matchesProvider =
                    selectedProvider == "__ALL__" || s.providers.any { it.provider == selectedProvider }
                val matchesCategory = selectedCategory == "__ALL__" || s.category == selectedCategory
                val matchesFavorites = favoriteFilterIds == null || favoriteFilterIds.contains(s.id)
                matchesTab && matchesGenre && matchesProvider && matchesCategory && matchesFavorites
            }
            .let { list ->
                if (orderBy == "lastChange") {
                    list.sortedByDescending { it.lastChangeUTC?.let(::parseUtcMillis) ?: 0L }
                } else {
                    list.sortedBy { it.title.lowercase() }
                }
            }
    }

    if (filtered.isEmpty()) {
        EmptyLibraryState(search)
        return
    }

    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = size.width),
        contentPadding = PaddingValues(start = 8.dp, end = 8.dp, top = 8.dp, bottom = 64.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        items(filtered, key = { it.id }) { series ->
            val effectiveStatus = if (series.isActive) series.status else SERIES_STATUS_DISABLED
            SeriesCard(
                title = series.title,
                coverUrl = series.thumbnailUrl.takeIf { it.isNotBlank() }?.let { absoluteUrl(baseUrl, it) },
                size = size,
                // The status strip is hidden while sorting by Last Change: it
                // clashes with the age-graded card border (web comment).
                statusColor = if (orderBy == "lastChange") null else getStatusDisplay(effectiveStatus).color,
                ringColor = if (orderBy == "lastChange") lastChangeRingColor(series.lastChangeUTC) else null,
                providerBadge = series.lastChangeProvider?.provider,
                lastChapter = series.lastChapter,
                lastChapterColor = getStatusDisplay(effectiveStatus).color,
                paused = series.pausedDownloads,
                hasUnknown = series.hasUnknown,
                onClick = { onOpenSeries(series.id) },
            )
        }
    }
}

/** ListSeries' empty / no-results block, wording verbatim. */
@Composable
private fun EmptyLibraryState(search: String) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
        modifier = Modifier.fillMaxSize().padding(24.dp),
    ) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier.size(80.dp).clip(CircleShape).background(RenzoColors.Muted),
        ) {
            Icon(
                Icons.AutoMirrored.Filled.MenuBook,
                contentDescription = null,
                tint = RenzoColors.MutedForeground,
                modifier = Modifier.size(40.dp),
            )
        }
        Spacer(Modifier.height(16.dp))
        Text(
            if (search.isNotEmpty()) "No results for \"$search\"" else "No series found",
            style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.Medium),
            color = RenzoColors.Foreground,
            textAlign = TextAlign.Center,
        )
        Text(
            if (search.isNotEmpty()) "Try a different search term." else "Add some manga to get started.",
            style = MaterialTheme.typography.bodyMedium,
            color = RenzoColors.MutedForeground,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 4.dp),
        )
    }
}

// ---------------------------------------------------------------------------
// Offline grid (offline-library-grid.tsx)
// ---------------------------------------------------------------------------

@Composable
private fun OfflineGrid(
    state: LibraryUiState,
    cardWidth: String,
    onOpenOfflineSeries: (String) -> Unit,
) {
    val size = cardSizeOf(cardWidth)
    val search = state.searchTerm.trim()
    val filtered = state.offlineSeries.filter {
        search.isEmpty() || it.title.contains(search, ignoreCase = true)
    }

    if (filtered.isEmpty()) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
            modifier = Modifier.fillMaxSize().padding(24.dp),
        ) {
            Icon(
                Icons.Filled.WifiOff,
                contentDescription = null,
                tint = RenzoColors.MutedForeground.copy(alpha = 0.5f),
                modifier = Modifier.size(32.dp),
            )
            Spacer(Modifier.height(8.dp))
            Text(
                "Nothing saved offline yet",
                style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                color = RenzoColors.MutedForeground,
            )
            Text(
                "Open a series and tap \"Save offline\" on a chapter to read it without a connection.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 4.dp, start = 24.dp, end = 24.dp),
            )
        }
        return
    }

    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = size.width),
        contentPadding = PaddingValues(start = 8.dp, end = 8.dp, top = 8.dp, bottom = 64.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        items(filtered, key = { it.seriesId }) { series ->
            OfflineSeriesCard(series, size) { onOpenOfflineSeries(series.seriesId) }
        }
    }
}

@Composable
private fun OfflineSeriesCard(
    series: OfflineRepository.OfflineSeries,
    size: CardSize,
    onClick: () -> Unit,
) {
    val renzoApp = LocalContext.current.applicationContext as RenzoApp
    var cover by remember(series.seriesId) { mutableStateOf<ByteArray?>(null) }
    LaunchedEffect(series.coverPath) {
        val path = series.coverPath
        cover = if (path == null) null else {
            withContext(Dispatchers.IO) { runCatching { renzoApp.offline.readPage(path) }.getOrNull() }
        }
    }

    val isTv = LocalIsTv.current
    TvFocusTile(onClick = onClick) {
    Column(modifier = if (isTv) Modifier else Modifier.clickable(onClick = onClick)) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(6.dp))
                .background(RenzoColors.Muted),
        ) {
            val bytes = cover
            if (bytes != null) {
                AsyncImage(
                    model = bytes,
                    contentDescription = series.title,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            } else {
                Icon(
                    Icons.Filled.WifiOff,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground.copy(alpha = 0.4f),
                    modifier = Modifier.align(Alignment.Center).size(24.dp),
                )
            }
            Text(
                series.title,
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
        Text(
            "${series.chapterCount} ch · ${formatBytes(series.bytes)}",
            style = MaterialTheme.typography.labelSmall,
            color = RenzoColors.MutedForeground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
        )
    }
    }
}

// ---------------------------------------------------------------------------
// Card
// ---------------------------------------------------------------------------

/**
 * The web ListSeries card, cloned: 2/3 cover, a 2px status strip across the top
 * edge, provider badge top-left (black/70), the status-colored last-chapter
 * badge top-RIGHT, an amber attention dot for unassigned providers, the yellow
 * pause glyph riding the title strip, and the centered semibold title bar
 * (black/60) along the bottom. When sorting by Last Change the whole card gets
 * the age-graded 1.5px border instead of the status strip.
 */
@Composable
private fun SeriesCard(
    title: String,
    coverUrl: String?,
    size: CardSize,
    statusColor: Color?,
    ringColor: Color?,
    providerBadge: String?,
    lastChapter: Double?,
    lastChapterColor: Color,
    paused: Boolean,
    hasUnknown: Boolean,
    onClick: () -> Unit,
) {
    val isTv = LocalIsTv.current
    TvFocusTile(onClick = onClick) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio(2f / 3f)
                .then(
                    if (ringColor != null) {
                        Modifier
                            .border(1.5.dp, ringColor, RoundedCornerShape(6.dp))
                            .padding(1.5.dp)
                    } else {
                        Modifier
                    },
                )
                .clip(RoundedCornerShape(6.dp))
                .background(RenzoColors.Muted)
                // On TV the wrapper owns the click (it owns the focus ring too).
                .then(if (isTv) Modifier else Modifier.clickable(onClick = onClick)),
        ) {
        if (coverUrl != null) {
            AsyncImage(
                model = coverUrl,
                contentDescription = title,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }

        // 2px status strip across the top edge.
        if (statusColor != null) {
            Box(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .fillMaxWidth()
                    .height(2.dp)
                    .background(statusColor),
            )
        }

        // Provider badge — top-left.
        if (!providerBadge.isNullOrBlank()) {
            Text(
                providerBadge,
                style = MaterialTheme.typography.labelSmall.copy(
                    fontSize = size.badge,
                    fontWeight = FontWeight.SemiBold,
                ),
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
        }

        // Last-chapter badge — top-right, filled with the status color.
        if (lastChapter != null) {
            Text(
                formatChapter(lastChapter),
                style = MaterialTheme.typography.labelSmall.copy(
                    fontSize = size.badge,
                    fontWeight = FontWeight.SemiBold,
                ),
                color = Color.White,
                maxLines = 1,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(4.dp)
                    .clip(RoundedCornerShape(4.dp))
                    .background(lastChapterColor)
                    .padding(horizontal = 8.dp, vertical = 2.dp),
            )
        }

        // Attention dot — this series has unassigned providers needing a match.
        if (hasUnknown) {
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .align(Alignment.BottomStart)
                    .padding(start = 4.dp, bottom = 28.dp)
                    .size(16.dp)
                    .clip(CircleShape)
                    .background(Color(0xFFF59E0B)),
            ) {
                Icon(
                    Icons.Filled.Warning,
                    contentDescription = "Has unassigned providers",
                    tint = Color(0xFFFFFBEB),
                    modifier = Modifier.size(10.dp),
                )
            }
        }

        // Paused indicator — yellow circle riding just above the title strip.
        if (paused) {
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(end = 4.dp, bottom = 26.dp)
                    .size(size.pauseDot)
                    .clip(CircleShape)
                    .background(Color(0xFFEAB308)),
            ) {
                Icon(
                    Icons.Filled.Pause,
                    contentDescription = "Downloads paused",
                    tint = Color.Black,
                    modifier = Modifier.size(size.pauseGlyph),
                )
            }
        }

        // Title bar.
        Text(
            title,
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
}

/**
 * Focus wrapper for a cover tile.
 *
 * The ring can't live on the card itself: a border modifier draws inside the
 * node's bounds and *before* its children, so the full-bleed cover image would
 * paint straight over it. It goes on a wrapper instead, with a 3dp gutter
 * reserved whether focused or not — a tile that grew on focus would reflow its
 * whole row under the cursor. On touch this is a pass-through.
 *
 * Scroll-into-view comes free: `focusable()` (inside `tvClickable`) asks its
 * scrollable parent to bring it into view, so a focused tile in a lazy grid is
 * never stranded off-screen.
 */
@Composable
private fun TvFocusTile(onClick: () -> Unit, content: @Composable () -> Unit) {
    val isTv = LocalIsTv.current
    val focus = rememberFocusState()
    if (!isTv) {
        content()
        return
    }
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .focusRing(focus.focused, 9.dp)
            .tvClickable(onFocused = focus::set, onClick = onClick)
            .padding(3.dp),
    ) {
        content()
    }
}

// ---------------------------------------------------------------------------
// Shared bits used by the shell and the other screens
// ---------------------------------------------------------------------------

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
            // Never let a squeezed command bar break the word across lines
            // ("Onlin / e") — the pill is one token, like the web's
            // whitespace-nowrap.
            maxLines = 1,
            softWrap = false,
        )
    }
}

fun formatChapter(n: Double): String =
    if (n == n.toLong().toDouble()) n.toLong().toString() else n.toString()

/** formatBytes() from the web downloads page — shared with DownloadsScreen. */
fun formatBytes(n: Long): String {
    if (n < 1024) return "$n B"
    val units = listOf("KB", "MB", "GB")
    var v = n / 1024.0
    var i = 0
    while (v >= 1024 && i < units.size - 1) {
        v /= 1024.0
        i++
    }
    return if (v < 10) String.format("%.1f %s", v, units[i]) else String.format("%.0f %s", v, units[i])
}
