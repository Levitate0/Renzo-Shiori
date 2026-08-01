package app.renzoshiori.client.ui.settings

import androidx.compose.ui.graphics.Color
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.math.abs
import kotlin.math.absoluteValue
import kotlin.math.roundToInt

/**
 * Port of RenzoFrontend/src/lib/utils/theme-prefs.ts + theme-preset.ts.
 *
 * The per-user `preferences` column is ONE JSON blob shared by appearance, the
 * onboarding flag, and the source-priority prefs (see
 * RenzoBackend/Extensions/UserPriorityPrefsExtensions.cs). Any writer MUST
 * read → parse → merge its own keys → re-serialize; blindly PUTting a fresh
 * object silently deletes whatever else lived in there. [mergePreferences] is
 * the only supported way to write it from the native client.
 */

private val prefsJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }

/** Parse the blob, keeping every unknown key intact. */
fun parsePreferences(raw: String?): Map<String, JsonElement> =
    runCatching {
        raw?.takeIf { it.isNotBlank() }?.let { prefsJson.parseToJsonElement(it).jsonObject.toMap() }
    }.getOrNull() ?: emptyMap()

/** Merge [updates] over the existing blob and return the JSON *string* to PUT. */
fun mergePreferences(existing: String?, updates: Map<String, JsonElement>): String {
    val merged = parsePreferences(existing).toMutableMap()
    merged.putAll(updates)
    return prefsJson.encodeToString(JsonObject.serializer(), JsonObject(merged))
}

fun prefString(raw: String?, key: String): String? =
    runCatching { parsePreferences(raw)[key]?.jsonPrimitive?.content }.getOrNull()

fun prefJson(value: String): JsonElement = JsonPrimitive(value)

// ── Theme presets ────────────────────────────────────────────────────────

/**
 * The named presets from theme-preset.ts, with their preview swatches kept as
 * the exact HSL strings globals.css uses so the native cards render the same
 * colours as the web cards rather than eyeballed hex approximations.
 */
data class ThemePreset(
    val id: String,
    val label: String,
    val bg: String,
    val card: String,
    val accent: String,
)

val THEME_PRESETS = listOf(
    ThemePreset("renzo", "Renzo", "20 14.3% 4.1%", "24 9.8% 10%", "346.8 77.2% 49.8%"),
    ThemePreset("amoled", "AMOLED", "0 0% 0%", "0 0% 7%", "346.8 77.2% 49.8%"),
    ThemePreset("midnight", "Midnight", "222 47% 8%", "222 40% 13%", "217.2 91.2% 59.8%"),
    ThemePreset("sakura", "Sakura", "330 22% 7%", "330 18% 12%", "340 82% 66%"),
    ThemePreset("matcha", "Matcha", "140 15% 6%", "140 12% 11%", "142.1 70.6% 45.3%"),
    ThemePreset("ember", "Ember", "20 22% 6%", "20 18% 11%", "24.6 95% 53.1%"),
    ThemePreset("ocean", "Ocean", "195 40% 7%", "195 34% 12%", "172 66% 45%"),
)

const val DEFAULT_PRESET = "renzo"
const val DEFAULT_CUSTOM_ACCENT = "265 83% 58%"

fun presetById(id: String?): ThemePreset =
    THEME_PRESETS.firstOrNull { it.id == id } ?: THEME_PRESETS[0]

// ── HSL <-> Color (the web's hslStrToHex / hexToHslStr) ──────────────────

/** Parses an `"H S% L%"` triple into a Compose [Color]. */
fun hslStrToColor(hsl: String): Color {
    val parts = hsl.trim().split(Regex("\\s+"))
    if (parts.size < 3) return Color.Black
    val h = parts[0].toFloatOrNull() ?: 0f
    val s = (parts[1].removeSuffix("%").toFloatOrNull() ?: 0f) / 100f
    val l = (parts[2].removeSuffix("%").toFloatOrNull() ?: 0f) / 100f
    return hslToColor(h, s, l)
}

fun hslToColor(hDeg: Float, s: Float, l: Float): Color {
    val h = ((hDeg % 360f) + 360f) % 360f / 360f
    val a = s * minOf(l, 1f - l)
    fun k(n: Int) = (n + h * 12f) % 12f
    fun f(n: Int): Float = l - a * maxOf(-1f, minOf(k(n) - 3f, minOf(9f - k(n), 1f)))
    return Color(f(0).coerceIn(0f, 1f), f(8).coerceIn(0f, 1f), f(4).coerceIn(0f, 1f))
}

/** Formats an HSL triple back into the `"H S% L%"` string the blob stores. */
fun hslToStr(hDeg: Float, s: Float, l: Float): String =
    "${hDeg.roundToInt()} ${(s * 100).roundToInt()}% ${(l * 100).roundToInt()}%"

/** Splits `"H S% L%"` into (hue 0..360, saturation 0..1, lightness 0..1). */
fun parseHsl(hsl: String): Triple<Float, Float, Float> {
    val parts = hsl.trim().split(Regex("\\s+"))
    val h = parts.getOrNull(0)?.toFloatOrNull() ?: 265f
    val s = (parts.getOrNull(1)?.removeSuffix("%")?.toFloatOrNull() ?: 83f) / 100f
    val l = (parts.getOrNull(2)?.removeSuffix("%")?.toFloatOrNull() ?: 58f) / 100f
    return Triple(h, s, l)
}

/** Colour → `"H S% L%"`, mirroring hexToHslStr for the picker's readout. */
fun colorToHslStr(color: Color): String {
    val r = color.red
    val g = color.green
    val b = color.blue
    val max = maxOf(r, g, b)
    val min = minOf(r, g, b)
    val l = (max + min) / 2f
    val d = max - min
    val s = if (d == 0f) 0f else d / (1f - abs(2f * l - 1f))
    var h = 0f
    if (d != 0f) {
        h = when (max) {
            r -> ((g - b) / d) % 6f
            g -> (b - r) / d + 2f
            else -> (r - g) / d + 4f
        } * 60f
        if (h < 0f) h += 360f
    }
    return hslToStr(h.absoluteValue, s, l)
}
