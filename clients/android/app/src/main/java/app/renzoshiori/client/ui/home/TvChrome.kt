package app.renzoshiori.client.ui.home

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.components.RibbonToggleChip
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.TvSelectedMark
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable
import app.renzoshiori.client.ui.tv.tvContentColor

/**
 * D-pad chrome shared by the shell and the small screens (updates, queue,
 * downloads, status).
 *
 * Every control here keeps its existing touch behaviour *verbatim* — same
 * composable, same ripple — and only grows a focus ring (and, where a current
 * value exists, a separate selection channel) when [LocalIsTv] is true. There
 * is deliberately no parallel TV screen tree; see ui/tv/TvFocus.kt.
 */

/**
 * `clickable` on a phone; ring + fill + focusable on a television.
 *
 * The touch path is left completely alone because [tvClickable] passes
 * `indication = null` — losing the ripple would be a real regression on a
 * phone, and a ripple is invisible from a sofa, so neither can serve both.
 *
 * Pass `fill = null` for controls that already paint their own background
 * (selected pills, coloured buttons) so focus doesn't repaint them.
 *
 * Scroll-into-view comes free: `Modifier.focusable` brings its item into view
 * inside a scrollable parent, so focus can't strand off-screen in a LazyColumn.
 */
@Composable
fun Modifier.dpadClickable(
    radius: Dp = 8.dp,
    enabled: Boolean = true,
    fill: Color? = RenzoColors.Card,
    onClick: () -> Unit,
): Modifier {
    val isTv = LocalIsTv.current
    // Remembered unconditionally: LocalIsTv is fixed for the process, but a
    // `remember` behind a branch is the kind of thing that rots quietly.
    val focus = rememberFocusState()
    if (!isTv) return this.clickable(enabled = enabled, onClick = onClick)
    return this
        .then(
            if (fill != null) {
                Modifier.background(
                    if (focus.focused) fill else Color.Transparent,
                    RoundedCornerShape(radius),
                )
            } else {
                Modifier
            },
        )
        .focusRing(focus.focused, radius)
        .tvClickable(onFocused = focus::set, enabled = enabled, onClick = onClick)
}

/**
 * An icon-only action. Material's [IconButton] on a phone (its ripple and 48dp
 * touch target are right there); a ringed 48dp box on a television.
 *
 * Note [IconButton] has no `shape` parameter — the TV branch is a plain Box for
 * exactly that reason.
 */
@Composable
fun DpadIconButton(
    icon: ImageVector,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    tint: Color = RenzoColors.Foreground,
    /** Decoration that belongs to the glyph itself (the account panel's
     *  hairline circle), preserved on both paths. */
    iconModifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    if (!LocalIsTv.current) {
        IconButton(onClick = onClick, modifier = modifier) {
            Icon(icon, contentDescription = contentDescription, tint = tint, modifier = iconModifier)
        }
        return
    }
    Box(
        contentAlignment = Alignment.Center,
        modifier = modifier
            .size(48.dp)
            .dpadClickable(radius = 24.dp, onClick = onClick),
    ) {
        Icon(icon, contentDescription = contentDescription, tint = tint, modifier = iconModifier)
    }
}

/**
 * The ribbon's "My library" / "All libraries" pill. On TV the active state is a
 * *selection*, so it keeps its accent colour and check mark while the cursor
 * moves elsewhere; the ring is what says "you are here".
 */
@Composable
fun DpadToggleChip(
    label: String,
    active: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
) {
    if (!LocalIsTv.current) {
        RibbonToggleChip(label = label, active = active, onClick = onClick, modifier = modifier, icon = icon)
        return
    }
    val focus = rememberFocusState()
    val shape = RoundedCornerShape(50)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .height(38.dp)
            .background(
                when {
                    active -> RenzoColors.Primary.copy(alpha = 0.15f)
                    focus.focused -> RenzoColors.Card
                    else -> Color.Transparent
                },
                shape,
            )
            .border(
                1.dp,
                if (active) RenzoColors.Primary.copy(alpha = 0.40f) else RenzoColors.Border.copy(alpha = 0.40f),
                shape,
            )
            .focusRing(focus.focused, 50.dp)
            .tvClickable(onFocused = focus::set, onClick = onClick)
            .padding(horizontal = 12.dp),
    ) {
        TvSelectedMark(active, Modifier.padding(end = 4.dp))
        if (icon != null) {
            Icon(
                icon,
                contentDescription = null,
                tint = tvContentColor(active, focus.focused),
                modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = tvContentColor(active, focus.focused),
            maxLines = 1,
        )
    }
}

/**
 * The TV rendering of a segmented filter (the queue's All/Completed/Failed/
 * Queued, the status page's Sources|Series). Touch callers keep their own
 * `SegmentedPills` / `SegmentedTabs`; this is only reached under [LocalIsTv].
 *
 * The selected segment stays accent-coloured and check-marked wherever the
 * cursor goes — conflating "focused" with "selected" here is precisely how a
 * user loses track of which filter is actually applied.
 */
@Composable
fun DpadSegmentedPills(
    labels: List<String>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
    counts: List<Int?> = emptyList(),
) {
    val outer = RoundedCornerShape(50)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
        modifier = modifier
            .border(1.dp, RenzoColors.Border.copy(alpha = 0.6f), outer)
            .padding(3.dp),
    ) {
        labels.forEachIndexed { index, label ->
            val selected = index == selectedIndex
            val focus = rememberFocusState()
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .background(
                        when {
                            selected -> RenzoColors.Primary.copy(alpha = 0.18f)
                            focus.focused -> RenzoColors.Card
                            else -> Color.Transparent
                        },
                        outer,
                    )
                    .focusRing(focus.focused, 50.dp)
                    .tvClickable(onFocused = focus::set) { onSelect(index) }
                    .padding(horizontal = 14.dp, vertical = 7.dp),
            ) {
                TvSelectedMark(selected, Modifier.padding(end = 2.dp))
                Text(
                    label,
                    style = MaterialTheme.typography.labelMedium,
                    color = tvContentColor(selected, focus.focused),
                    maxLines = 1,
                )
                val count = counts.getOrNull(index)
                if (count != null && count > 0) {
                    Text(
                        count.toString(),
                        style = MaterialTheme.typography.labelSmall,
                        color = tvContentColor(selected, focus.focused).copy(alpha = 0.7f),
                        modifier = Modifier.padding(start = 4.dp),
                    )
                }
            }
        }
    }
}
