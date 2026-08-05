package app.renzoshiori.client.ui.reader

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.TvOptionRow
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable

/**
 * The reader's D-pad half.
 *
 * A television has no pointer, which takes out both of the reader's primary
 * interactions at once: the pager is driven by drag and the continuous strip by
 * tap zones. Everything here exists to give those a keyboard equivalent, and to
 * make the settings panel — the one control surface a TV genuinely needs, since
 * couch distance is a sizing problem — operable from four arrows and OK.
 *
 * Nothing in this file is TV-only by construction; it is simply never reached on
 * a touch device, so the touch paths stay exactly as they were.
 */

/** Which way the D-pad went. */
enum class DpadDir { LEFT, RIGHT, UP, DOWN }

/**
 * The reading surface publishes its D-pad semantics here and the reader root —
 * which owns the focusable key sink — calls into it.
 *
 * The alternative (hoisting the pager/list state up to [ReaderScreen]) would
 * mean the two renderers no longer own their own scrolling, which is a much
 * larger change to code that touch depends on.
 */
class ReaderNavHandle {
    var onDpad: ((DpadDir) -> Unit)? = null
}

@Composable
fun rememberReaderNavHandle(): ReaderNavHandle = remember { ReaderNavHandle() }

private fun dpadDirOf(key: Key): DpadDir? = when (key) {
    Key.DirectionLeft, Key.SystemNavigationLeft -> DpadDir.LEFT
    Key.DirectionRight, Key.SystemNavigationRight -> DpadDir.RIGHT
    Key.DirectionUp, Key.SystemNavigationUp, Key.PageUp, Key.ChannelUp -> DpadDir.UP
    Key.DirectionDown, Key.SystemNavigationDown, Key.PageDown, Key.ChannelDown -> DpadDir.DOWN
    else -> null
}

private fun isCenterKey(key: Key): Boolean = when (key) {
    Key.DirectionCenter, Key.Enter, Key.NumPadEnter, Key.Spacebar -> true
    else -> false
}

/**
 * D-pad handling for whichever node below this one holds focus.
 *
 * Key-up is swallowed for every key whose key-down we claim: leaving the up half
 * to propagate makes the platform re-dispatch it as a fallback, which on some
 * launchers moves focus a second time.
 */
fun Modifier.dpadKeys(
    enabled: Boolean = true,
    onCenter: () -> Boolean = { false },
    onDir: (DpadDir) -> Boolean,
): Modifier = if (!enabled) this else this.onKeyEvent { event ->
    val dir = dpadDirOf(event.key)
    val center = isCenterKey(event.key)
    when {
        dir == null && !center -> false
        event.type != KeyEventType.KeyDown -> true
        dir != null -> onDir(dir)
        else -> onCenter()
    }
}

// ── Panels ────────────────────────────────────────────────────────────────

/**
 * The TV stand-in for a `ModalBottomSheet`.
 *
 * A bottom sheet does not contain focus on a television — the D-pad walks
 * straight out of it into whatever is behind, and the sheet's own drag-to-
 * dismiss has no remote equivalent. A [Dialog] gets its own window, so focus is
 * trapped for free and Back is routed to [onDismiss] by the platform.
 *
 * Laid out as a right-hand drawer, which is what the web reader does at desktop
 * width — a television is a wide screen, not a tall one.
 */
@Composable
fun ReaderTvPanel(
    title: String,
    onDismiss: () -> Unit,
    content: @Composable ColumnScope.() -> Unit,
) {
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Box(
            modifier = Modifier.fillMaxSize().background(Color(0xB3000000)),
            contentAlignment = Alignment.CenterEnd,
        ) {
            Column(
                modifier = Modifier
                    // Overscan: TVs crop the outer few percent of the panel.
                    .padding(vertical = 32.dp, horizontal = 40.dp)
                    .fillMaxHeight()
                    .fillMaxWidth(0.46f)
                    .clip(RoundedCornerShape(16.dp))
                    .background(ReaderPalette.Sheet)
                    .padding(horizontal = 18.dp, vertical = 16.dp),
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().padding(bottom = 10.dp),
                ) {
                    Text(
                        title,
                        fontSize = 18.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = ReaderPalette.Text,
                        modifier = Modifier.weight(1f),
                    )
                    Text("BACK to close", fontSize = 11.sp, color = ReaderPalette.Text40)
                }
                content()
            }
        }
    }
}

