package app.renzoshiori.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.platform.LocalContext
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.UpdateUserDto
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.network.AccountApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonPrimitive

/**
 * Per-user Appearance — transliterated from
 * RenzoFrontend/src/app/appearance/page.tsx.
 *
 * Named presets with live preview swatches plus a custom-accent override; the
 * app is dark-only (light mode was permanently removed on the web, so there is
 * no mode switch to port). Every change is written straight to
 * `PUT /api/auth/me { preferences }` so it follows the account across devices —
 * MERGED into the existing blob, never overwriting it, because that same blob
 * also carries the onboarding flag and the per-user source-priority prefs.
 *
 * The web's `<input type="color">` has no native counterpart, so the custom
 * accent is picked with hue/saturation/lightness sliders that write the exact
 * same `"H S% L%"` string the web stores.
 */
@Composable
fun AppearanceScreen(onBack: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }

    var username by remember { mutableStateOf("you") }
    var prefsRaw by remember { mutableStateOf<String?>(null) }
    var loaded by remember { mutableStateOf(false) }
    var preset by remember { mutableStateOf(DEFAULT_PRESET) }
    var customOn by remember { mutableStateOf(false) }
    var customHsl by remember { mutableStateOf(DEFAULT_CUSTOM_ACCENT) }

    LaunchedEffect(Unit) {
        runCatching { app.network.currentServiceOf<AccountApi>()?.me() }
            .onSuccess { me: UserDto? ->
                if (me != null) {
                    username = me.username
                    prefsRaw = me.preferences
                    preset = presetById(prefString(me.preferences, "preset")).id
                    customOn = prefString(me.preferences, "accent") == "custom"
                    customHsl = prefString(me.preferences, "accentCustom") ?: DEFAULT_CUSTOM_ACCENT
                }
                loaded = true
            }
            .onFailure {
                loaded = true
                snackbar.showSnackbar(it.apiMessage("Couldn't load your appearance settings"))
            }
    }

    // Dragging a slider must not fire one PUT per pixel — the web debounces
    // custom-accent writes by 400ms for the same reason.
    var saveJob by remember { mutableStateOf<Job?>(null) }

    /** Read → merge → write; the unknown keys in the blob must survive. */
    fun persist(debounce: Boolean = false) {
        saveJob?.cancel()
        saveJob = scope.launch {
            if (debounce) delay(400)
            val body = UpdateUserDto(
                preferences = mergePreferences(
                    prefsRaw,
                    mapOf(
                        "preset" to JsonPrimitive(preset),
                        "accent" to JsonPrimitive(if (customOn) "custom" else "preset"),
                        "accentCustom" to JsonPrimitive(customHsl),
                    ),
                ),
            )
            runCatching { app.network.currentServiceOf<AccountApi>()?.updateMe(body) }
                .onSuccess { prefsRaw = it?.preferences ?: prefsRaw }
                .onFailure { snackbar.showSnackbar("Couldn't save your appearance settings.") }
        }
    }

    val activePreset = presetById(preset)
    val accentColor = if (customOn) hslStrToColor(customHsl) else hslStrToColor(activePreset.accent)

    // Repaint the whole app as the choice changes — the web swaps the CSS
    // custom properties live, so the preview IS the app.
    LaunchedEffect(preset, customOn, customHsl) {
        RenzoColors.applyTheme(
            background = hslStrToColor(activePreset.bg),
            card = hslStrToColor(activePreset.card),
            primary = accentColor,
        )
    }

    SettingsScaffold(title = "Appearance", onBack = onBack, snackbar = snackbar) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
        ) {
            PageHeading(
                "Appearance",
                "Personalize how Renzo Shiori looks for $username — saved to your account, so it " +
                    "follows you on every device.",
            )
            Spacer(Modifier.height(20.dp))

            if (!loaded) {
                LoadingBlock()
                return@Column
            }

            // ── Theme ───────────────────────────────────────────────────
            SettingsCard(
                title = "Theme",
                description = "Pick a look. Applies instantly and syncs to your account.",
            ) {
                THEME_PRESETS.chunked(2).forEach { row ->
                    Row(modifier = Modifier.fillMaxWidth().padding(bottom = 10.dp)) {
                        row.forEachIndexed { index, themePreset ->
                            val active = themePreset.id == preset
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .padding(end = if (index == 0 && row.size > 1) 10.dp else 0.dp)
                                    .clip(RoundedCornerShape(12.dp))
                                    .border(
                                        if (active) 2.dp else 1.dp,
                                        if (active) RenzoColors.Primary else RenzoColors.Border,
                                        RoundedCornerShape(12.dp),
                                    )
                                    .clickable {
                                        preset = themePreset.id
                                        persist()
                                    }
                                    .padding(10.dp),
                            ) {
                                // The web's swatch: page background, a card
                                // block, and an accent dot.
                                Box(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .height(60.dp)
                                        .clip(RoundedCornerShape(8.dp))
                                        .background(hslStrToColor(themePreset.bg))
                                        .border(1.dp, Color.White.copy(alpha = 0.08f), RoundedCornerShape(8.dp)),
                                ) {
                                    Box(
                                        modifier = Modifier
                                            .padding(start = 8.dp, top = 16.dp)
                                            .width(60.dp)
                                            .height(22.dp)
                                            .clip(RoundedCornerShape(6.dp))
                                            .background(hslStrToColor(themePreset.card)),
                                    )
                                    Box(
                                        modifier = Modifier
                                            .align(Alignment.TopEnd)
                                            .padding(8.dp)
                                            .size(14.dp)
                                            .clip(CircleShape)
                                            .background(hslStrToColor(themePreset.accent)),
                                    )
                                }
                                Row(
                                    verticalAlignment = Alignment.CenterVertically,
                                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                                ) {
                                    Text(
                                        themePreset.label,
                                        style = MaterialTheme.typography.titleSmall,
                                        color = RenzoColors.Foreground,
                                        modifier = Modifier.weight(1f),
                                    )
                                    if (active) {
                                        Icon(
                                            Icons.Filled.Check, contentDescription = "Selected",
                                            tint = RenzoColors.Primary, modifier = Modifier.size(14.dp),
                                        )
                                    }
                                }
                            }
                        }
                        if (row.size == 1) Spacer(Modifier.weight(1f))
                    }
                }
            }

            Spacer(Modifier.height(16.dp))

            // ── Accent ──────────────────────────────────────────────────
            SettingsCard(
                title = "Accent color",
                description = "Recolors buttons, highlights, and focus rings across the app. Use " +
                    "a preset theme above or choose your own.",
            ) {
                Text("Custom accent", style = MaterialTheme.typography.titleSmall, color = RenzoColors.Foreground)
                Text(
                    "Override the selected theme's highlight color.",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.padding(bottom = 12.dp),
                )

                val (hue, saturation, lightness) = parseHsl(customHsl)
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    Box(
                        modifier = Modifier
                            .width(56.dp)
                            .height(36.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .background(accentColor)
                            .border(
                                if (customOn) 2.dp else 1.dp,
                                if (customOn) RenzoColors.Foreground else RenzoColors.Border,
                                RoundedCornerShape(8.dp),
                            ),
                    )
                    Spacer(Modifier.width(12.dp))
                    Text(
                        if (customOn) customHsl else activePreset.accent,
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier.weight(1f),
                    )
                    if (customOn) {
                        RenzoButton(
                            text = "Use preset accent",
                            variant = "outline",
                            small = true,
                            onClick = {
                                customOn = false
                                persist()
                            },
                        )
                    }
                }

                Spacer(Modifier.height(12.dp))
                AccentSlider("Hue", hue, 0f, 360f, accentColor) { value ->
                    customOn = true
                    customHsl = hslToStr(value, saturation, lightness)
                    persist(debounce = true)
                }
                AccentSlider("Saturation", saturation * 100f, 0f, 100f, accentColor) { value ->
                    customOn = true
                    customHsl = hslToStr(hue, value / 100f, lightness)
                    persist(debounce = true)
                }
                AccentSlider("Lightness", lightness * 100f, 0f, 100f, accentColor) { value ->
                    customOn = true
                    customHsl = hslToStr(hue, saturation, value / 100f)
                    persist(debounce = true)
                }

                // ── Live preview ────────────────────────────────────────
                Spacer(Modifier.height(12.dp))
                Text(
                    "PREVIEW",
                    style = MaterialTheme.typography.labelSmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.padding(bottom = 8.dp),
                )
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                        .background(hslStrToColor(activePreset.card))
                        .padding(16.dp),
                ) {
                    Text("Series", style = MaterialTheme.typography.bodySmall, color = RenzoColors.MutedForeground)
                    Text(
                        "Renzo Shiori",
                        style = MaterialTheme.typography.titleMedium,
                        color = RenzoColors.Foreground,
                    )
                    Text(
                        "2026 · Action",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                    )
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                    ) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier
                                .clip(RoundedCornerShape(8.dp))
                                .background(accentColor)
                                .padding(horizontal = 12.dp, vertical = 7.dp),
                        ) {
                            Icon(
                                Icons.Filled.PlayArrow, contentDescription = null,
                                tint = Color.White, modifier = Modifier.size(14.dp),
                            )
                            Text(
                                "Read",
                                style = MaterialTheme.typography.labelMedium,
                                color = Color.White,
                                modifier = Modifier.padding(start = 6.dp),
                            )
                        }
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier
                                .clip(RoundedCornerShape(8.dp))
                                .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                                .padding(horizontal = 12.dp, vertical = 7.dp),
                        ) {
                            Icon(
                                Icons.Filled.Add, contentDescription = null,
                                tint = RenzoColors.Foreground, modifier = Modifier.size(14.dp),
                            )
                            Text(
                                "List",
                                style = MaterialTheme.typography.labelMedium,
                                color = RenzoColors.Foreground,
                                modifier = Modifier.padding(start = 6.dp),
                            )
                        }
                    }
                }
            }

            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
private fun AccentSlider(
    label: String,
    value: Float,
    min: Float,
    max: Float,
    accent: Color,
    onChange: (Float) -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text(
                label,
                style = MaterialTheme.typography.labelMedium,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.weight(1f),
            )
            Text(
                value.toInt().toString(),
                style = MaterialTheme.typography.labelMedium,
                color = RenzoColors.MutedForeground,
            )
        }
        Slider(
            value = value.coerceIn(min, max),
            onValueChange = onChange,
            valueRange = min..max,
            colors = SliderDefaults.colors(
                thumbColor = accent,
                activeTrackColor = accent,
                inactiveTrackColor = RenzoColors.Secondary,
            ),
        )
    }
}
