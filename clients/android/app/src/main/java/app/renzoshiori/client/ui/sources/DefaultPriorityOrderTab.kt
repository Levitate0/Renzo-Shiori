package app.renzoshiori.client.ui.sources

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.DragIndicator
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.TextUnitType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.data.model.UpdatePreferencesRequestDto
import app.renzoshiori.client.data.network.SourcesApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlin.math.roundToInt

private val prefsJson = Json { ignoreUnknownKeys = true }

private const val KEY_DEFAULT_ORDER = "defaultSourcePriorityOrder"
private const val KEY_REDOWNLOAD = "redownloadFromHigherPrioritySources"

private fun parsePrefsObject(json: String?): JsonObject? {
    if (json.isNullOrBlank()) return null
    return runCatching { prefsJson.parseToJsonElement(json) as? JsonObject }.getOrNull()
}

private fun parseDefaultOrder(json: String?): List<String> {
    val arr = parsePrefsObject(json)?.get(KEY_DEFAULT_ORDER) as? JsonArray ?: return emptyList()
    return arr.mapNotNull { (it as? JsonPrimitive)?.contentOrNull }
}

private fun parseRedownloadEnabled(json: String?): Boolean =
    (parsePrefsObject(json)?.get(KEY_REDOWNLOAD) as? JsonPrimitive)?.booleanOrNull ?: false

/**
 * Round-trips the FULL existing preferences blob and only overrides the one key
 * this feature owns, so saving a priority change can never clobber theme (or
 * any other) keys already in there — the same unknown-keys-preserved contract
 * lib/utils/priority-prefs.ts follows.
 */
private fun mergePriorityPrefs(existingJson: String?, order: List<String>): String {
    val base = parsePrefsObject(existingJson)
    val merged = buildJsonObject {
        base?.forEach { (k, v) -> if (k != KEY_DEFAULT_ORDER) put(k, v) }
        put(KEY_DEFAULT_ORDER, JsonArray(order.map { JsonPrimitive(it) }))
    }
    return merged.toString()
}

/**
 * Builds the editable working list: the saved default order, filtered down to
 * providers that are still installed-for-me, followed by any installed provider
 * not yet in that order (alphabetical), so newly installed sources always show
 * up instead of silently being left out of the ranking.
 */
private fun buildWorkingOrder(saved: List<String>, installedNames: List<String>): List<String> {
    val installedSet = installedNames.toSet()
    val kept = saved.filter { it in installedSet }
    val keptSet = kept.toSet()
    val rest = installedNames.filter { it !in keptSet }.sortedWith(String.CASE_INSENSITIVE_ORDER)
    return kept + rest
}

/**
 * Per-user "Default priority order" — a one-time-setup ranking of installed
 * sources by display name. Lives on the ACCOUNT (same preferences blob as
 * theme), not instance-wide settings — every user ranks their own sources.
 *
 * Reordering is purely local until Apply is pressed (the same "buffer, then
 * commit" model as the per-series order). Apply only saves the ranking; it does
 * NOT touch any series. "Apply to All Series" is the separate, bigger action
 * that pushes this ranking onto every series you own AND turns on the
 * redownload-on-upgrade system for you.
 */