// ── Controls ──────────────────────────────────────────────────────────────

/**
 * A slider a remote can actually move.
 *
 * Drag is the only gesture Material's `Slider` understands, which makes it dead
 * weight on a TV. This takes focus as one row, adjusts by exactly [step] on
 * left/right, and deliberately does NOT consume up/down — that is the only way
 * focus can leave, and a control focus cannot leave is a trap.
 *
 * [label] is expected to carry the live value ("Page scale — 130%"): at three
 * metres the label is far more readable than the thumb position, and it is the
 * only feedback a user without a pointer gets.
 */
@Composable
fun TvStepper(
    label: String,
    value: Int,
    min: Int,
    max: Int,
    step: Int,
    help: String? = null,
    modifier: Modifier = Modifier,
    onChange: (Int) -> Unit,
) {
    val focus = rememberFocusState()
    val fraction = if (max > min) ((value - min).toFloat() / (max - min)).coerceIn(0f, 1f) else 0f
    val chevron = if (focus.focused) RenzoColors.Primary else ReaderPalette.Text35

    Column(
        modifier = modifier
            .fillMaxWidth()
            .focusRing(focus.focused, 10.dp)
            .background(
                if (focus.focused) RenzoColors.Card else Color.Transparent,
                RoundedCornerShape(10.dp),
            )
            .dpadKeys(
                onDir = { dir ->
                    when (dir) {
                        DpadDir.LEFT -> {
                            onChange((value - step).coerceIn(min, max)); true
                        }
                        DpadDir.RIGHT -> {
                            onChange((value + step).coerceIn(min, max)); true
                        }
                        // Up/down belong to focus traversal, not to the value.
                        else -> false
                    }
                },
            )
            .tvClickable(onFocused = focus::set) { }
            .padding(horizontal = 14.dp, vertical = 10.dp),
    ) {
        Text(
            label,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            color = if (focus.focused) ReaderPalette.Text else ReaderPalette.Text80,
        )
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
        ) {
            Icon(
                Icons.Filled.ChevronLeft,
                contentDescription = null,
                tint = if (value > min) chevron else ReaderPalette.Text35.copy(alpha = 0.2f),
                modifier = Modifier.size(18.dp),
            )
            Box(
                modifier = Modifier
                    .weight(1f)
                    .padding(horizontal = 8.dp)
                    .height(6.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(ReaderPalette.Field),
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxHeight()
                        .fillMaxWidth(fraction)
                        .background(RenzoColors.Primary),
                )
            }
            Icon(
                Icons.Filled.ChevronRight,
                contentDescription = null,
                tint = if (value < max) chevron else ReaderPalette.Text35.copy(alpha = 0.2f),
                modifier = Modifier.size(18.dp),
            )
        }
        if (help != null) {
            Text(
                help,
                fontSize = 11.sp,
                lineHeight = 15.sp,
                color = ReaderPalette.Text50,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
    }
}

/** Section label above a group of [TvOptionRow]s. */
@Composable
fun TvSectionLabel(text: String) {
    Text(
        text,
        fontSize = 12.sp,
        letterSpacing = 1.2.sp,
        fontWeight = FontWeight.Medium,
        color = ReaderPalette.Text50,
        modifier = Modifier.padding(start = 14.dp, top = 10.dp, bottom = 4.dp),
    )
}

/**
 * A labelled list of selection rows. Every option is on screen at once instead of
 * behind a dropdown: a `DropdownMenu` is a second focus container over the
 * first, and the selected value stops being visible the moment it opens.
 */
@Composable
fun TvOptionGroup(
    label: String,
    options: List<Pair<String, String>>,
    value: String,
    help: String? = null,
    onChange: (String) -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp)) {
        TvSectionLabel(label)
        options.forEach { (optionValue, optionLabel) ->
            TvOptionRow(
                label = optionLabel,
                selected = optionValue == value,
                onClick = { onChange(optionValue) },
            )
        }
        if (help != null) {
            Text(
                help,
                fontSize = 11.sp,
                lineHeight = 15.sp,
                color = ReaderPalette.Text50,
                modifier = Modifier.padding(start = 14.dp, top = 4.dp),
            )
        }
    }
}

