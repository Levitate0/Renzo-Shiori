package app.renzoshiori.client.ui.reader

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.data.model.ReaderChapterDto
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvContentColor
import app.renzoshiori.client.ui.tv.tvFocusable
import kotlin.math.roundToInt

/**
 * Tailwind hexes the web reader's chrome uses literally. Kept together so the
 * sheets, chrome and overlays all read from one place.
 */
internal object ReaderPalette {
    val Sheet = Color(0xFF18181B)        // bg-zinc-900
    val Field = Color(0xFF27272A)        // bg-zinc-800
    val Hairline = Color(0x1AFFFFFF)     // border-white/10
    val Chrome = Color(0xB3000000)       // bg-black/70
    val Text = Color(0xFFFFFFFF)
    val Text85 = Color(0xD9FFFFFF)
    val Text80 = Color(0xCCFFFFFF)
    val Text70 = Color(0xB3FFFFFF)
    val Text60 = Color(0x99FFFFFF)
    val Text50 = Color(0x80FFFFFF)
    val Text40 = Color(0x66FFFFFF)
    val Text35 = Color(0x59FFFFFF)
    val Pink400 = Color(0xFFF472B6)
    val Pink500 = Color(0xFFEC4899)
    val Violet400 = Color(0xFFA78BFA)
    val Emerald400 = Color(0xFF34D399)
}

// ── Shared controls (web: Label / Select / Slider / Switch) ────────────────

@Composable
private fun SettingsLabel(text: String) {
    Text(
        text,
        fontSize = 14.sp,
        fontWeight = FontWeight.Medium,
        color = ReaderPalette.Text,
        modifier = Modifier.padding(bottom = 6.dp),
    )
}

@Composable
private fun SettingsHelp(text: String) {
    Text(
        text,
        fontSize = 11.sp,
        lineHeight = 15.sp,
        color = ReaderPalette.Text50,
        modifier = Modifier.padding(top = 6.dp),
    )
}

/** The web's SelectTrigger (bg-zinc-800, border-white/10) + SelectContent menu. */
@Composable
private fun SettingsSelect(
    options: List<Pair<String, String>>,
    value: String,
    onChange: (String) -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    val selected = options.firstOrNull { it.first == value } ?: options.firstOrNull()

    Box(modifier = Modifier.fillMaxWidth()) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .fillMaxWidth()
                .height(36.dp)
                .clip(RoundedCornerShape(8.dp))
                .border(1.dp, ReaderPalette.Hairline, RoundedCornerShape(8.dp))
                .background(ReaderPalette.Field)
                .clickable { open = true }
                .padding(horizontal = 12.dp),
        ) {
            Text(
                selected?.second ?: "",
                fontSize = 14.sp,
                color = ReaderPalette.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Icon(
                Icons.Filled.ExpandMore,
                contentDescription = null,
                tint = ReaderPalette.Text50,
                modifier = Modifier.size(16.dp),
            )
        }
        DropdownMenu(
            expanded = open,
            onDismissRequest = { open = false },
            containerColor = RenzoColors.Popover,
        ) {
            options.forEach { (optValue, optLabel) ->
                DropdownMenuItem(
                    text = {
                        Text(
                            optLabel,
                            fontSize = 14.sp,
                            color = if (optValue == value) RenzoColors.Primary else ReaderPalette.Text,
                        )
                    },
                    onClick = {
                        onChange(optValue)
                        open = false
                    },
                )
            }
        }
    }
}

/** Web pattern: a Label whose text carries the live value, then a stepped Slider. */
@Composable
private fun SettingsSlider(
    label: String,
    value: Int,
    min: Int,
    max: Int,
    step: Int,
    help: String? = null,
    onChange: (Int) -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp)) {
        SettingsLabel(label)
        Slider(
            value = value.toFloat(),
            onValueChange = { raw ->
                val snapped = min + (((raw - min) / step).roundToInt() * step)
                onChange(snapped.coerceIn(min, max))
            },
            valueRange = min.toFloat()..max.toFloat(),
            steps = (((max - min) / step) - 1).coerceAtLeast(0),
            colors = SliderDefaults.colors(
                thumbColor = RenzoColors.Primary,
                activeTrackColor = RenzoColors.Primary,
                activeTickColor = Color.Transparent,
                inactiveTrackColor = ReaderPalette.Field,
                inactiveTickColor = Color.Transparent,
            ),
            modifier = Modifier.fillMaxWidth(),
        )
        if (help != null) SettingsHelp(help)
    }
}

