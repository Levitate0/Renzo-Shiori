package app.renzoshiori.client.ui.sources

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.renzoshiori.client.data.model.ProviderPreferenceEntryType
import app.renzoshiori.client.data.network.SourcesApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull

/**
 * Provider settings — the native form of
 * components/comp/provider-preferences-requester.tsx. Opens automatically after
 * an install (like the web) and from a row's "Settings…" menu item.
 *
 * The DTO is kept as a raw [JsonObject] and only each entry's `currentValue` is
 * replaced before it is POSTed straight back, because `defaultValue` /
 * `currentValue` are polymorphic (string | bool | string[]) and everything else
 * in the payload must survive the round trip untouched.
 */
@Composable
internal fun ProviderPreferencesDialog(
    api: SourcesApi?,
    pkgName: String,
    providerName: String?,
    onDismiss: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var root by remember(pkgName) { mutableStateOf<JsonObject?>(null) }
    var loading by remember(pkgName) { mutableStateOf(false) }
    var savingPrefs by remember(pkgName) { mutableStateOf(false) }
    var error by remember(pkgName) { mutableStateOf<String?>(null) }
    val items = remember(pkgName) { mutableStateListOf<PrefItem>() }

    LaunchedEffect(pkgName) {
        loading = true
        error = null
        runCatching { api?.providerPreferences(pkgName) }
            .onSuccess { obj ->
                root = obj
                items.clear()
                items.addAll(parsePrefItems(obj))
            }
            .onFailure { error = "Failed to load preferences" }
        loading = false
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Column(
            modifier = Modifier
                .padding(16.dp)
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(RenzoColors.Popover)
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                .padding(20.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.Settings,
                    contentDescription = null,
                    tint = RenzoColors.Foreground,
                    modifier = Modifier.size(20.dp),
                )
                Text(
                    if (!providerName.isNullOrEmpty()) "$providerName Settings" else "Provider Settings",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    color = RenzoColors.Foreground,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }
            Text(
                "Configure preferences for this provider. Changes will be saved automatically.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(top = 6.dp),
            )

            error?.let {
                Box(
                    modifier = Modifier
                        .padding(top = 12.dp)
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(6.dp))
                        .background(RenzoColors.Destructive.copy(alpha = 0.10f))
                        .border(1.dp, RenzoColors.Destructive.copy(alpha = 0.20f), RoundedCornerShape(6.dp))
                        .padding(12.dp),
                ) {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = RenzoColors.Red)
                }
            }

            if (loading) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.Center,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 32.dp),
                ) {
                    CircularProgressIndicator(
                        strokeWidth = 2.dp,
                        color = RenzoColors.Foreground,
                        modifier = Modifier.size(24.dp),
                    )
                    Text(
                        "Loading preferences...",
                        style = MaterialTheme.typography.bodyMedium,
                        color = RenzoColors.Foreground,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
            } else if (root != null) {
                Column(
                    verticalArrangement = Arrangement.spacedBy(24.dp),
                    modifier = Modifier
                        .padding(top = 16.dp)
                        .heightIn(max = 420.dp)
                        .verticalScroll(rememberScrollState()),
                ) {
                    items.forEachIndexed { index, item ->
                        PreferenceField(
                            item = item,
                            onValueChange = { newValue ->
                                items[index] = item.copy(currentValue = newValue)
                            },
                        )
                    }
                }
            }

            Row(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(top = 20.dp),
            ) {
                Box(Modifier.weight(1f))
                DialogButton(label = "Cancel", primary = false, enabled = true) { onDismiss() }
                DialogButton(
                    label = "Save Preferences",
                    primary = true,
                    loading = savingPrefs,
                    enabled = !loading && !savingPrefs && root != null,
                ) {
                    val current = root ?: return@DialogButton
                    scope.launch {
                        savingPrefs = true
                        error = null
                        runCatching {
                            api?.setProviderPreferences(rebuildWithValues(current, items.map { it.currentValue }))
                        }
                            .onSuccess { onDismiss() }
                            .onFailure { error = "Failed to save preferences" }
                        savingPrefs = false
                    }
                }
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Model
// ─────────────────────────────────────────────────────────────────────────────

private data class PrefItem(
    val type: Int,
    val title: String,
    val summary: String?,
    val entries: List<String>,
    val entryValues: List<String>,
    val defaultValue: JsonElement?,
    val currentValue: JsonElement?,
)

private fun JsonElement?.stringsOrEmpty(): List<String> =
    (this as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull } ?: emptyList()

private fun parsePrefItems(root: JsonObject?): List<PrefItem> {
    val arr = root?.get("preferences") as? JsonArray ?: return emptyList()
    return arr.mapNotNull { element ->
        val o = element as? JsonObject ?: return@mapNotNull null
        PrefItem(
            type = (o["type"] as? JsonPrimitive)?.intOrNull ?: ProviderPreferenceEntryType.COMBO_BOX,
            title = (o["title"] as? JsonPrimitive)?.contentOrNull ?: "",
            summary = (o["summary"] as? JsonPrimitive)?.contentOrNull,
            entries = o["entries"].stringsOrEmpty(),
            entryValues = o["entryValues"].stringsOrEmpty(),
            defaultValue = o["defaultValue"]?.takeIf { it !is JsonNull },
            currentValue = o["currentValue"]?.takeIf { it !is JsonNull },
        )
    }
}

private fun rebuildWithValues(root: JsonObject, values: List<JsonElement?>): JsonObject {
    val arr = root["preferences"] as? JsonArray ?: return root
    val rebuilt = JsonArray(
        arr.mapIndexed { i, element ->
            val o = element as? JsonObject ?: return@mapIndexed element
            buildJsonObject {
                o.forEach { (k, v) -> if (k != "currentValue") put(k, v) }
                put("currentValue", values.getOrNull(i) ?: JsonNull)
            }
        },
    )
    return buildJsonObject {
        root.forEach { (k, v) -> if (k != "preferences") put(k, v) }
        put("preferences", rebuilt)
    }
}

private fun PrefItem.effectiveValue(): JsonElement? = currentValue ?: defaultValue

/** getCurrentComboBoxValue — ensures the value actually exists in entryValues. */
private fun PrefItem.comboValue(): String {
    var current = (currentValue as? JsonPrimitive)?.contentOrNull
    if (current.isNullOrEmpty()) current = (defaultValue as? JsonPrimitive)?.contentOrNull
    if (entryValues.isNotEmpty()) {
        return if (current != null && entryValues.contains(current)) current else entryValues.first()
    }
    return current ?: ""
}

/** getProcessedSummary — %s is replaced by the selected ComboBox display entry. */
private fun PrefItem.processedSummary(): String {
    val s = summary ?: return ""
    if (type != ProviderPreferenceEntryType.COMBO_BOX || !s.contains("%s")) return s
    val currentValue = (effectiveValue() as? JsonPrimitive)?.contentOrNull
    if (!currentValue.isNullOrEmpty() && entries.isNotEmpty() && entryValues.isNotEmpty()) {
        val valueIndex = entryValues.indexOf(currentValue)
        if (valueIndex != -1 && valueIndex < entries.size) {
            return s.replace("%s", entries[valueIndex])
        }
    }
    return s.replace("%s", currentValue ?: "")
}

// ─────────────────────────────────────────────────────────────────────────────
// Fields
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun PreferenceField(item: PrefItem, onValueChange: (JsonElement?) -> Unit) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Text(
            item.title,
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.Foreground,
        )
        val summary = item.processedSummary()
        if (summary.isNotEmpty()) {
            Text(
                summary,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
        Box(modifier = Modifier.padding(top = 12.dp)) {
            when (item.type) {
                ProviderPreferenceEntryType.COMBO_BOX -> ComboBoxField(item, onValueChange)
                ProviderPreferenceEntryType.COMBO_CHECK_BOX -> ComboCheckBoxField(item, onValueChange)
                ProviderPreferenceEntryType.TEXT_BOX -> TextBoxField(item, onValueChange)
                ProviderPreferenceEntryType.SWITCH -> SwitchField(item, onValueChange)
                else -> Text(
                    "Unknown preference type",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                )
            }
        }
    }
}

@Composable
private fun ComboBoxField(item: PrefItem, onValueChange: (JsonElement?) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val value = item.comboValue()
    val label = item.entryValues.indexOf(value)
        .takeIf { it >= 0 && it < item.entries.size }
        ?.let { item.entries[it] }
        ?: value.ifEmpty { "Select an option" }

    Box {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .fillMaxWidth()
                .height(36.dp)
                .clip(RoundedCornerShape(6.dp))
                .background(RenzoColors.Background)
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(6.dp))
                .clickable { open = true }
                .padding(horizontal = 12.dp),
        ) {
            Text(
                label,
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Icon(
                Icons.Filled.ExpandMore,
                contentDescription = null,
                tint = RenzoColors.MutedForeground,
                modifier = Modifier.size(16.dp),
            )
        }
        DropdownMenu(
            expanded = open,
            onDismissRequest = { open = false },
            containerColor = RenzoColors.Popover,
        ) {
            item.entries.forEachIndexed { entryIndex, entry ->
                val rawValue = item.entryValues.getOrNull(entryIndex) ?: entry
                DropdownMenuItem(
                    text = {
                        Text(entry, style = MaterialTheme.typography.bodyMedium, color = RenzoColors.Foreground)
                    },
                    onClick = {
                        open = false
                        onValueChange(JsonPrimitive(rawValue))
                    },
                )
            }
        }
    }
}

@Composable
private fun ComboCheckBoxField(item: PrefItem, onValueChange: (JsonElement?) -> Unit) {
    val selected = item.effectiveValue().stringsOrEmpty()
    Column(modifier = Modifier.fillMaxWidth()) {
        item.entries.forEachIndexed { entryIndex, entry ->
            val rawValue = item.entryValues.getOrNull(entryIndex) ?: entry
            val checked = rawValue in selected
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable {
                        val next = if (checked) selected - rawValue else selected + rawValue
                        onValueChange(JsonArray(next.map { JsonPrimitive(it) }))
                    }
                    .padding(vertical = 2.dp),
            ) {
                Checkbox(
                    checked = checked,
                    onCheckedChange = {
                        val next = if (checked) selected - rawValue else selected + rawValue
                        onValueChange(JsonArray(next.map { JsonPrimitive(it) }))
                    },
                    colors = CheckboxDefaults.colors(
                        checkedColor = RenzoColors.Primary,
                        uncheckedColor = RenzoColors.MutedForeground,
                        checkmarkColor = RenzoColors.PrimaryForeground,
                    ),
                )
                Text(
                    entry,
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.padding(start = 4.dp),
                )
            }
        }
    }
}

