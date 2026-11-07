package extension.bridge.server

import androidx.preference.DialogPreference
import androidx.preference.EditTextPreference
import androidx.preference.ListPreference
import androidx.preference.MultiSelectListPreference
import androidx.preference.Preference
import androidx.preference.PreferenceScreen
import eu.kanade.tachiyomi.source.CatalogueSource
import eu.kanade.tachiyomi.source.ConfigurableSource
import eu.kanade.tachiyomi.source.sourcePreferences
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.put
import java.util.concurrent.ConcurrentHashMap

/**
 * A source's settings screen, the way the in-process bridge's SourceInterop
 * served it: built once per loaded source from the extension's own
 * setupPreferenceScreen, values read from and written to its
 * SharedPreferences (settings/source_<id>.xml, the same files as before the
 * sidecar). Field names match the C# KeyPreference, so the app reads the
 * answer straight into it.
 */
object SidecarPreferences {
    private class Built(val source: CatalogueSource, val screen: PreferenceScreen)

    private val screens = ConcurrentHashMap<Long, Built>()

    /** The cached screen, rebuilt when the extension was reloaded (a new source object). */
    private fun screen(s: ConfigurableSource): PreferenceScreen {
        screens[s.id]?.takeIf { it.source === s }?.let { return it.screen }
        synchronized(screens) {
            screens[s.id]?.takeIf { it.source === s }?.let { return it.screen }
            val screen = ConfigurableSource.PrefsHelper().createScreen()
            screen.sharedPreferences = s.sourcePreferences()
            s.setupPreferenceScreen(screen)
            screens[s.id] = Built(s as CatalogueSource, screen)
            return screen
        }
    }

    fun list(s: CatalogueSource): String {
        if (s !is ConfigurableSource) return "[]"
        return buildJsonArray {
            screen(s).preferences.forEachIndexed { i, p -> if (p != null) add(describe(p, i)) }
        }.toString()
    }

    fun set(s: CatalogueSource, index: Int, value: String): String {
        if (s !is ConfigurableSource) throw IllegalStateException("${s.name} has no settings")
        val p = screen(s).preferences.getOrNull(index) ?: throw IllegalArgumentException("No setting at $index")
        val newValue: Any = when (val type = p.defaultValueType) {
            "String" -> value
            "Boolean" -> value.equals("true", ignoreCase = true)
            "Set<String>" -> setOf(value)
            else -> throw IllegalArgumentException("Unsupported setting type $type")
        }
        // Save first, then tell the extension: the order the in-process bridge
        // (and Suwayomi) used. A listener that throws (one showing a Toast, say)
        // must not undo the save.
        p.saveNewValue(newValue)
        runCatching { p.callChangeListener(newValue) }
            .onFailure { System.err.println("[sidecar] ${s.name}: setting '${p.key}' listener failed: $it") }
        return """{"ok":true}"""
    }

    /** A Set<String> value travels as a JSON array of strings. */
    private fun setOf(value: String): Set<String> =
        if (value.isBlank()) emptySet()
        else (Json.parseToJsonElement(value) as JsonArray).mapNotNull { (it as? JsonPrimitive)?.contentOrNull }.toHashSet()

    private fun text(c: CharSequence?): String = c?.toString() ?: ""

    private fun valueText(v: Any?): String = when (v) {
        null -> ""
        is Set<*> -> buildJsonArray { v.forEach { add(JsonPrimitive(it?.toString() ?: "")) } }.toString()
        else -> v.toString()
    }

    private fun describe(p: Preference, index: Int) = buildJsonObject {
        put("Index", index)
        put("Type", p.javaClass.simpleName)
        put("Key", p.key ?: "")
        put("Title", text(p.title))
        put("Summary", text(runCatching { p.summary }.getOrNull()))
        put("DefaultValue", valueText(p.defaultValue))
        // A bare Preference with no default (an info line) has no type at all.
        put("DefaultValueType", runCatching { p.defaultValueType }.getOrDefault(""))
        put("CurrentValue", valueText(runCatching { p.currentValue }.getOrNull()))
        put("Visible", p.visible)
        when (p) {
            is ListPreference -> {
                put("Entries", strings(p.entries))
                put("EntryValues", strings(p.entryValues))
            }
            is MultiSelectListPreference -> {
                put("Entries", strings(p.entries))
                put("EntryValues", strings(p.entryValues))
            }
        }
        if (p is DialogPreference) {
            put("DialogTitle", text(p.dialogTitle))
            put("DialogMessage", text(p.dialogMessage))
        }
        if (p is EditTextPreference) put("Text", runCatching { p.text }.getOrNull() ?: "")
    }

    private fun strings(a: Array<CharSequence>?) = buildJsonArray { a?.forEach { add(JsonPrimitive(it.toString())) } }
}