/** The web's ToggleRow: label on the left, Switch on the right. */
@Composable
private fun ToggleRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp),
    ) {
        Text(
            label,
            fontSize = 14.sp,
            lineHeight = 18.sp,
            color = ReaderPalette.Text,
            modifier = Modifier.weight(1f).padding(end = 12.dp),
        )
        Switch(
            checked = checked,
            onCheckedChange = onChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = RenzoColors.PrimaryForeground,
                checkedTrackColor = RenzoColors.Primary,
                checkedBorderColor = RenzoColors.Primary,
                uncheckedThumbColor = ReaderPalette.Text60,
                uncheckedTrackColor = ReaderPalette.Field,
                uncheckedBorderColor = ReaderPalette.Hairline,
            ),
        )
    }
}

@Composable
private fun SheetHeader(title: String, onClose: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, bottom = 8.dp),
    ) {
        Text(
            title,
            fontSize = 15.sp,
            fontWeight = FontWeight.SemiBold,
            color = ReaderPalette.Text,
            modifier = Modifier.weight(1f),
        )
        IconButton(onClick = onClose) {
            Icon(Icons.Filled.Close, contentDescription = "Close", tint = ReaderPalette.Text70, modifier = Modifier.size(18.dp))
        }
    }
}

// ── Reader Settings ───────────────────────────────────────────────────────

/**
 * The web reader's settings panel, option for option and label for label. The
 * web renders it as a right-hand drawer; on a phone that becomes a bottom sheet
 * (the "side-by-side becomes vertical" rule), with identical content and order.
 *
 * On a television the bottom sheet is replaced by [ReaderTvPanel] — a sheet does
 * not contain D-pad focus — and the controls are re-ordered and swapped for
 * focusable equivalents. The panel is the same content either way; only the
 * container and the control widgets differ. See [ReaderSettingsBody].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ReaderSettingsSheet(
    state: ReaderUiState,
    mode: ResolvedMode,
    isTv: Boolean,
    onDismiss: () -> Unit,
    onModeChange: (ReaderMode) -> Unit,
    onSettingsChange: ((ReaderSettings) -> ReaderSettings) -> Unit,
    onClearCache: () -> Unit,
    onToggleChapterRead: () -> Unit,
) {
    if (isTv) {
        ReaderTvPanel(title = "Reader Settings", onDismiss = onDismiss) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState()),
            ) {
                ReaderSettingsBody(
                    state = state,
                    mode = mode,
                    isTv = true,
                    onModeChange = onModeChange,
                    onSettingsChange = onSettingsChange,
                    onClearCache = onClearCache,
                    onToggleChapterRead = onToggleChapterRead,
                )
            }
        }
        return
    }

    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = ReaderPalette.Sheet,
        contentColor = ReaderPalette.Text,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .navigationBarsPadding()
                .padding(bottom = 24.dp),
        ) {
            SheetHeader("Reader Settings", onDismiss)
            Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
                ReaderSettingsBody(
                    state = state,
                    mode = mode,
                    isTv = false,
                    onModeChange = onModeChange,
                    onSettingsChange = onSettingsChange,
                    onClearCache = onClearCache,
                    onToggleChapterRead = onToggleChapterRead,
                )
            }
        }
    }
}

/**
 * Every reader setting, once.
 *
 * Two things change on a television and nothing else does:
 *
 *  - **Order.** Sizing comes first, because viewing distance is the only
 *    problem a TV reader really has: scale, fit, page width, then mode,
 *    background and the scroll step.
 *  - **Widgets.** Dropdowns become inline option lists (a menu is a second
 *    focus container, and it hides the current value while it is open) and
 *    sliders become [TvStepper]s (drag is the only gesture `Slider` knows).
 *
 * Two rows are hidden rather than disabled: "tap zones" describes touch zones
 * that do not exist on a remote, and "clear cache on exit" is a phone-storage
 * concern. A disabled row invites the user to keep pressing it.
 */