/**
 * A switch row. The switch itself is inert (`onCheckedChange = null`) so it
 * cannot take focus of its own — the row is the focus target, and OK flips it.
 */
@Composable
fun TvToggleRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    TvOptionRow(
        label = label,
        selected = checked,
        trailing = {
            Switch(
                checked = checked,
                onCheckedChange = null,
                colors = SwitchDefaults.colors(
                    checkedThumbColor = RenzoColors.PrimaryForeground,
                    checkedTrackColor = RenzoColors.Primary,
                    checkedBorderColor = RenzoColors.Primary,
                    uncheckedThumbColor = ReaderPalette.Text60,
                    uncheckedTrackColor = ReaderPalette.Field,
                    uncheckedBorderColor = ReaderPalette.Hairline,
                ),
            )
        },
        onClick = { onChange(!checked) },
    )
}

/** A button-shaped focusable row (clear cache, mark read…). */
@Composable
fun TvActionRow(label: String, busy: Boolean = false, onClick: () -> Unit) {
    val focus = rememberFocusState()
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(if (focus.focused) RenzoColors.Card else RenzoColors.Secondary)
            .focusRing(focus.focused, 10.dp)
            .tvClickable(onFocused = focus::set, onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        if (busy) {
            CircularProgressIndicator(
                color = ReaderPalette.Text,
                strokeWidth = 2.dp,
                modifier = Modifier.size(16.dp),
            )
            Spacer(Modifier.width(8.dp))
        }
        Text(
            label,
            fontSize = 14.sp,
            color = if (focus.focused) ReaderPalette.Text else ReaderPalette.Text80,
        )
    }
}

/**
 * The bottom-chrome page scrubber, as a focusable stepper.
 *
 * Material's `Slider` is here on touch; on a remote it would be a focus stop
 * that cannot be moved, which is worse than no scrubber at all.
 */
@Composable
fun ReaderTvScrubber(
    page: Int,
    pageCount: Int,
    showPageNumber: Boolean,
    modifier: Modifier = Modifier,
    onSeek: (Int) -> Unit,
) {
    val focus = rememberFocusState()
    val last = (pageCount - 1).coerceAtLeast(0)
    val fraction = if (last > 0) (page.toFloat() / last).coerceIn(0f, 1f) else 0f
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .fillMaxWidth()
            .focusRing(focus.focused, 8.dp)
            .background(
                if (focus.focused) Color(0x33FFFFFF) else Color.Transparent,
                RoundedCornerShape(8.dp),
            )
            .dpadKeys(
                onDir = { dir ->
                    when (dir) {
                        DpadDir.LEFT -> {
                            onSeek((page - 1).coerceIn(0, last)); true
                        }
                        DpadDir.RIGHT -> {
                            onSeek((page + 1).coerceIn(0, last)); true
                        }
                        else -> false
                    }
                },
            )
            .tvClickable(onFocused = focus::set) { }
            .padding(horizontal = 10.dp, vertical = 8.dp),
    ) {
        Box(
            modifier = Modifier
                .weight(1f)
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(Color(0x33FFFFFF)),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxHeight()
                    .fillMaxWidth(fraction)
                    .background(RenzoColors.Primary),
            )
        }
        if (showPageNumber) {
            Text(
                "${(page + 1).coerceAtMost(pageCount)} / $pageCount",
                fontSize = 13.sp,
                color = if (focus.focused) ReaderPalette.Text else ReaderPalette.Text70,
                modifier = Modifier.padding(start = 10.dp),
            )
        }
    }
}