@Composable
internal fun DefaultPriorityOrderTab(api: SourcesApi?, snackbar: SnackbarHostState) {
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current

    var preferences by remember { mutableStateOf<String?>(null) }
    var installedNames by remember { mutableStateOf<List<String>>(emptyList()) }
    var loaded by remember { mutableStateOf(false) }
    var saving by remember { mutableStateOf(false) }
    var applyingToAll by remember { mutableStateOf(false) }

    val order = remember { mutableStateListOf<String>() }

    val savedOrder = parseDefaultOrder(preferences)
    val isConfigured = savedOrder.isNotEmpty()
    val redownloadEnabled = parseRedownloadEnabled(preferences)

    suspend fun loadAll() {
        preferences = runCatching { api?.me()?.preferences }.getOrNull()
        runCatching { api?.providers() ?: emptyList() }.getOrNull()?.let { list ->
            installedNames = list.filter { it.isEnabledForMe }.map { it.name }.distinct()
        }
    }

    LaunchedEffect(Unit) {
        loadAll()
        loaded = true
    }

    // Seed (and re-seed if the installed source set changes) without clobbering
    // in-progress reordering the user hasn't applied yet.
    LaunchedEffect(installedNames, savedOrder.joinToString("|"), isConfigured, loaded) {
        if (!loaded) return@LaunchedEffect
        val seed = if (isConfigured) savedOrder else installedNames.sortedWith(String.CASE_INSENSITIVE_ORDER)
        val next = buildWorkingOrder(seed, installedNames)
        if (order.isEmpty()) {
            order.addAll(next)
        } else {
            val prevSet = order.toSet()
            val additions = next.filter { it !in prevSet }
            val stillInstalled = order.filter { it in installedNames }
            order.clear()
            order.addAll(stillInstalled + additions)
        }
    }

    val dirty = if (isConfigured) {
        val committed = savedOrder.filter { it in installedNames } + order.filter { it !in savedOrder }
        order.toList() != committed
    } else {
        order.isNotEmpty()
    }

    fun move(index: Int, up: Boolean) {
        val swap = if (up) index - 1 else index + 1
        if (swap < 0 || swap >= order.size) return
        val tmp = order[index]
        order[index] = order[swap]
        order[swap] = tmp
    }

    fun handleApply() {
        scope.launch {
            saving = true
            val user = runCatching {
                api?.updateMe(UpdatePreferencesRequestDto(mergePriorityPrefs(preferences, order.toList())))
            }.getOrNull()
            if (user == null) {
                snackbar.showSnackbar("Failed to save default priority order")
            } else {
                preferences = user.preferences
                snackbar.showSnackbar("Default priority order saved")
            }
            saving = false
        }
    }

    fun handleReset() {
        val next = if (isConfigured) buildWorkingOrder(savedOrder, installedNames) else emptyList()
        order.clear()
        order.addAll(next)
    }

    fun handleApplyToAll() {
        if (dirty) {
            scope.launch {
                snackbar.showSnackbar("Save your order first — press Apply above before applying it to your library.")
            }
            return
        }
        scope.launch {
            applyingToAll = true
            val result = runCatching { api?.applyDefaultPriorityToAll() }.getOrNull()
            when {
                result == null -> snackbar.showSnackbar("Failed to apply to all series")
                !result.success -> snackbar.showSnackbar(
                    "Nothing to apply — " + (result.error ?: "Set up an order above first."),
                )
                else -> {
                    preferences = runCatching { api?.me()?.preferences }.getOrNull() ?: preferences
                    snackbar.showSnackbar(
                        buildString {
                            append("Applied to your library: ")
                            append("${result.seriesReordered} of ${result.seriesConsidered} series reordered")
                            if (result.seriesAdopted > 0) append(", ${result.seriesAdopted} series adopted")
                            if (result.chaptersQueued > 0) append(", ${result.chaptersQueued} chapter re-download(s) queued")
                            append(". Redownload-on-upgrade is now on for you.")
                        },
                    )
                }
            }
            applyingToAll = false
        }
    }

    // ── Drag-to-reorder state (dnd-kit's PointerSensor equivalent) ───────────
    val rowHeight = 44.dp
    val rowHeightPx = with(density) { rowHeight.toPx() }
    var draggingIndex by remember { mutableStateOf<Int?>(null) }
    var dragOffsetY by remember { mutableStateOf(0f) }

    Column(modifier = Modifier.fillMaxSize()) {
        Column(
            verticalArrangement = Arrangement.spacedBy(16.dp),
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp)
                .padding(top = 8.dp, bottom = 16.dp),
        ) {
            // ── Accent callout ────────────────────────────────────────────────
            PriorityCallout(isConfigured = isConfigured, redownloadEnabled = redownloadEnabled)

            if (order.isEmpty()) {
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(RenzoColors.Card.copy(alpha = 0.50f))
                        .border(1.dp, RenzoColors.Border.copy(alpha = 0.60f), RoundedCornerShape(12.dp))
                        .padding(32.dp),
                ) {
                    Text(
                        "No installed sources to rank yet — install some sources first.",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                    )
                }
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    order.forEachIndexed { index, name ->
                        SortableProviderRow(
                            name = name,
                            index = index,
                            total = order.size,
                            height = rowHeight,
                            isDragging = draggingIndex == index,
                            dragOffsetY = if (draggingIndex == index) dragOffsetY else 0f,
                            onMove = { i, up -> move(i, up) },
                            dragModifier = Modifier.pointerInput(order.size) {
                                detectDragGestures(
                                    onDragStart = {
                                        draggingIndex = index
                                        dragOffsetY = 0f
                                    },
                                    onDragEnd = {
                                        draggingIndex = null
                                        dragOffsetY = 0f
                                    },
                                    onDragCancel = {
                                        draggingIndex = null
                                        dragOffsetY = 0f
                                    },
                                ) { change, amount ->
                                    change.consume()
                                    val current = draggingIndex ?: return@detectDragGestures
                                    dragOffsetY += amount.y
                                    // Row spacing is 8dp; swap once the row has
                                    // travelled a full slot.
                                    val slot = rowHeightPx + with(density) { 8.dp.toPx() }
                                    val shift = (dragOffsetY / slot).roundToInt()
                                    if (shift != 0) {
                                        val target = (current + shift).coerceIn(0, order.size - 1)
                                        if (target != current) {
                                            val item = order.removeAt(current)
                                            order.add(target, item)
                                            draggingIndex = target
                                            dragOffsetY -= (target - current) * slot
                                        }
                                    }
                                }
                            },
                        )
                    }
                }
            }
        }

        // ── Sticky apply bar ─────────────────────────────────────────────────
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(RenzoColors.Background)
                .border(1.dp, RenzoColors.Border.copy(alpha = 0.60f), RoundedCornerShape(8.dp))
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(
                if (dirty) "Unsaved changes — press Apply to save." else "No unsaved changes.",
                fontSize = 12.sp,
                color = RenzoColors.MutedForeground,
            )
            Row(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                OutlineActionButton(
                    label = "Reset",
                    icon = Icons.Filled.Refresh,
                    enabled = dirty,
                    onClick = { handleReset() },
                )
                OutlineActionButton(
                    label = "Apply",
                    icon = null,
                    loading = saving,
                    enabled = dirty && !saving,
                    onClick = { handleApply() },
                )
                PrimaryActionButton(
                    label = "Apply to All Series",
                    loading = applyingToAll,
                    enabled = isConfigured && !applyingToAll,
                    onClick = { handleApplyToAll() },
                )
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun PriorityCallout(isConfigured: Boolean, redownloadEnabled: Boolean) {
    val shape = RoundedCornerShape(12.dp)
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(
                if (isConfigured) RenzoColors.Primary.copy(alpha = 0.06f)
                else RenzoColors.Primary.copy(alpha = 0.15f),
            )
            .border(
                if (isConfigured) 1.dp else 2.dp,
                if (isConfigured) RenzoColors.Primary.copy(alpha = 0.30f) else RenzoColors.Primary,
                shape,
            )
            .padding(16.dp),
    ) {
        Icon(
            Icons.Filled.AutoAwesome,
            contentDescription = null,
            tint = if (isConfigured) RenzoColors.Primary.copy(alpha = 0.80f) else RenzoColors.Primary,
            modifier = Modifier.padding(top = 2.dp).size(20.dp),
        )
        Column(modifier = Modifier.padding(start = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(
                if (isConfigured) "Default priority order is set up" else "HIGHLY RECOMMENDED TO BE SET UP",
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = if (isConfigured) FontWeight.SemiBold else FontWeight.Bold,
                letterSpacing = if (isConfigured) TextUnit.Unspecified else TextUnit(0.5f, TextUnitType.Sp),
                color = if (isConfigured) RenzoColors.Primary.copy(alpha = 0.90f) else RenzoColors.Primary,
            )
            Text(
                "Rank your sources once, here — it's just for you, not shared with other " +
                    "accounts — and every new series you add starts with this priority " +
                    "automatically. It's also available as “Revert to Default” on any existing " +
                    "series. Pressing Apply below only saves this ranking; nothing changes on " +
                    "any series until you also use Apply to All.",
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
            )
            if (redownloadEnabled) {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
                    Icon(
                        Icons.Filled.CheckCircle,
                        contentDescription = null,
                        tint = RenzoColors.Primary.copy(alpha = 0.90f),
                        modifier = Modifier.size(14.dp),
                    )
                    Text(
                        "Redownload-on-upgrade is currently ON for your series.",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium,
                        color = RenzoColors.Primary.copy(alpha = 0.90f),
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun SortableProviderRow(
    name: String,
    index: Int,
    total: Int,
    height: Dp,
    isDragging: Boolean,
    dragOffsetY: Float,
    onMove: (Int, Boolean) -> Unit,
    dragModifier: Modifier,
) {
    val shape = RoundedCornerShape(8.dp)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .height(height)
            .offset { IntOffset(0, dragOffsetY.roundToInt()) }
            .clip(shape)
            .background(RenzoColors.Card)
            .border(1.dp, RenzoColors.Border.copy(alpha = 0.60f), shape)
            .alpha(if (isDragging) 0.5f else 1f)
            .padding(horizontal = 12.dp),
    ) {
        Icon(
            Icons.Filled.DragIndicator,
            contentDescription = "Drag to reorder $name",
            tint = RenzoColors.MutedForeground.copy(alpha = 0.70f),
            modifier = Modifier.size(16.dp).then(dragModifier),
        )
        Text(
            (index + 1).toString(),
            fontSize = 12.sp,
            color = RenzoColors.MutedForeground.copy(alpha = 0.70f),
            modifier = Modifier.padding(start = 8.dp).width(20.dp),
        )
        Text(
            name,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.Foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 4.dp).weight(1f),
        )
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .size(20.dp)
                    .clip(RoundedCornerShape(4.dp))
                    .clickable(enabled = index > 0) { onMove(index, true) }
                    .alpha(if (index > 0) 1f else 0.3f),
            ) {
                Icon(
                    Icons.Filled.KeyboardArrowUp,
                    contentDescription = "Move $name up",
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
            }
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .size(20.dp)
                    .clip(RoundedCornerShape(4.dp))
                    .clickable(enabled = index < total - 1) { onMove(index, false) }
                    .alpha(if (index < total - 1) 1f else 0.3f),
            ) {
                Icon(
                    Icons.Filled.KeyboardArrowDown,
                    contentDescription = "Move $name down",
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
            }
        }
    }
}

/** shadcn `<Button variant="outline" size="sm">`. */
@Composable
private fun OutlineActionButton(
    label: String,
    icon: ImageVector?,
    enabled: Boolean,
    loading: Boolean = false,
    onClick: () -> Unit,
) {
    val shape = RoundedCornerShape(6.dp)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .height(32.dp)
            .clip(shape)
            .background(RenzoColors.Background)
            .border(1.dp, RenzoColors.Border, shape)
            .clickable(enabled = enabled) { onClick() }
            .alpha(if (enabled) 1f else 0.5f)
            .padding(horizontal = 12.dp),
    ) {
        when {
            loading -> CircularProgressIndicator(
                strokeWidth = 2.dp,
                color = RenzoColors.Foreground,
                modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
            icon != null -> Icon(
                icon,
                contentDescription = null,
                tint = RenzoColors.Foreground,
                modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(
            label,
            fontSize = 12.sp,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.Foreground,
        )
    }
}

/** shadcn `<Button size="sm">` (default/primary variant). */
@Composable
private fun PrimaryActionButton(
    label: String,
    enabled: Boolean,
    loading: Boolean = false,
    onClick: () -> Unit,
) {
    val shape = RoundedCornerShape(6.dp)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .height(32.dp)
            .clip(shape)
            .background(RenzoColors.Primary)
            .clickable(enabled = enabled) { onClick() }
            .alpha(if (enabled) 1f else 0.5f)
            .padding(horizontal = 12.dp),
    ) {
        if (loading) {
            CircularProgressIndicator(
                strokeWidth = 2.dp,
                color = RenzoColors.PrimaryForeground,
                modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(
            label,
            fontSize = 12.sp,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.PrimaryForeground,
        )
    }
}