@Composable
private fun ReaderSettingsBody(
    state: ReaderUiState,
    mode: ResolvedMode,
    isTv: Boolean,
    onModeChange: (ReaderMode) -> Unit,
    onSettingsChange: ((ReaderSettings) -> ReaderSettings) -> Unit,
    onClearCache: () -> Unit,
    onToggleChapterRead: () -> Unit,
) {
    val settings = state.settings
    val continuous = mode.continuous
    val activeChapter = state.activeChapter
    val autoSuffix =
        if (settings.mode == ReaderMode.AUTO && state.seriesModeOverride == null)
            " (auto → ${modeSlug(mode)})" else ""
    val modeHelp = "Choice is remembered per series; “Auto” follows the chapter’s detected layout."
    val scaleHelp =
        "Makes the page itself bigger — the one thing fit and page width can't do. " +
            "Above 100% the page is larger than the screen and pans as you read."

    // The first control on TV takes focus when the panel opens, so the user
    // never has to hunt for the cursor.
    val firstFocus = remember { FocusRequester() }
    LaunchedEffect(isTv) {
        if (!isTv) return@LaunchedEffect
        // Not before the first frame: the row has to be attached to take focus.
        withFrameNanos { }
        runCatching { firstFocus.requestFocus() }
    }

    val scaleLabel = "Page scale — ${settings.scalePct}%"
    val widthLabel = "Page width — ${settings.maxWidthPct}% of screen"
    val widthHelp = "Auto-resize: every page is scaled to this same width, so mixed-size pages line up."
    val stepLabel =
        if (isTv) "Scroll step — ${settings.tapAdvancePct}% of screen"
        else "Click-to-scroll step — ${settings.tapAdvancePct}% of screen"
    val stepHelp =
        if (isTv) "How far one press of up or down moves the page."
        else "Tap the right side to scroll forward by this much; tap the left side to scroll back."

    val modeOptions = ReaderMode.values().map { it.value to it.label }
    val fitOptions = FitMode.values().map { it.value to it.label }
    val backgroundOptions = ReaderBackground.values().map { it.value to it.label }
    val modeValue = (state.seriesModeOverride ?: settings.mode).value

    if (isTv) {
        // Sizing first — it is the whole reason a TV needs this panel.
        SettingsNumber(
            isTv, scaleLabel, settings.scalePct,
            ReaderPrefs.SCALE_MIN, ReaderPrefs.SCALE_MAX, ReaderPrefs.SCALE_STEP,
            scaleHelp, Modifier.focusRequester(firstFocus),
        ) { v -> onSettingsChange { it.copy(scalePct = v) } }
        if (!continuous) {
            SettingsOptions(isTv, "Page fit", fitOptions, settings.fit.value) { v ->
                onSettingsChange { it.copy(fit = FitMode.from(v)) }
            }
        }
        if (continuous) {
            SettingsNumber(isTv, widthLabel, settings.maxWidthPct, 20, 100, 5, widthHelp) { v ->
                onSettingsChange { it.copy(maxWidthPct = v) }
            }
        }
        SettingsOptions(isTv, "Reading mode$autoSuffix", modeOptions, modeValue, modeHelp) { v ->
            onModeChange(ReaderMode.from(v))
        }
        SettingsOptions(isTv, "Background", backgroundOptions, settings.background.value) { v ->
            onSettingsChange { it.copy(background = ReaderBackground.from(v)) }
        }
        if (continuous) {
            SettingsNumber(isTv, stepLabel, settings.tapAdvancePct, 20, 100, 10, stepHelp) { v ->
                onSettingsChange { it.copy(tapAdvancePct = v) }
            }
        }
    } else {
        SettingsOptions(isTv, "Reading mode$autoSuffix", modeOptions, modeValue, modeHelp) { v ->
            onModeChange(ReaderMode.from(v))
        }
        if (!continuous) {
            SettingsOptions(isTv, "Page fit", fitOptions, settings.fit.value) { v ->
                onSettingsChange { it.copy(fit = FitMode.from(v)) }
            }
        }
        SettingsNumber(
            isTv, scaleLabel, settings.scalePct,
            ReaderPrefs.SCALE_MIN, ReaderPrefs.SCALE_MAX, ReaderPrefs.SCALE_STEP, scaleHelp,
        ) { v -> onSettingsChange { it.copy(scalePct = v) } }
        if (continuous) {
            SettingsNumber(isTv, widthLabel, settings.maxWidthPct, 20, 100, 5, widthHelp) { v ->
                onSettingsChange { it.copy(maxWidthPct = v) }
            }
            SettingsNumber(isTv, stepLabel, settings.tapAdvancePct, 20, 100, 10, stepHelp) { v ->
                onSettingsChange { it.copy(tapAdvancePct = v) }
            }
        }
    }

    // Gap (vertical only)
    if (mode == ResolvedMode.VERTICAL) {
        SettingsNumber(isTv, "Gap between pages — ${settings.gapPx}px", settings.gapPx, 0, 48, 4) { v ->
            onSettingsChange { it.copy(gapPx = v) }
        }
    }
    if (!isTv) {
        SettingsOptions(isTv, "Background", backgroundOptions, settings.background.value) { v ->
            onSettingsChange { it.copy(background = ReaderBackground.from(v)) }
        }
    }

    // Preload (paged modes only)
    if (!continuous) {
        SettingsNumber(isTv, "Preload pages — ${settings.preload}", settings.preload, 0, 10, 1) { v ->
            onSettingsChange { it.copy(preload = v) }
        }
    }

    // Tap zones describe something a remote does not have.
    if (!isTv) {
        SettingsToggle(
            isTv = isTv,
            label = "Tap zones (sides turn pages · tap to scroll)",
            checked = settings.tapNavigation,
            onChange = { v -> onSettingsChange { it.copy(tapNavigation = v) } },
        )
    }
    if (continuous) {
        SettingsToggle(
            isTv = isTv,
            label = "Infinite scroll (roll into next chapter)",
            checked = settings.infiniteScroll,
            onChange = { v -> onSettingsChange { it.copy(infiniteScroll = v) } },
        )
    }
    SettingsToggle(
        isTv = isTv,
        label = "Show page number",
        checked = settings.showPageNumber,
        onChange = { v -> onSettingsChange { it.copy(showPageNumber = v) } },
    )
    if (!continuous) {
        SettingsToggle(
            isTv = isTv,
            label = "Chapter transition screen (finished · up next)",
            checked = settings.chapterTransition,
            onChange = { v -> onSettingsChange { it.copy(chapterTransition = v) } },
        )
    }
    SettingsToggle(
        isTv = isTv,
        label = "Mark read on last page",
        checked = settings.autoMarkRead,
        onChange = { v -> onSettingsChange { it.copy(autoMarkRead = v) } },
    )

    // ── Cache ──
    HorizontalDivider(color = ReaderPalette.Hairline, modifier = Modifier.padding(vertical = 8.dp))
    SettingsLabel("Streamed image cache")
    Text(
        "Pages read live from a source (not downloaded) are cached in memory for smooth " +
            "scrolling. Clear it if a source served a stale or broken image.",
        fontSize = 11.sp,
        lineHeight = 15.sp,
        color = ReaderPalette.Text50,
        modifier = Modifier.padding(bottom = 10.dp),
    )
    // Auto-clear-on-exit is about phone storage; it is noise on a TV.
    if (!isTv) {
        SettingsToggle(
            isTv = isTv,
            label = "Clear the cache when I exit the reader",
            checked = settings.autoClearCache,
            onChange = { v -> onSettingsChange { it.copy(autoClearCache = v) } },
        )
    }
    if (isTv) {
        TvActionRow("Clear reader cache (web pages)", busy = state.cacheBusy, onClick = onClearCache)
    } else {
        Button(
            onClick = onClearCache,
            shape = MaterialTheme.shapes.small,
            colors = ButtonDefaults.buttonColors(
                containerColor = RenzoColors.Secondary,
                contentColor = ReaderPalette.Text,
            ),
            modifier = Modifier.fillMaxWidth(),
        ) {
            if (state.cacheBusy) {
                CircularProgressIndicator(
                    color = ReaderPalette.Text,
                    strokeWidth = 2.dp,
                    modifier = Modifier.size(16.dp),
                )
            } else {
                Icon(Icons.Filled.Delete, contentDescription = null, modifier = Modifier.size(16.dp))
            }
            Spacer(Modifier.width(8.dp))
            Text("Clear reader cache (web pages)", fontSize = 14.sp)
        }
    }

    // ── Read state ──
    if (activeChapter != null) {
        HorizontalDivider(color = ReaderPalette.Hairline, modifier = Modifier.padding(vertical = 12.dp))
        val readLabel = if (activeChapter.isCompleted) "Mark chapter unread" else "Mark chapter read"
        if (isTv) {
            TvActionRow(readLabel, onClick = onToggleChapterRead)
        } else {
            Button(
                onClick = onToggleChapterRead,
                shape = MaterialTheme.shapes.small,
                colors = ButtonDefaults.buttonColors(
                    containerColor = RenzoColors.Secondary,
                    contentColor = ReaderPalette.Text,
                ),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(readLabel, fontSize = 14.sp)
            }
        }
    }
    // Room to scroll the last row clear of the panel edge.
    Spacer(Modifier.height(24.dp))
}

