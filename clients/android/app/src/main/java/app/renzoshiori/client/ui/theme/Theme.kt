package app.renzoshiori.client.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.R

/**
 * Faithful port of the web app's design tokens (RenzoFrontend
 * src/styles/globals.css :root, dark-only, default "rose" accent). Every
 * value below is the hex equivalent of the HSL custom property of the same
 * name — keep them in sync with globals.css, not with Material defaults.
 */
object RenzoColors {
    // Background / Card / Primary are theme-preset driven (Appearance page):
    // they're snapshot state so changing the preset or accent recomposes the
    // whole app live, exactly like the web swapping CSS custom properties.
    private var backgroundState by mutableStateOf(Color(0xFF0C0A09))
    private var cardState by mutableStateOf(Color(0xFF1C1917))
    private var primaryState by mutableStateOf(Color(0xFFE11D48))

    val Background: Color get() = backgroundState   // --background: 20 14.3% 4.1%
    val Card: Color get() = cardState               // --card: 24 9.8% 10%
    val Primary: Color get() = primaryState         // --primary: 346.8 77.2% 49.8% (rose)

    /** Applied by the Appearance screen and on sign-in from the prefs blob. */
    fun applyTheme(background: Color, card: Color, primary: Color) {
        backgroundState = background
        cardState = card
        primaryState = primary
    }

    val Foreground = Color(0xFFF2F2F2)      // --foreground: 0 0% 95%
    val Popover = Color(0xFF171717)         // --popover: 0 0% 9%
    val PrimaryForeground = Color(0xFFFFF1F2) // --primary-foreground
    val Secondary = Color(0xFF27272A)       // --secondary: 240 3.7% 15.9%
    val Muted = Color(0xFF262626)           // --muted: 0 0% 15%
    val MutedForeground = Color(0xFFA1A1AA) // --muted-foreground: 240 5% 64.9%
    val Border = Color(0xFF27272A)          // --border: 240 3.7% 15.9%
    val Destructive = Color(0xFF7F1D1D)     // --destructive: 0 62.8% 30.6%

    // Status colors used by the web library cards / queue badges (Tailwind).
    val Green = Color(0xFF22C55E)
    val Yellow = Color(0xFFEAB308)
    val Amber = Color(0xFFF59E0B)
    val Blue = Color(0xFF3B82F6)
    val Emerald = Color(0xFF10B981)
    val Red = Color(0xFFEF4444)
}

private fun renzoColorScheme() = darkColorScheme(
    primary = RenzoColors.Primary,
    onPrimary = RenzoColors.PrimaryForeground,
    background = RenzoColors.Background,
    onBackground = RenzoColors.Foreground,
    surface = RenzoColors.Card,
    onSurface = RenzoColors.Foreground,
    surfaceVariant = RenzoColors.Secondary,
    onSurfaceVariant = RenzoColors.MutedForeground,
    secondary = RenzoColors.Secondary,
    onSecondary = RenzoColors.Foreground,
    outline = RenzoColors.Border,
    outlineVariant = RenzoColors.Border,
    error = RenzoColors.Red,
    surfaceContainer = RenzoColors.Card,
    surfaceContainerHigh = RenzoColors.Popover,
    surfaceContainerHighest = RenzoColors.Secondary,
)

// Web --radius is 0.5rem (8px); cards use rounded-xl (12px). Material's
// default pill-shaped buttons are the single biggest "this isn't our app"
// tell, so shapes are globally squared to the web's scale.
private val RenzoShapes = Shapes(
    extraSmall = RoundedCornerShape(6.dp),
    small = RoundedCornerShape(8.dp),
    medium = RoundedCornerShape(8.dp),
    large = RoundedCornerShape(12.dp),
    extraLarge = RoundedCornerShape(16.dp),
)

// Geist Sans (v1.7.2, OFL) — the web app's actual font (GeistSans in
// layout.tsx). Bundling it is what makes native text read as "the same app"
// instead of generic Android; weights map to the web's usage (400 body,
// 500 medium, 600 semibold titles, 700 bold).
val GeistFamily = FontFamily(
    Font(R.font.geist_regular, FontWeight.Normal),
    Font(R.font.geist_medium, FontWeight.Medium),
    Font(R.font.geist_semibold, FontWeight.SemiBold),
    Font(R.font.geist_bold, FontWeight.Bold),
)

private val base = Typography()
private val RenzoTypography = Typography(
    displayLarge = base.displayLarge.copy(fontFamily = GeistFamily),
    displayMedium = base.displayMedium.copy(fontFamily = GeistFamily),
    displaySmall = base.displaySmall.copy(fontFamily = GeistFamily),
    headlineLarge = base.headlineLarge.copy(fontFamily = GeistFamily, fontWeight = FontWeight.Bold),
    headlineMedium = base.headlineMedium.copy(fontFamily = GeistFamily, fontWeight = FontWeight.Bold),
    headlineSmall = base.headlineSmall.copy(fontFamily = GeistFamily, fontWeight = FontWeight.SemiBold),
    titleLarge = base.titleLarge.copy(fontFamily = GeistFamily, fontWeight = FontWeight.SemiBold),
    titleMedium = base.titleMedium.copy(fontFamily = GeistFamily, fontWeight = FontWeight.SemiBold),
    titleSmall = base.titleSmall.copy(fontFamily = GeistFamily, fontWeight = FontWeight.Medium),
    bodyLarge = base.bodyLarge.copy(fontFamily = GeistFamily),
    bodyMedium = base.bodyMedium.copy(fontFamily = GeistFamily),
    bodySmall = base.bodySmall.copy(fontFamily = GeistFamily),
    labelLarge = base.labelLarge.copy(fontFamily = GeistFamily, fontWeight = FontWeight.Medium),
    labelMedium = base.labelMedium.copy(fontFamily = GeistFamily, fontWeight = FontWeight.Medium),
    labelSmall = base.labelSmall.copy(fontFamily = GeistFamily, fontWeight = FontWeight.Medium),
)

@Composable
fun RenzoTheme(content: @Composable () -> Unit) {
    // Dark-only regardless of system setting, matching the web app.
    MaterialTheme(
        colorScheme = renzoColorScheme(),
        shapes = RenzoShapes,
        typography = RenzoTypography,
        content = content,
    )
}
