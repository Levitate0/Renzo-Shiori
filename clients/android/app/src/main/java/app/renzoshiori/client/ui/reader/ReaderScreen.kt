package app.renzoshiori.client.ui.reader

import android.app.Application
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.BookmarkBorder
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.SingletonImageLoader
import coil3.compose.AsyncImage
import coil3.compose.AsyncImagePainter
import coil3.compose.LocalPlatformContext
import coil3.request.ImageRequest
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

/**
 * The reader — a transliteration of the web reader (RenzoFrontend
 * src/app/reader/page.tsx): translucent top chrome, a bottom scrubber, the full
 * Reader Settings panel, the chapter list, tap zones, chapter transitions and
 * infinite scroll across chapter boundaries.
 *
 * Layout modes:
 *  - paged / paged-rtl / double: HorizontalPager, one slot per swipe.
 *  - webtoon / longstrip / vertical: a LazyColumn strip. The current page comes
 *    from the item under a probe line a third of the way down the viewport —
 *    the same tracking rule the web reader uses, but Compose hands us
 *    visibility info directly instead of DOM rect math. The end-of-chapter
 *    block is a real list item, so reaching it drives completion exactly like
 *    the web reader's sentinel page.
 */
@Composable
fun ReaderScreen(
    seriesId: String,
    chapterNumber: Double,
    onExit: () -> Unit,
    vm: ReaderViewModel = viewModel(
        key = "reader-$seriesId-$chapterNumber",
        factory = ReaderViewModel.factory(
            LocalContext.current.applicationContext as Application,
            seriesId,
            chapterNumber,
        ),
    ),
) {
    val state by vm.state.collectAsState()
    val settings = state.settings
    val mode = state.resolvedMode()
    val context = LocalContext.current

    var chromeVisible by remember { mutableStateOf(true) }
    var settingsOpen by remember { mutableStateOf(false) }
    var chaptersOpen by remember { mutableStateOf(false) }
    // Page the bottom scrubber asked for; the active renderer scrolls to it and clears it.
    var seekTarget by remember { mutableStateOf<Int?>(null) }
    // Live thumb position while dragging, so the scrubber doesn't snap back to
    // the tracked page between frames.
    var scrubbing by remember { mutableStateOf<Float?>(null) }

    LaunchedEffect(state.toast) {
        val message = state.toast
        if (message != null) {
            Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
            vm.consumeToast()
        }
    }

    Box(modifier = Modifier.fillMaxSize().background(Color(settings.background.argb))) {
        when {
            state.error != null -> Column(
                modifier = Modifier.fillMaxSize().padding(24.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(state.error ?: "", color = ReaderPalette.Text80, textAlign = TextAlign.Center)
                Spacer(Modifier.height(16.dp))
                Button(
                    onClick = onExit,
                    shape = MaterialTheme.shapes.small,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = RenzoColors.Secondary,
                        contentColor = ReaderPalette.Text,
                    ),
                ) { Text("Go back") }
            }

            state.loading -> Column(
                modifier = Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                CircularProgressIndicator(color = ReaderPalette.Text70, strokeWidth = 2.dp, modifier = Modifier.size(32.dp))
                Spacer(Modifier.height(16.dp))
                val opening = state.openingLabel
                if (opening != null) {
                    Text(
                        "OPENING",
                        fontSize = 12.sp,
                        letterSpacing = 1.8.sp,
                        color = ReaderPalette.Text40,
                    )
                    Text(
                        opening,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                        color = ReaderPalette.Text,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.padding(top = 4.dp, start = 24.dp, end = 24.dp),
                    )
                } else {
                    Text("Loading chapter…", fontSize = 14.sp, color = ReaderPalette.Text50)
                }
            }

            state.locked -> LockedChapterScreen(
                chapterLabel = state.chapters.firstOrNull { it.number == state.chapterNumber }
                    ?.let { state.nameOf(it) } ?: "Chapter ${trimNumber(state.chapterNumber)}",
                url = state.lockedUrl,
                hasPrev = state.hasPrev,
                hasNext = state.hasNext,
                checking = state.unlockChecking,
                onCheckNow = vm::checkUnlock,
                onPrev = { vm.goToChapter(-1) },
                onNext = { vm.goToChapter(1) },
                onExit = onExit,
            )

            else -> key(state.chapterNumber) {
                if (mode.continuous) {
                    ContinuousReader(
                        state = state,
                        mode = mode,
                        seekTarget = seekTarget,
                        onSeekHandled = { seekTarget = null },
                        onPosition = vm::onPosition,
                        onImageLoaded = vm::onImageLoaded,
                        onNearEnd = vm::maybeAppendNext,
                        onRetryAppend = vm::retryAppend,
                        onToggleChrome = { chromeVisible = !chromeVisible },
                        onNextChapter = { vm.goToChapter(1) },
                        onPrevChapter = { vm.goToChapter(-1) },
                        onExit = onExit,
                    )
                } else {
                    PagedReader(
                        state = state,
                        mode = mode,
                        seekTarget = seekTarget,
                        onSeekHandled = { seekTarget = null },
                        onPosition = vm::onPosition,
                        onImageLoaded = vm::onImageLoaded,
                        onToggleChrome = { chromeVisible = !chromeVisible },
                        onNextChapter = { vm.goToChapter(1) },
                        onPrevChapter = { vm.goToChapter(-1) },
                        onExit = onExit,
                    )
                }
            }
        }

        // "Loading next chapter…" (infinite scroll)
        if (state.appending) {
            Pill(
                text = "Loading next chapter…",
                spinner = true,
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 72.dp),
            )
        }
        // Brief confirmation that a new chapter opened
        val arrived = state.arrivedLabel
        if (arrived != null && !state.loading) {
            Pill(
                text = arrived,
                spinner = false,
                modifier = Modifier.align(Alignment.TopCenter).padding(top = 72.dp),
            )
        }

        // ── Top chrome ──
        if (chromeVisible && !state.loading) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .fillMaxWidth()
                    .background(ReaderPalette.Chrome)
                    .statusBarsPadding()
                    .padding(horizontal = 8.dp, vertical = 6.dp),
            ) {
                IconButton(onClick = onExit) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = ReaderPalette.Text)
                }
                Column(modifier = Modifier.weight(1f).padding(horizontal = 4.dp)) {
                    Text(
                        state.seriesTitle,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                        color = ReaderPalette.Text,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    val next = state.nextChapterName
                    Text(
                        state.activeLabel + (if (next != null) " · next: $next" else ""),
                        fontSize = 12.sp,
                        color = ReaderPalette.Text60,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                val bookmarkable = state.activeChapter
                if (bookmarkable != null) {
                    IconButton(onClick = vm::toggleBookmark) {
                        Icon(
                            if (bookmarkable.bookmarked) Icons.Filled.Bookmark else Icons.Filled.BookmarkBorder,
                            contentDescription = if (bookmarkable.bookmarked) "Remove bookmark" else "Bookmark chapter",
                            tint = if (bookmarkable.bookmarked) ReaderPalette.Pink400 else ReaderPalette.Text,
                        )
                    }
                }
                IconButton(onClick = { chaptersOpen = true }) {
                    Icon(Icons.AutoMirrored.Filled.List, contentDescription = "Chapters", tint = ReaderPalette.Text)
                }
                IconButton(onClick = { settingsOpen = true }) {
                    Icon(Icons.Filled.Tune, contentDescription = "Reader settings", tint = ReaderPalette.Text)
                }
            }
        }

        // ── Bottom chrome ──
        if (chromeVisible && !state.loading && state.error == null && !state.locked && state.activePageCount > 0) {
            val pageCount = state.activePageCount
            val current = state.currentPage.coerceIn(0, (pageCount - 1).coerceAtLeast(0))
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .background(ReaderPalette.Chrome)
                    .navigationBarsPadding()
                    .padding(horizontal = 8.dp, vertical = 4.dp),
            ) {
                IconButton(onClick = { vm.goToChapter(-1) }) {
                    Icon(Icons.Filled.ChevronLeft, contentDescription = "Previous chapter", tint = ReaderPalette.Text)
                }
                Box(modifier = Modifier.weight(1f)) {
                    // Right-to-left paged reading mirrors the scrubber, as on the web.
                    CompositionLocalProvider(
                        LocalLayoutDirection provides
                            if (mode.rtl) LayoutDirection.Rtl else LayoutDirection.Ltr,
                    ) {
                        Slider(
                            value = scrubbing ?: current.toFloat(),
                            onValueChange = { raw ->
                                scrubbing = raw
                                seekTarget = raw.roundToInt().coerceIn(0, (pageCount - 1).coerceAtLeast(0))
                            },
                            onValueChangeFinished = { scrubbing = null },
                            valueRange = 0f..(pageCount - 1).coerceAtLeast(1).toFloat(),
                            colors = SliderDefaults.colors(
                                thumbColor = RenzoColors.Primary,
                                activeTrackColor = RenzoColors.Primary,
                                inactiveTrackColor = Color(0x33FFFFFF),
                            ),
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }
                if (settings.showPageNumber) {
                    val shown = (scrubbing?.roundToInt() ?: current).coerceIn(0, pageCount - 1)
                    Text(
                        "${(shown + 1).coerceAtMost(pageCount)} / $pageCount",
                        fontSize = 12.sp,
                        color = ReaderPalette.Text70,
                    )
                }
                IconButton(onClick = { vm.goToChapter(1) }) {
                    Icon(Icons.Filled.ChevronRight, contentDescription = "Next chapter", tint = ReaderPalette.Text)
                }
            }
        }
    }

    if (settingsOpen) {
        ReaderSettingsSheet(
            state = state,
            mode = mode,
            onDismiss = { settingsOpen = false },
            onModeChange = vm::setMode,
            onSettingsChange = vm::updateSettings,
            onClearCache = vm::clearStreamCache,
            onToggleChapterRead = vm::toggleChapterRead,
        )
    }
    if (chaptersOpen) {
        ReaderChapterListSheet(
            state = state,
            onDismiss = { chaptersOpen = false },
            onPick = { number ->
                chaptersOpen = false
                vm.jumpToChapter(number)
            },
        )
    }
}