// ── One control, two input models ─────────────────────────────────────────
//
// These three are the whole of the touch/TV divergence in the settings panel.
// Keeping the branch inside them is what stops the panel itself from forking:
// the option set, the labels and the wiring are written once.

/** Stepped number: a drag slider on touch, a focusable ±step row on a remote. */
@Composable
private fun SettingsNumber(
    isTv: Boolean,
    label: String,
    value: Int,
    min: Int,
    max: Int,
    step: Int,
    help: String? = null,
    modifier: Modifier = Modifier,
    onChange: (Int) -> Unit,
) {
    if (isTv) {
        TvStepper(
            label = label, value = value, min = min, max = max, step = step,
            help = help, modifier = modifier, onChange = onChange,
        )
    } else {
        SettingsSlider(label = label, value = value, min = min, max = max, step = step, help = help, onChange = onChange)
    }
}

/** Pick-one: a dropdown on touch, an always-visible option list on a remote. */
@Composable
private fun SettingsOptions(
    isTv: Boolean,
    label: String,
    options: List<Pair<String, String>>,
    value: String,
    help: String? = null,
    onChange: (String) -> Unit,
) {
    if (isTv) {
        TvOptionGroup(label = label, options = options, value = value, help = help, onChange = onChange)
    } else {
        Column(modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp)) {
            SettingsLabel(label)
            SettingsSelect(options = options, value = value, onChange = onChange)
            if (help != null) SettingsHelp(help)
        }
    }
}

