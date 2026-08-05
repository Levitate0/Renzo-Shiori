package app.renzoshiori.client.ui.tv

import android.app.UiModeManager
import android.content.Context
import android.content.pm.PackageManager
import android.content.res.Configuration
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.focusable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.RenzoColors

/**
 * D-pad support for the reading/discovery half of the app.
 *
 * The starting point is better than it looks: `Modifier.clickable` is focusable
 * by default and D-pad centre activates it, so every existing click target is
 * already *reachable*. What's missing is that you can't SEE where focus is
 * (Material's ripple is legible at arm's length, invisible across a room), and
 * that focus and selection get conflated.
 *
 * There is deliberately no separate TV screen tree — one composable per screen,
 * branching on [LocalIsTv] only where behaviour genuinely differs. Two parallel
 * trees diverge within a month.
 */

/** True when running on a television (leanback / UI mode television). */
val LocalIsTv = compositionLocalOf { false }

fun isTvDevice(context: Context): Boolean {
    val uiMode = context.getSystemService(Context.UI_MODE_SERVICE) as? UiModeManager
    if (uiMode?.currentModeType == Configuration.UI_MODE_TYPE_TELEVISION) return true
    val pm = context.packageManager
    return pm.hasSystemFeature(PackageManager.FEATURE_LEANBACK) ||
        pm.hasSystemFeature("android.hardware.type.television")
}

@Composable
fun rememberIsTvDevice(): Boolean {
    val context = LocalContext.current
    return remember(context) { isTvDevice(context) }
}

/**
 * The focus indicator: a thick accent ring, readable at three metres. Deliberately
 * 3dp — a 1dp hairline disappears at couch distance on a large panel.
 */
fun Modifier.focusRing(focused: Boolean, radius: Dp = 10.dp): Modifier =
    if (focused) border(3.dp, RenzoColors.Primary, RoundedCornerShape(radius)) else this

/** Focusable + clickable with a focus-state callback — the standard tile wrapper. */
@Composable
fun Modifier.tvClickable(
    onFocused: (Boolean) -> Unit,
    enabled: Boolean = true,
    onClick: () -> Unit,
): Modifier {
    val interaction = remember { MutableInteractionSource() }
    return this
        .onFocusChanged { onFocused(it.isFocused) }
        .clickable(interactionSource = interaction, indication = null, enabled = enabled, onClick = onClick)
        .focusable(enabled = enabled, interactionSource = interaction)
}

/**
 * Focus ring + fill + click in one call, for the common case.
 *
 * [selected] is intentionally NOT part of the ring: see [tvContentColor]. The
 * fill and ring say "the cursor is here"; colour says "this is what's active".
 */
@Composable
fun Modifier.tvFocusable(
    focused: Boolean,
    onFocused: (Boolean) -> Unit,
    radius: Dp = 10.dp,
    enabled: Boolean = true,
    fill: Color = RenzoColors.Card,
    onClick: () -> Unit,
): Modifier = this
    // Fill BEFORE ring: modifiers draw outermost-first, and an opaque background
    // applied after the border paints straight over the 3dp ring — making focus
    // invisible on exactly the rows that are focused. Order is load-bearing here.
    .background(if (focused) fill else Color.Transparent, RoundedCornerShape(radius))
    .focusRing(focused, radius)
    .tvClickable(onFocused = onFocused, enabled = enabled, onClick = onClick)

/**
 * Focused and selected are different things and must both be visible.
 *
 * - **Focused** — where the D-pad cursor is. Moves constantly, one per screen.
 * - **Selected** — what is actually in effect (open tab, current reading mode,
 *   chosen background). Does NOT move when focus does, and has to stay legible
 *   while the user navigates elsewhere.
 *
 * Conflating them means the user loses track of what they picked the moment
 * they move the stick. Colour carries selection; the ring and fill carry focus.
 * Both can be true at once and stay distinguishable.
 *
 * Colour alone is not enough — it fails for colour-blind users and washes out on
 * a badly calibrated panel — so pair this with a check/indicator on the selected
 * row (see [TvSelectedMark]).
 */
fun tvContentColor(selected: Boolean, focused: Boolean, enabled: Boolean = true): Color = when {
    !enabled -> RenzoColors.MutedForeground.copy(alpha = 0.4f)
    selected -> RenzoColors.Primary
    focused -> RenzoColors.Foreground
    else -> RenzoColors.MutedForeground
}

/** Remembers focus state for a single row/tile without boilerplate. */
@Composable
fun rememberFocusState(): MutableFocusState = remember { MutableFocusState() }

class MutableFocusState {
    var focused by mutableStateOf(false)
        private set

    fun set(value: Boolean) {
        focused = value
    }
}
