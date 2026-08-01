package app.renzoshiori.client.ui.importwizard

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.ui.theme.RenzoColors

/**
 * The import wizard's own palette — the `--as-*` / `.iw-*` tokens from
 * RenzoFrontend src/styles/globals.css, converted to opaque ARGB for Compose
 * (the web layers translucent panels over a blurred dark backdrop; on native
 * the composited result is used directly).
 */
object WizardColors {
    /** .cmd-card — hsla(240 10% 8% / .72) over the dark scrim. */
    val Shell = Color(0xFF121216)

    /** .iw-scan-card / .iw-hero-progress — hsla(240 8% 6% / .5). */
    val Panel = Color(0xFF101014)

    /** .iw-import-card gradient (240 8% 10% → 240 8% 7%). */
    val CardBg = Color(0xFF15151A)

    /** .iw-tabs container — hsla(240 8% 6% / .6). */
    val TabStrip = Color(0xFF0F0F13)

    /** .iw-tab.is-active — hsla(240 6% 14%) → hsla(240 6% 9%). */
    val TabActive = Color(0xFF1E1E22)

    /** Mobile .iw-match-switch-cell — hsla(240 8% 5% / .5). */
    val SwitchCell = Color(0xFF0D0D10)

    val Fg = Color(0xFFFAFAFA)          // --as-fg: 0 0% 98%
    val FgMuted = Color(0xFFA3A3A3)     // --as-fg-muted: 0 0% 64%
    val FgDim = Color(0xFF757575)       // --as-fg-dim: 0 0% 46%

    val Border = Color(0x0FFFFFFF)      // --as-border: white / .06
    val BorderStrong = Color(0x1FFFFFFF) // --as-border-strong: white / .12

    val Primary = RenzoColors.Primary
    val Destructive = RenzoColors.Destructive

    // Filter-tab dots + banners.
    val Green = Color(0xFF22C55E)
    val DoneGreen = Color(0xFF4ADE80)
    val Violet = Color(0xFFA855F7)
    val Blue = Color(0xFF3B82F6)
    val Gray = Color(0xFF64748B)
    val Warn = Color(0xFFFACC15)
}

/**
 * The mono voice the wizard uses for eyebrows, step meta, buttons, percentages
 * and status lines. The web sets JetBrains Mono; the app ships only Geist, so
 * the platform monospace stands in (same role, same tracking).
 */
fun wizardMono(
    size: Float,
    weight: FontWeight = FontWeight.SemiBold,
    tracking: Float = 0.14f,
    color: Color = WizardColors.FgMuted,
): TextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontSize = size.sp,
    fontWeight = weight,
    letterSpacing = (size * tracking).sp,
    color = color,
)

/** .iw-progress-bar + .iw-progress-fill. */
@Composable
fun WizardProgressBar(progress: Float, modifier: Modifier = Modifier, height: Int = 3) {
    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(height.dp)
            .clip(RoundedCornerShape(2.dp))
            .background(Color(0x0FFFFFFF)),
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth(progress.coerceIn(0f, 1f))
                .height(height.dp)
                .clip(RoundedCornerShape(2.dp))
                .background(
                    Brush.horizontalGradient(
                        listOf(WizardColors.Primary, Color(0xFFF2698A)),
                    ),
                ),
        )
    }
}

/**
 * Indeterminate variant of the same bar: a travelling 35% band. Used by the
 * final Import step, whose server-side percentage is only broadcast over the
 * SignalR hub the native client deliberately doesn't speak.
 */
@Composable
fun WizardIndeterminateBar(modifier: Modifier = Modifier, height: Int = 5) {
    val transition = rememberInfiniteTransition(label = "iw-indeterminate")
    val offset by transition.animateFloat(
        initialValue = -0.35f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(1400, easing = LinearEasing),
            repeatMode = RepeatMode.Restart,
        ),
        label = "iw-indeterminate-offset",
    )
    BoxWithConstraints(
        modifier = modifier
            .fillMaxWidth()
            .height(height.dp)
            .clip(RoundedCornerShape(2.dp))
            .background(Color(0x0FFFFFFF)),
    ) {
        val track = maxWidth
        Box(
            modifier = Modifier
                .offset(x = track * offset)
                .width(track * 0.35f)
                .height(height.dp)
                .clip(RoundedCornerShape(2.dp))
                .background(
                    Brush.horizontalGradient(
                        listOf(WizardColors.Primary, Color(0xFFF2698A)),
                    ),
                ),
        )
    }
}

/** .iw-scan-icon.is-spinning — a 2px ring with a rose top edge, 1s linear. */
@Composable
fun WizardSpinner(size: Int, modifier: Modifier = Modifier) {
    val transition = rememberInfiniteTransition(label = "iw-spin")
    val angle by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(
            animation = tween(1000, easing = LinearEasing),
            repeatMode = RepeatMode.Restart,
        ),
        label = "iw-spin-angle",
    )
    Box(
        modifier = modifier
            .size(size.dp)
            .rotate(angle)
            .border(
                width = 2.dp,
                brush = Brush.sweepGradient(
                    listOf(
                        WizardColors.Primary,
                        WizardColors.Primary.copy(alpha = 0.25f),
                        WizardColors.Primary.copy(alpha = 0.25f),
                        WizardColors.Primary,
                    ),
                ),
                shape = CircleShape,
            ),
    )
}

/** .iw-done-banner */
@Composable
fun WizardDoneBanner(text: String, modifier: Modifier = Modifier) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(
                Brush.horizontalGradient(
                    listOf(Color(0x2E1FA97A), Color(0x0F1FA97A)),
                ),
            )
            .border(1.dp, Color(0x591FA97A), RoundedCornerShape(10.dp))
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        Icon(
            Icons.Filled.CheckCircle,
            contentDescription = null,
            tint = WizardColors.DoneGreen,
            modifier = Modifier.size(16.dp),
        )
        Text(
            text.uppercase(),
            style = wizardMono(11f, FontWeight.SemiBold, 0.12f, WizardColors.DoneGreen),
            modifier = Modifier.padding(start = 10.dp),
        )
    }
}

/** .iw-fail-banner */
@Composable
fun WizardFailBanner(title: String, body: String, modifier: Modifier = Modifier) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(
                Brush.horizontalGradient(
                    listOf(Color(0x1FEF4444), Color(0x0AEF4444)),
                ),
            )
            .border(1.dp, Color(0x59EF4444), RoundedCornerShape(10.dp))
            .padding(horizontal = 16.dp, vertical = 20.dp),
    ) {
        Icon(
            Icons.Filled.ErrorOutline,
            contentDescription = null,
            tint = WizardColors.Destructive,
            modifier = Modifier.size(40.dp),
        )
        Text(
            title,
            style = MaterialTheme.typography.titleMedium,
            color = WizardColors.Destructive,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 8.dp),
        )
        Text(
            body,
            style = MaterialTheme.typography.bodySmall,
            color = WizardColors.FgMuted,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 8.dp),
        )
    }
}