/** On/off: the row is the focus target on TV, so the switch itself stays inert. */
@Composable
private fun SettingsToggle(isTv: Boolean, label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    if (isTv) TvToggleRow(label, checked, onChange) else ToggleRow(label, checked, onChange)
}

/** "auto → webtoon" etc. — the web prints the resolved mode's slug. */
private fun modeSlug(mode: ResolvedMode): String = when (mode) {
    ResolvedMode.PAGED -> "paged"
    ResolvedMode.PAGED_RTL -> "paged-rtl"
    ResolvedMode.DOUBLE -> "double"
    ResolvedMode.WEBTOON -> "webtoon"
    ResolvedMode.LONGSTRIP -> "longstrip"
    ResolvedMode.VERTICAL -> "vertical"
}

// ── Chapter list ──────────────────────────────────────────────────────────

/**
 * The web's ChapterListDrawer: filter box, newest first, read-state marks,
 * lock / "web" (not downloaded) / bookmark badges and a "reading" marker.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ReaderChapterListSheet(
    state: ReaderUiState,
    isTv: Boolean,
    onDismiss: () -> Unit,
    onPick: (Double) -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var query by remember { mutableStateOf("") }
    val listState = rememberLazyListState()

    val q = query.trim().lowercase()
    val rows = remember(state.chapters, q) {
        val sorted = state.chapters.sortedByDescending { it.number }
        if (q.isEmpty()) sorted
        else sorted.filter { trimNumber(it.number).contains(q) || it.name.lowercase().contains(q) }
    }
    val currentNumber = state.activeChapterNumber

    // Jump to the chapter being read when the drawer opens.
    LaunchedEffect(rows.size) {
        val idx = rows.indexOfFirst { it.number == currentNumber }
        if (idx > 0) listState.scrollToItem(idx)
    }

    if (isTv) {
        // The filter box is deliberately absent: it is the one text field in the
        // reader, and typing a chapter number on a leanback IME is slower than
        // scrolling to it. The list opens on the chapter being read instead.
        val activeFocus = remember { FocusRequester() }
        LaunchedEffect(rows.size) {
            // The row has to exist before it can take focus, and it only exists
            // after the jump above has laid it out.
            withFrameNanos { }
            runCatching { activeFocus.requestFocus() }
        }
        ReaderTvPanel(title = "Chapters", onDismiss = onDismiss) {
            if (rows.isEmpty()) {
                Text(
                    "No chapters yet.",
                    fontSize = 13.sp,
                    color = ReaderPalette.Text40,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 32.dp),
                )
            } else {
                LazyColumn(
                    state = listState,
                    contentPadding = PaddingValues(vertical = 4.dp),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    items(rows, key = { it.number }) { chapter ->
                        val active = chapter.number == currentNumber
                        TvChapterRow(
                            chapter = chapter,
                            active = active,
                            modifier = if (active) Modifier.focusRequester(activeFocus) else Modifier,
                            onClick = { onPick(chapter.number) },
                        )
                    }
                }
            }
        }
        return
    }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = ReaderPalette.Sheet,
        contentColor = ReaderPalette.Text,
    ) {
        Column(modifier = Modifier.fillMaxWidth().navigationBarsPadding()) {
            SheetHeader("Chapters", onDismiss)
            HorizontalDivider(color = ReaderPalette.Hairline)

            // Filter box
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp, vertical = 8.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .border(1.dp, ReaderPalette.Hairline, RoundedCornerShape(6.dp))
                    .background(Color(0x0DFFFFFF))
                    .padding(horizontal = 10.dp, vertical = 8.dp),
            ) {
                Icon(
                    Icons.Filled.Search,
                    contentDescription = null,
                    tint = ReaderPalette.Text40,
                    modifier = Modifier.size(14.dp),
                )
                Spacer(Modifier.width(8.dp))
                Box(modifier = Modifier.weight(1f)) {
                    if (query.isEmpty()) {
                        Text("Filter by number or title…", fontSize = 14.sp, color = Color(0x59FFFFFF))
                    }
                    BasicTextField(
                        value = query,
                        onValueChange = { query = it },
                        singleLine = true,
                        textStyle = MaterialTheme.typography.bodyMedium.copy(color = ReaderPalette.Text),
                        cursorBrush = SolidColor(RenzoColors.Primary),
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
            HorizontalDivider(color = ReaderPalette.Hairline)

            if (rows.isEmpty()) {
                Text(
                    "No chapters match.",
                    fontSize = 12.sp,
                    color = ReaderPalette.Text40,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 32.dp),
                )
            } else {
                LazyColumn(
                    state = listState,
                    contentPadding = PaddingValues(vertical = 4.dp),
                    modifier = Modifier.fillMaxWidth().heightIn(max = 440.dp),
                ) {
                    items(rows, key = { it.number }) { chapter ->
                        ChapterRow(
                            chapter = chapter,
                            active = chapter.number == currentNumber,
                            onClick = { onPick(chapter.number) },
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun ChapterRow(chapter: ReaderChapterDto, active: Boolean, onClick: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier
            .fillMaxWidth()
            .background(if (active) Color(0x14FFFFFF) else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 10.dp),
    ) {
        // Read-state mark (check / percent / nothing)
        Box(modifier = Modifier.width(20.dp), contentAlignment = Alignment.CenterStart) {
            when {
                chapter.isCompleted -> Icon(
                    Icons.Filled.Check,
                    contentDescription = "Read",
                    tint = ReaderPalette.Emerald400,
                    modifier = Modifier.size(14.dp),
                )
                chapter.progress > 0f -> Text(
                    "${(chapter.progress * 100).roundToInt()}%",
                    fontSize = 10.sp,
                    color = RenzoColors.Primary,
                )
            }
        }
        Text(
            chapter.name.ifBlank { "Chapter ${trimNumber(chapter.number)}" },
            fontSize = 14.sp,
            fontWeight = if (active) FontWeight.Medium else FontWeight.Normal,
            color = if (chapter.isCompleted) ReaderPalette.Text50 else ReaderPalette.Text,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        if (chapter.locked) {
            Icon(
                Icons.Filled.Lock,
                contentDescription = "Locked",
                tint = ReaderPalette.Violet400,
                modifier = Modifier.size(12.dp),
            )
        } else if (chapter.filename == null) {
            // Not downloaded — streams live from the source.
            Text("WEB", fontSize = 10.sp, color = Color(0x4DFFFFFF))
        }
        if (chapter.bookmarked) {
            Icon(
                Icons.Filled.Bookmark,
                contentDescription = "Bookmarked",
                tint = ReaderPalette.Pink500,
                modifier = Modifier.size(12.dp),
            )
        }
        if (active) Text("reading", fontSize = 11.sp, color = RenzoColors.Primary)
    }
}

/**
 * The same row for a remote.
 *
 * Focused and selected are different things and both have to survive being read
 * from across a room: the ring and the fill say where the cursor is, the accent
 * colour and the "reading" tag say which chapter is actually open. Move the
 * cursor off the current chapter and it is still obvious which one that was.
 */