/** The web's floating status pills (bg-black/70, rounded-full, backdrop blur). */
@Composable
private fun Pill(text: String, spinner: Boolean, modifier: Modifier = Modifier) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .clip(RoundedCornerShape(999.dp))
            .background(ReaderPalette.Chrome)
            .padding(horizontal = 14.dp, vertical = 6.dp),
    ) {
        if (spinner) {
            CircularProgressIndicator(
                color = ReaderPalette.Text,
                strokeWidth = 1.5.dp,
                modifier = Modifier.size(14.dp),
            )
            Spacer(Modifier.width(8.dp))
        }
        Text(text, fontSize = 13.sp, color = ReaderPalette.Text)
    }
}

// ── Continuous (webtoon / longstrip / vertical) ───────────────────────────

private const val KIND_PAGE = 0
private const val KIND_DIVIDER = 1
private const val KIND_END = 2

private data class StripItem(val segIndex: Int, val pageIndex: Int, val kind: Int)

@Composable
private fun ContinuousReader(
    state: ReaderUiState,
    mode: ResolvedMode,
    seekTarget: Int?,
    onSeekHandled: () -> Unit,
    onPosition: (Int, Int) -> Unit,
    onImageLoaded: (Int, Int) -> Unit,
    onNearEnd: () -> Unit,
    onRetryAppend: () -> Unit,
    onToggleChrome: () -> Unit,
    onNextChapter: () -> Unit,
    onPrevChapter: () -> Unit,
    onExit: () -> Unit,
) {
    val settings = state.settings
    val segments = state.segments
    val listState: LazyListState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    // Real aspect ratios as images decode, so a page box stops being a guess
    // (the web reader's loadedDimsRef — without it, boxes leave gaps).
    val loadedAspect = remember { mutableStateMapOf<String, Float>() }
    // A page whose image never arrives used to leave an empty box with no
    // explanation and no way out — indistinguishable from the reader hanging.
    // These track per-page failure and a manual retry counter (bumping it
    // changes the request key, so Coil refetches instead of replaying its
    // cached failure).
    val loadFailed = remember { mutableStateMapOf<String, Boolean>() }
    val retryTick = remember { mutableStateMapOf<String, Int>() }
    val placeholderHeight = (LocalConfiguration.current.screenHeightDp * 0.7f).dp

    val strip = remember(segments) {
        buildList {
            segments.forEachIndexed { si, seg ->
                if (si > 0) add(StripItem(si, -1, KIND_DIVIDER))
                for (p in 0 until seg.pageCount) add(StripItem(si, p, KIND_PAGE))
            }
            add(StripItem(segments.lastIndex.coerceAtLeast(0), -1, KIND_END))
        }
    }
    // Item index of the first page of each segment — used for seeking.
    val segStart = remember(strip) {
        val out = HashMap<Int, Int>()
        strip.forEachIndexed { index, item ->
            if (item.kind == KIND_PAGE && !out.containsKey(item.segIndex)) out[item.segIndex] = index
        }
        out
    }

    // Land on the resume page when re-opening a partially-read chapter.
    LaunchedEffect(segments.firstOrNull()?.key) {
        if (state.resumePage > 0) listState.scrollToItem(state.resumePage)
    }

    // Scrubber seek — relative to the segment currently on screen.
    LaunchedEffect(seekTarget) {
        val target = seekTarget ?: return@LaunchedEffect
        val base = segStart[state.activeSegIndex] ?: 0
        listState.scrollToItem((base + target).coerceIn(0, (strip.size - 1).coerceAtLeast(0)))
        onSeekHandled()
    }

    // Page tracking against a probe line a third of the way down the viewport.
    val currentItem by remember {
        derivedStateOf {
            val info = listState.layoutInfo
            val probe = info.viewportStartOffset + info.viewportSize.height / 3
            info.visibleItemsInfo.lastOrNull { it.offset <= probe }?.index ?: 0
        }
    }
    LaunchedEffect(strip) {
        snapshotFlow { currentItem }.collect { index ->
            val item = strip.getOrNull(index) ?: return@collect
            when (item.kind) {
                KIND_PAGE -> onPosition(item.segIndex, item.pageIndex)
                // Reaching the end block is unambiguous proof the chapter is
                // done — resolve progress to exactly 100%.
                KIND_END -> {
                    val last = segments.lastIndex
                    val count = segments.lastOrNull()?.pageCount ?: 0
                    if (last >= 0 && count > 0) onPosition(last, count - 1)
                }
                else -> Unit // divider: leave the tracked position alone
            }
        }
    }
    // Infinite scroll: pull the next chapter as the bottom approaches.
    LaunchedEffect(strip) {
        snapshotFlow { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0 }
            .collect { last -> if (last >= strip.size - 3) onNearEnd() }
    }

    val widthFraction = (settings.maxWidthPct / 100f).coerceIn(0.2f, 1f)

    LazyColumn(
        state = listState,
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement =
            if (mode == ResolvedMode.VERTICAL) Arrangement.spacedBy(settings.gapPx.dp) else Arrangement.Top,
        modifier = Modifier
            .fillMaxSize()
            .pointerInput(settings.tapNavigation, settings.tapAdvancePct) {
                detectTapGestures { offset ->
                    if (!settings.tapNavigation) {
                        onToggleChrome()
                        return@detectTapGestures
                    }
                    val x = offset.x / size.width.toFloat()
                    val amount =
                        listState.layoutInfo.viewportSize.height * (settings.tapAdvancePct / 100f)
                    when {
                        x > 0.7f -> scope.launch { listState.animateScrollBy(amount) }
                        x < 0.3f -> scope.launch { listState.animateScrollBy(-amount) }
                        else -> onToggleChrome()
                    }
                }
            },
    ) {
        items(
            count = strip.size,
            key = { index ->
                val item = strip[index]
                "${segments.getOrNull(item.segIndex)?.key ?: item.segIndex}:${item.kind}:${item.pageIndex}"
            },
        ) { index ->
            val item = strip[index]
            val seg = segments.getOrNull(item.segIndex)
            when {
                item.kind == KIND_PAGE && seg != null -> {
                    val cacheKey = "${seg.key}:${item.pageIndex}"
                    val serverAspect = seg.dims.getOrNull(item.pageIndex)?.let { dims ->
                        dims.first.toFloat() / dims.second.toFloat()
                    }
                    val aspect = loadedAspect[cacheKey] ?: serverAspect
                    val attempt = retryTick[cacheKey] ?: 0
                    val failed = loadFailed[cacheKey] == true
                    var settled by remember(cacheKey, attempt) { mutableStateOf(false) }
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .fillMaxWidth(widthFraction)
                            .then(
                                if (aspect != null && aspect > 0f) Modifier.aspectRatio(aspect)
                                else Modifier.height(placeholderHeight),
                            ),
                    ) {
                        val rawModel = seg.pages.getOrNull(item.pageIndex)
                        AsyncImage(
                            model = if (attempt > 0 && rawModel is String) {
                                rawModel + (if (rawModel.contains('?')) "&" else "?") + "retry=" + attempt
                            } else {
                                rawModel
                            },
                            contentDescription = "Page ${item.pageIndex + 1}",
                            contentScale = ContentScale.FillWidth,
                            onSuccess = { success ->
                                settled = true
                                loadFailed[cacheKey] = false
                                val w = success.result.image.width
                                val h = success.result.image.height
                                if (w > 0 && h > 0) {
                                    loadedAspect[cacheKey] = w.toFloat() / h.toFloat()
                                    onImageLoaded(w, h)
                                }
                            },
                            onError = {
                                settled = true
                                loadFailed[cacheKey] = true
                            },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        if (failed) {
                            Column(
                                horizontalAlignment = Alignment.CenterHorizontally,
                                modifier = Modifier.clickable {
                                    loadFailed[cacheKey] = false
                                    retryTick[cacheKey] = attempt + 1
                                },
                            ) {
                                Text(
                                    "Page ${item.pageIndex + 1} didn't load",
                                    fontSize = 13.sp,
                                    color = ReaderPalette.Text70,
                                )
                                Text(
                                    "Tap to retry",
                                    fontSize = 12.sp,
                                    color = RenzoColors.Primary,
                                    modifier = Modifier.padding(top = 4.dp),
                                )
                            }
                        } else if (!settled) {
                            CircularProgressIndicator(
                                color = ReaderPalette.Text35,
                                strokeWidth = 2.dp,
                                modifier = Modifier.size(20.dp),
                            )
                        }
                    }
                }

                item.kind == KIND_DIVIDER && seg != null -> ChapterDivider(
                    finishedLabel = segments.getOrNull(item.segIndex - 1)?.name ?: "",
                    nextLabel = seg.name,
                    modifier = Modifier.fillMaxWidth(),
                )

                else -> {
                    val last = segments.lastOrNull()
                    // Full-screen like the web's sentinel page: guaranteed scroll
                    // distance below the final image is what lets the tracker
                    // register the last page and mark the chapter read.
                    Box(modifier = Modifier.fillMaxWidth().fillParentMaxHeight()) {
                        EndOfChapter(
                            chapterLabel = last?.name ?: "",
                            nextLabel = state.nextChapterName,
                            hasNext = state.hasNext,
                            hasPrev = state.hasPrev,
                            infinite = settings.infiniteScroll,
                            onNext = onNextChapter,
                            onPrev = onPrevChapter,
                            onExit = onExit,
                            appendError = state.appendError,
                            appending = state.appending,
                            onRetryAppend = onRetryAppend,
                            modifier = Modifier.align(Alignment.Center),
                        )
                    }
                }
            }
        }
    }
}

