package app.renzoshiori.client.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.tvFocusable

/**
 * Alias for [tvFocusable], kept so existing call sites keep compiling.
 *
 * This existed because the shared helper applied its opaque fill AFTER the focus
 * ring, and modifiers draw outermost-first — so the fill painted over the 3dp
 * ring and focus went invisible on exactly the row that had it. That ordering
 * bug is now fixed in `ui/tv/TvFocus.kt`, so this forwards rather than
 * duplicating; call sites can migrate to [tvFocusable] and this file can go.
 */
@Composable
fun Modifier.tvFocusTarget(
    focused: Boolean,
    onFocused: (Boolean) -> Unit,
    radius: Dp = 10.dp,
    enabled: Boolean = true,
    fill: Color = RenzoColors.Card,
    onClick: () -> Unit,
): Modifier = this.tvFocusable(
    focused = focused,
    onFocused = onFocused,
    radius = radius,
    enabled = enabled,
    fill = fill,
    onClick = onClick,
)