@Composable
private fun TvChapterRow(
    chapter: ReaderChapterDto,
    active: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val focus = rememberFocusState()
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        modifier = modifier
            .fillMaxWidth()
            .tvFocusable(
                focused = focus.focused,
                onFocused = focus::set,
                radius = 10.dp,
                onClick = onClick,
            )
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        Box(modifier = Modifier.width(22.dp), contentAlignment = Alignment.CenterStart) {
            when {
                chapter.isCompleted -> Icon(
                    Icons.Filled.Check,
                    contentDescription = "Read",
                    tint = ReaderPalette.Emerald400,
                    modifier = Modifier.size(16.dp),
                )
                chapter.progress > 0f -> Text(
                    "${(chapter.progress * 100).roundToInt()}%",
                    fontSize = 11.sp,
                    color = RenzoColors.Primary,
                )
            }
        }
        Text(
            chapter.name.ifBlank { "Chapter ${trimNumber(chapter.number)}" },
            fontSize = 15.sp,
            fontWeight = if (active) FontWeight.Medium else FontWeight.Normal,
            color = tvContentColor(selected = active, focused = focus.focused),
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        if (chapter.locked) {
            Icon(
                Icons.Filled.Lock,
                contentDescription = "Locked",
                tint = ReaderPalette.Violet400,
                modifier = Modifier.size(14.dp),
            )
        } else if (chapter.filename == null) {
            Text("WEB", fontSize = 11.sp, color = Color(0x4DFFFFFF))
        }
        if (chapter.bookmarked) {
            Icon(
                Icons.Filled.Bookmark,
                contentDescription = "Bookmarked",
                tint = ReaderPalette.Pink500,
                modifier = Modifier.size(14.dp),
            )
        }
        if (active) Text("reading", fontSize = 12.sp, color = RenzoColors.Primary)
    }
}