@Composable
private fun TextBoxField(item: PrefItem, onValueChange: (JsonElement?) -> Unit) {
    val value = (item.effectiveValue() as? JsonPrimitive)?.contentOrNull ?: ""
    val shape = RoundedCornerShape(6.dp)
    Box(
        contentAlignment = Alignment.CenterStart,
        modifier = Modifier
            .fillMaxWidth()
            .height(36.dp)
            .clip(shape)
            .background(RenzoColors.Background)
            .border(1.dp, RenzoColors.Border, shape)
            .padding(horizontal = 12.dp),
    ) {
        if (value.isEmpty()) {
            Text(
                "Enter value",
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.MutedForeground,
            )
        }
        BasicTextField(
            value = value,
            onValueChange = { onValueChange(JsonPrimitive(it)) },
            singleLine = true,
            textStyle = MaterialTheme.typography.bodyMedium.copy(color = RenzoColors.Foreground),
            cursorBrush = SolidColor(RenzoColors.Primary),
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

@Composable
private fun SwitchField(item: PrefItem, onValueChange: (JsonElement?) -> Unit) {
    val checked = (item.effectiveValue() as? JsonPrimitive)?.booleanOrNull ?: false
    Row(verticalAlignment = Alignment.CenterVertically) {
        Switch(
            checked = checked,
            onCheckedChange = { onValueChange(JsonPrimitive(it)) },
            colors = SwitchDefaults.colors(
                checkedTrackColor = RenzoColors.Primary,
                checkedThumbColor = RenzoColors.Background,
                uncheckedTrackColor = RenzoColors.Secondary,
                uncheckedThumbColor = RenzoColors.Background,
                uncheckedBorderColor = RenzoColors.Border,
            ),
        )
        Text(
            if (checked) "Enabled" else "Disabled",
            style = MaterialTheme.typography.bodyMedium,
            color = RenzoColors.Foreground,
            modifier = Modifier.padding(start = 8.dp),
        )
    }
}

@Composable
private fun DialogButton(
    label: String,
    primary: Boolean,
    enabled: Boolean,
    loading: Boolean = false,
    onClick: () -> Unit,
) {
    val shape = RoundedCornerShape(6.dp)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .height(36.dp)
            .clip(shape)
            .background(if (primary) RenzoColors.Primary else RenzoColors.Background)
            .then(
                if (primary) Modifier else Modifier.border(1.dp, RenzoColors.Border, shape),
            )
            .clickable(enabled = enabled) { onClick() }
            .alpha(if (enabled) 1f else 0.5f)
            .padding(horizontal = 16.dp),
    ) {
        if (loading) {
            CircularProgressIndicator(
                strokeWidth = 2.dp,
                color = if (primary) RenzoColors.PrimaryForeground else RenzoColors.Foreground,
                modifier = Modifier.padding(end = 8.dp).size(14.dp),
            )
        }
        Text(
            label,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            color = if (primary) RenzoColors.PrimaryForeground else RenzoColors.Foreground,
        )
    }
}