// ── Paged (paged / paged-rtl / double) ────────────────────────────────────

@Composable
private fun PagedReader(
    state: ReaderUiState,
    mode: ResolvedMode,
    seekTarget: Int?,
    onSeekHandled: () -> Unit,
    onPosition: (Int, Int) -> Unit,
    onImageLoaded: (Int, Int) -> Unit,
    onToggleChrome: () -> Unit,
    onNextChapter: () -> Unit,
    onPrevChapter: () -> Unit,
    onExit: () -> Unit,
) {
    val settings = state.settings
    val seg = state.segments.firstOrNull() ?: return
    val pageCount = seg.pageCount
    val doubled = mode == ResolvedMode.DOUBLE
    val contentSlots = if (doubled) (pageCount + 1) / 2 else pageCount
    val slotCount = contentSlots + if (settings.chapterTransition) 1 else 0
    val scope = rememberCoroutineScope()

    val pagerState = rememberPagerState(
        initialPage = if (doubled) state.resumePage / 2 else state.resumePage,
    ) { slotCount.coerceAtLeast(1) }

    // Report the page under the reader; the transition slot isn't a real page.
    LaunchedEffect(pagerState, contentSlots) {
        snapshotFlow { pagerState.currentPage }.collect { slot ->
            if (slot < contentSlots) {
                val page = if (doubled) (slot * 2 + 1).coerceAtMost(pageCount - 1) else slot
                onPosition(0, page)
            }
        }
    }
    LaunchedEffect(seekTarget) {
        val target = seekTarget ?: return@LaunchedEffect
        pagerState.scrollToPage((if (doubled) target / 2 else target).coerceIn(0, slotCount - 1))
        onSeekHandled()
    }

    // Preload upcoming pages (web: an invisible <img> per preload slot).
    val platformContext = LocalPlatformContext.current
    val imageLoader = SingletonImageLoader.get(platformContext)
    LaunchedEffect(pagerState.currentPage, settings.preload, seg.key) {
        val from = if (doubled) pagerState.currentPage * 2 + 1 else pagerState.currentPage
        for (i in 1..settings.preload) {
            val next = from + i
            if (next in 0 until pageCount) {
                val model = seg.pages.getOrNull(next) ?: continue
                imageLoader.enqueue(ImageRequest.Builder(platformContext).data(model).build())
            }
        }
    }

    /** The web's `advance`: page turns stay within the chapter; the ends step chapters. */
    fun advance(direction: Int) {
        val slot = pagerState.currentPage
        if (direction > 0) {
            if (slot >= slotCount - 1) {
                // On the last slot: the transition screen carries into the next
                // chapter, and without one the last page does it directly.
                onNextChapter()
                return
            }
            scope.launch { pagerState.animateScrollToPage(slot + 1) }
        } else {
            if (slot <= 0) return
            scope.launch { pagerState.animateScrollToPage(slot - 1) }
        }
    }

    CompositionLocalProvider(
        LocalLayoutDirection provides if (mode.rtl) LayoutDirection.Rtl else LayoutDirection.Ltr,
    ) {
        HorizontalPager(state = pagerState, modifier = Modifier.fillMaxSize()) { slot ->
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .pointerInput(settings.tapNavigation, mode, slotCount) {
                        detectTapGestures { offset ->
                            if (!settings.tapNavigation) {
                                onToggleChrome()
                                return@detectTapGestures
                            }
                            val x = offset.x / size.width.toFloat()
                            when {
                                x < 0.3f -> advance(if (mode.rtl) 1 else -1)
                                x > 0.7f -> advance(if (mode.rtl) -1 else 1)
                                else -> onToggleChrome()
                            }
                        }
                    },
                contentAlignment = Alignment.Center,
            ) {
                if (slot >= contentSlots) {
                    // Chapter transition — its own page.
                    CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
                        ChapterTransition(
                            finishedLabel = seg.name,
                            nextLabel = state.nextChapterName,
                            hasNext = state.hasNext,
                            hasPrev = state.hasPrev,
                            onNext = onNextChapter,
                            onPrevPage = {
                                scope.launch {
                                    pagerState.animateScrollToPage((contentSlots - 1).coerceAtLeast(0))
                                }
                            },
                            onPrevChapter = onPrevChapter,
                            onExit = onExit,
                        )
                    }
                } else if (doubled) {
                    // Desktop shows the pair side by side; on a phone it stacks.
                    Column(
                        modifier = Modifier.fillMaxSize(),
                        verticalArrangement = Arrangement.Center,
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        for (p in slot * 2..(slot * 2 + 1)) {
                            if (p >= pageCount) continue
                            Box(modifier = Modifier.fillMaxWidth().weight(1f)) {
                                PageImage(
                                    model = seg.pages.getOrNull(p),
                                    index = p,
                                    fit = settings.fit,
                                    onImageLoaded = onImageLoaded,
                                )
                            }
                        }
                    }
                } else {
                    PageImage(
                        model = seg.pages.getOrNull(slot),
                        index = slot,
                        fit = settings.fit,
                        onImageLoaded = onImageLoaded,
                    )
                }
            }
        }
    }
}

