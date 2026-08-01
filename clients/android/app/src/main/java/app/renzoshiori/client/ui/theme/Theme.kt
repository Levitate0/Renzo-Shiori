package app.renzoshiori.client.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// Ported from RenzoFrontend/src/styles/globals.css's default (rose) accent —
// hsl(346.8 77.2% 49.8%) — so the native app opens on the same brand color
// instead of Material's stock purple. Renzo Shiori is dark-only on web; same
// here, no light scheme.
private val RenzoPrimary = Color(0xFFE11D48)
private val RenzoBackground = Color(0xFF0D0A09) // hsl(20 14.3% 4.1%)
private val RenzoSurface = Color(0xFF1C1614) // hsl(24 9.8% 10%)
private val RenzoOnBackground = Color(0xFFF2F2F2) // hsl(0 0% 95%)

private val RenzoColorScheme = darkColorScheme(
    primary = RenzoPrimary,
    onPrimary = Color(0xFFFFF1F2),
    background = RenzoBackground,
    onBackground = RenzoOnBackground,
    surface = RenzoSurface,
    onSurface = RenzoOnBackground,
)

@Composable
fun RenzoTheme(content: @Composable () -> Unit) {
    // Dark-only regardless of system setting, matching the web app.
    MaterialTheme(colorScheme = RenzoColorScheme, content = content)
}
