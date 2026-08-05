package app.renzoshiori.client.ui.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Language
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.RenzoColors

/**
 * The leading mark on a selected row. Colour alone can't carry selection —
 * colour-blind users and badly calibrated panels both lose it — so every
 * selection control pairs [tvContentColor] with this.
 */
@Composable
fun TvSelectedMark(selected: Boolean, modifier: Modifier = Modifier) {
    Box(modifier.size(20.dp), contentAlignment = Alignment.Center) {
        if (selected) {
            Icon(
                Icons.Filled.Check,
                contentDescription = "Selected",
                tint = RenzoColors.Primary,
                modifier = Modifier.size(18.dp),
            )
        }
    }
}

/**
 * Stand-in for a screen that is deliberately not usable from a remote —
 * settings, sources, the import wizard. These are typed, long and error-prone:
 * they're where TV UX dies, and they aren't what anyone picks up a remote to do.
 *
 * Leaving them reachable-but-broken is worse than not shipping them, so the TV
 * build points at the instance's own web address instead. Any household
 * computer or tablet does the job — which, unlike "set it up on your phone",
 * doesn't assume the user owns one.
 */
@Composable
fun TvUseAComputerScreen(
    title: String,
    serverUrl: String?,
    path: String,
    modifier: Modifier = Modifier,
) {
    val address = buildString {
        append(serverUrl?.trimEnd('/')?.ifBlank { null } ?: "your server")
        append(path)
    }
    Column(
        modifier = modifier.fillMaxSize().padding(48.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(
            Icons.Filled.Language,
            contentDescription = null,
            tint = RenzoColors.MutedForeground,
            modifier = Modifier.size(48.dp),
        )
        Text(
            title,
            style = MaterialTheme.typography.headlineSmall,
            color = RenzoColors.Foreground,
            modifier = Modifier.padding(top = 20.dp),
        )
        Text(
            "This is easier on a computer or tablet than with a remote.",
            style = MaterialTheme.typography.bodyLarge,
            color = RenzoColors.MutedForeground,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 10.dp),
        )
        Text(
            address,
            style = MaterialTheme.typography.titleMedium.copy(fontFamily = FontFamily.Monospace),
            color = RenzoColors.Primary,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 24.dp).fillMaxWidth(),
        )
    }
}

/**
 * A selection row for TV option lists (reading mode, fit, background…).
 * Selection is colour + check; focus is ring + fill. See [tvContentColor].
 */
@Composable
fun TvOptionRow(
    label: String,
    selected: Boolean,
    enabled: Boolean = true,
    trailing: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    val focus = rememberFocusState()
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .tvFocusable(
                focused = focus.focused,
                onFocused = focus::set,
                radius = 10.dp,
                enabled = enabled,
                onClick = onClick,
            )
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        TvSelectedMark(selected)
        Text(
            label,
            style = MaterialTheme.typography.bodyLarge,
            color = tvContentColor(selected, focus.focused, enabled),
            modifier = Modifier.padding(start = 10.dp).width(0.dp).then(Modifier.fillMaxWidth(0.7f)),
        )
        if (trailing != null) {
            Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) { trailing() }
        }
    }
}