/** One page honoring the Page fit setting (fit width / fit height / original size). */
@Composable
private fun PageImage(
    model: Any?,
    index: Int,
    fit: FitMode,
    onImageLoaded: (Int, Int) -> Unit,
) {
    val onSuccess: (AsyncImagePainter.State.Success) -> Unit = { success ->
        val w = success.result.image.width
        val h = success.result.image.height
        if (w > 0 && h > 0) onImageLoaded(w, h)
    }
    when (fit) {
        FitMode.HEIGHT -> AsyncImage(
            model = model,
            contentDescription = "Page ${index + 1}",
            contentScale = ContentScale.Fit,
            onSuccess = onSuccess,
            modifier = Modifier.fillMaxSize(),
        )

        FitMode.WIDTH -> Box(
            modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
            contentAlignment = Alignment.Center,
        ) {
            AsyncImage(
                model = model,
                contentDescription = "Page ${index + 1}",
                contentScale = ContentScale.FillWidth,
                onSuccess = onSuccess,
                modifier = Modifier.fillMaxWidth(),
            )
        }

        FitMode.ORIGINAL -> Box(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .horizontalScroll(rememberScrollState()),
            contentAlignment = Alignment.Center,
        ) {
            // No size modifier: inside a two-way scroller the image lays out at
            // its intrinsic pixel size, which is what "Original size" means.
            AsyncImage(
                model = model,
                contentDescription = "Page ${index + 1}",
                contentScale = ContentScale.None,
                onSuccess = onSuccess,
            )
        }
    }
}
