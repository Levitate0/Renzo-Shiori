package app.renzoshiori.client.ui.browse

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.LinkedSeriesRowDto
import app.renzoshiori.client.data.model.SearchSourceDto
import app.renzoshiori.client.data.network.BrowseApi
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.components.TvSearchBar
import app.renzoshiori.client.ui.components.tvFocusTarget
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable
import app.renzoshiori.client.ui.tv.tvContentColor
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import app.renzoshiori.client.ui.theme.RenzoColors

private val looseJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }

/**
 * Add Series — the two-stage flow from
 * RenzoFrontend src/components/comp/series/add-series (STAGE 01 / SEARCH →
 * STAGE 02 / CONFIRM), transliterated as a full-screen sheet.
 *
 * Stage 1 searches every enabled source (3-character minimum, 800ms debounce),
 * lets the user tick the matching entries and shows the same "✓ added" tail.
 * Stage 2 augments the picks and exposes the per-source Storage / Cover /
 * Title / Status switches plus the selection checkbox, then POSTs the whole
 * augmented payload back verbatim.
 */
@Composable
fun AddSeriesSheet(
    initialTitle: String?,
    canAddSeries: Boolean,
    onDismiss: () -> Unit,
    onAdded: () -> Unit,
) {
    val context = LocalContext.current
    val renzoApp = context.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    val api = remember { renzoApp.network.currentServiceOf<BrowseApi>() }
    val baseUrl = renzoApp.tokenStore.serverUrl ?: ""
    val isTv = LocalIsTv.current

    var stage by remember { mutableStateOf(0) }
    var searchValue by remember { mutableStateOf(initialTitle ?: "") }
    var debounced by remember { mutableStateOf(initialTitle ?: "") }
    var searching by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var pending by remember { mutableStateOf(false) }

    var sources by remember { mutableStateOf<List<SearchSourceDto>>(emptyList()) }
    val selectedSources = remember { mutableStateListOf<String>() }
    var sourcesExpanded by remember { mutableStateOf(false) }

    // Raw search rows, kept verbatim so augment gets exactly what the API sent.
    var rawResults by remember { mutableStateOf<JsonArray?>(null) }
    var results by remember { mutableStateOf<List<LinkedSeriesRowDto>>(emptyList()) }
    val selectedRows = remember { mutableStateListOf<String>() }

    // Stage 2 state — the augmented payload plus the per-source toggles.
    var augmented by remember { mutableStateOf<JsonObject?>(null) }
    var confirmRows by remember { mutableStateOf<List<ConfirmRow>>(emptyList()) }

    // Which sources to search, remembered between visits — the web keeps the
    // same choice in localStorage. Preselecting EVERY source (what this did
    // before) fans a single keystroke out across every installed extension;
    // that takes long enough that a reverse proxy in front of the server
    // returns 502 before the search finishes.
    LaunchedEffect(Unit) {
        runCatching { api?.searchSources() }.getOrNull()?.let { list ->
            sources = list.sortedBy { it.provider.lowercase() }
            val available = list.mapNotNull { it.mihonProviderId.takeIf(String::isNotBlank) }.toSet()
            selectedSources.clear()
            selectedSources.addAll(loadSearchSources(context).filter { it in available })
        }
    }
    LaunchedEffect(selectedSources.size, selectedSources.toList()) {
        if (sources.isNotEmpty()) saveSearchSources(context, selectedSources.toList())
    }

    LaunchedEffect(searchValue) {
        delay(800)
        debounced = searchValue
    }

    LaunchedEffect(debounced, selectedSources.size) {
        if (debounced.trim().length < 3 || selectedSources.isEmpty()) {
            results = emptyList()
            rawResults = null
            return@LaunchedEffect
        }
        searching = true
        error = null
        runCatching {
            api?.searchRaw(debounced.trim(), selectedSources.toList())
        }
            .onSuccess { arr ->
                rawResults = arr
                results = arr?.mapNotNull { element ->
                    runCatching { looseJson.decodeFromJsonElement(LinkedSeriesRowDto.serializer(), element) }.getOrNull()
                }.orEmpty()
                val valid = results.map { it.rowId }.toSet()
                selectedRows.retainAll { it in valid }
            }
            .onFailure { cause ->
                val code = (cause as? retrofit2.HttpException)?.code()
                error = when {
                    code == 502 || code == 504 ->
                        "The search took too long and the connection timed out. " +
                            "Try fewer sources, or a longer keyword."
                    code != null -> "Search failed (HTTP $code)."
                    cause is java.net.SocketTimeoutException ->
                        "The sources are taking too long to answer. Try fewer of them."
                    cause is java.io.IOException -> "Can't reach the server."
                    else -> cause.message ?: "Search failed."
                }
            }
        searching = false
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(RenzoColors.Background),
        ) {
            // ── Stage label + close ──────────────────────────────────────
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 16.dp, bottom = 8.dp),
            ) {
                Text(
                    if (stage == 0) "STAGE 01 / SEARCH" else "STAGE 02 / CONFIRM",
                    style = MaterialTheme.typography.labelSmall.copy(fontSize = 11.sp, fontWeight = FontWeight.SemiBold),
                    color = RenzoColors.MutedForeground,
                    letterSpacing = 1.6.sp,
                    modifier = Modifier.weight(1f),
                )
                val closeFocus = rememberFocusState()
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .size(if (isTv) 44.dp else 32.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .then(
                            if (isTv) {
                                Modifier
                                    .focusRing(closeFocus.focused, 8.dp)
                                    .tvClickable(onFocused = closeFocus::set, onClick = onDismiss)
                            } else {
                                Modifier.clickable(onClick = onDismiss)
                            },
                        ),
                ) {
                    Icon(
                        Icons.Filled.Close,
                        contentDescription = "Close",
                        tint = if (closeFocus.focused) RenzoColors.Foreground else RenzoColors.MutedForeground,
                        modifier = Modifier.size(if (isTv) 22.dp else 16.dp),
                    )
                }
            }
            HorizontalDivider(color = RenzoColors.Border)

            Box(modifier = Modifier.fillMaxWidth().weight(1f)) {
                if (stage == 0) {
                    SearchStage(
                        searchValue = searchValue,
                        onSearchValue = { searchValue = it },
                        // IME Search / a voice result skips the 800ms debounce —
                        // the user has explicitly said "go".
                        onSubmitSearch = {
                            searchValue = it
                            debounced = it
                        },
                        searching = searching,
                        sources = sources,
                        selectedSources = selectedSources,
                        sourcesExpanded = sourcesExpanded,
                        onToggleSourcesExpanded = { sourcesExpanded = !sourcesExpanded },
                        results = results,
                        selectedRows = selectedRows,
                        baseUrl = baseUrl,
                    )
                } else {
                    ConfirmStage(
                        rows = confirmRows,
                        onRows = { confirmRows = it },
                        baseUrl = baseUrl,
                    )
                }
            }

            // ── Error banner ─────────────────────────────────────────────
            val err = error
            if (err != null) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 8.dp)
                        .clip(RoundedCornerShape(6.dp))
                        .border(1.dp, RenzoColors.Red.copy(alpha = 0.25f), RoundedCornerShape(6.dp))
                        .background(Color.White.copy(alpha = 0.04f))
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                ) {
                    Icon(
                        Icons.Filled.Warning,
                        contentDescription = null,
                        tint = RenzoColors.Red,
                        modifier = Modifier.size(14.dp),
                    )
                    Text(
                        err,
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.Red,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
            }

            // ── CTA row ──────────────────────────────────────────────────
            HorizontalDivider(color = RenzoColors.Border)
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
            ) {
                Text(
                    if (stage == 0) {
                        "${results.size} results · ${selectedRows.size} selected"
                    } else {
                        val sel = confirmRows.filter { it.isSelected }
                        "${sel.size} sources · ${sel.sumOf { it.chapterCount }} chapters"
                    },
                    style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.weight(1f),
                )
                val backFocus = rememberFocusState()
                if (stage > 0) {
                    Text(
                        "Back",
                        style = MaterialTheme.typography.labelLarge,
                        color = if (backFocus.focused) RenzoColors.Foreground else RenzoColors.MutedForeground,
                        modifier = Modifier
                            .clip(RoundedCornerShape(8.dp))
                            .then(
                                if (isTv) {
                                    Modifier
                                        .focusRing(backFocus.focused, 8.dp)
                                        .tvClickable(onFocused = backFocus::set, onClick = { stage = 0 })
                                } else {
                                    Modifier.clickable { stage = 0 }
                                },
                            )
                            .padding(horizontal = 12.dp, vertical = 8.dp),
                    )
                }
                val canProgress = if (stage == 0) selectedRows.isNotEmpty() else confirmRows.any { it.isSelected }
                val ctaFocus = rememberFocusState()
                if (canProgress && !pending) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .padding(start = 8.dp)
                            .height(if (isTv) 44.dp else 36.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .background(RenzoColors.Primary)
                            // The CTA's onClick is a long coroutine block, so it
                            // keeps its clickable (already D-pad activatable) and
                            // only gains the ring + focus reporting.
                            .then(
                                if (isTv) {
                                    Modifier
                                        .focusRing(ctaFocus.focused, 8.dp)
                                        .onFocusChanged { ctaFocus.set(it.isFocused) }
                                } else {
                                    Modifier
                                },
                            )
                            .clickable {
                                scope.launch {
                                    pending = true
                                    error = null
                                    if (stage == 0) {
                                        val picks = buildJsonArrayOfSelected(rawResults, selectedRows.toSet())
                                        val response = runCatching { api?.augment(picks) }.getOrNull()
                                        if (response == null) {
                                            error = "Failed to load series details."
                                        } else {
                                            val rows = buildConfirmRows(response)
                                            if (rows.isEmpty()) {
                                                error = droppedMessage(response)
                                            } else {
                                                augmented = response
                                                confirmRows = rows
                                                stage = 1
                                            }
                                        }
                                    } else {
                                        val payload = augmented?.let { buildSubmitPayload(it, confirmRows) }
                                        if (payload == null) {
                                            error = "Original augmented response not found"
                                        } else {
                                            runCatching { api?.addSeries(payload) }
                                                .onSuccess { onAdded() }
                                                .onFailure { error = it.message ?: "Failed to add series." }
                                        }
                                    }
                                    pending = false
                                }
                            }
                            .padding(horizontal = 16.dp),
                    ) {
                        Icon(
                            if (stage == 0) Icons.Filled.Check else Icons.Filled.Add,
                            contentDescription = null,
                            tint = RenzoColors.PrimaryForeground,
                            modifier = Modifier.size(14.dp),
                        )
                        Text(
                            if (stage == 0) "Next" else if (canAddSeries) "Add Series" else "Request Series",
                            style = MaterialTheme.typography.labelLarge,
                            color = RenzoColors.PrimaryForeground,
                            modifier = Modifier.padding(start = 6.dp),
                        )
                    }
                } else if (pending) {
                    CircularProgressIndicator(
                        color = RenzoColors.Primary,
                        strokeWidth = 2.dp,
                        modifier = Modifier.padding(start = 12.dp).size(20.dp),
                    )
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Stage 1
// ---------------------------------------------------------------------------

@Composable
private fun SearchStage(
    searchValue: String,
    onSearchValue: (String) -> Unit,
    onSubmitSearch: (String) -> Unit,
    searching: Boolean,
    sources: List<SearchSourceDto>,
    selectedSources: MutableList<String>,
    sourcesExpanded: Boolean,
    onToggleSourcesExpanded: () -> Unit,
    results: List<LinkedSeriesRowDto>,
    selectedRows: MutableList<String>,
    baseUrl: String,
) {
    val isTv = LocalIsTv.current
    Column(modifier = Modifier.fillMaxSize()) {
        // Search input row. On TV this becomes the bordered field + mic (voice
        // fills the field and searches, but the transcript stays editable — a
        // romanised title comes back mangled often enough that the user has to
        // be able to fix it in place).
        if (isTv) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Box(modifier = Modifier.weight(1f)) {
                    TvSearchBar(
                        value = searchValue,
                        onValueChange = onSearchValue,
                        onSubmit = onSubmitSearch,
                        placeholder = "Search for a series…",
                        voicePrompt = "Say the series title",
                    )
                }
                if (searching) {
                    CircularProgressIndicator(
                        color = RenzoColors.MutedForeground,
                        strokeWidth = 2.dp,
                        modifier = Modifier.padding(end = 16.dp).size(20.dp),
                    )
                }
            }
        } else {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            ) {
                Icon(
                    Icons.Filled.Search,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(22.dp),
                )
                BasicTextField(
                    value = searchValue,
                    onValueChange = onSearchValue,
                    singleLine = true,
                    textStyle = MaterialTheme.typography.bodyLarge.copy(color = RenzoColors.Foreground),
                    cursorBrush = SolidColor(RenzoColors.Foreground),
                    decorationBox = { inner ->
                        Box(modifier = Modifier.padding(start = 12.dp)) {
                            if (searchValue.isEmpty()) {
                                Text(
                                    "Search for a series…",
                                    style = MaterialTheme.typography.bodyLarge,
                                    color = RenzoColors.MutedForeground,
                                )
                            }
                            inner()
                        }
                    },
                    modifier = Modifier.weight(1f),
                )
                if (searching) {
                    CircularProgressIndicator(
                        color = RenzoColors.MutedForeground,
                        strokeWidth = 1.5.dp,
                        modifier = Modifier.size(16.dp),
                    )
                }
            }
        }

        // Sources selector — its own row, as in the web.
        if (sources.isNotEmpty()) {
            val expandFocus = rememberFocusState()
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .then(
                        if (isTv) {
                            Modifier
                                .padding(horizontal = 12.dp)
                                .tvFocusTarget(
                                    focused = expandFocus.focused,
                                    onFocused = expandFocus::set,
                                    radius = 8.dp,
                                    fill = RenzoColors.Card,
                                    onClick = onToggleSourcesExpanded,
                                )
                        } else {
                            Modifier.clickable(onClick = onToggleSourcesExpanded)
                        },
                    )
                    .padding(horizontal = 16.dp, vertical = 8.dp),
            ) {
                Text(
                    "Sources: ${selectedSources.size}/${sources.size}",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.weight(1f),
                )
                Text(
                    if (sourcesExpanded) "Hide" else "Choose…",
                    style = MaterialTheme.typography.labelMedium,
                    color = RenzoColors.Primary,
                )
            }
            if (sourcesExpanded) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = if (isTv) 320.dp else 220.dp)
                        .padding(horizontal = 16.dp),
                ) {
                    // Select all / none. Note that selecting everything makes
                    // each search query every installed extension live, which
                    // is slow enough to trip a proxy's gateway timeout — hence
                    // the count next to it rather than a silent toggle.
                    val allSelected = selectedSources.size == sources.size && sources.isNotEmpty()
                    val allFocus = rememberFocusState()
                    val toggleAll = {
                        if (allSelected) {
                            selectedSources.clear()
                        } else {
                            selectedSources.clear()
                            selectedSources.addAll(
                                sources.mapNotNull { it.mihonProviderId.takeIf(String::isNotBlank) },
                            )
                        }
                        Unit
                    }
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .fillMaxWidth()
                            .then(
                                if (isTv) {
                                    Modifier.tvFocusTarget(
                                        focused = allFocus.focused,
                                        onFocused = allFocus::set,
                                        radius = 8.dp,
                                        fill = RenzoColors.Card,
                                        onClick = toggleAll,
                                    )
                                } else {
                                    Modifier.clickable(onClick = toggleAll)
                                },
                            )
                            .padding(horizontal = if (isTv) 8.dp else 0.dp, vertical = if (isTv) 10.dp else 6.dp),
                    ) {
                        CheckBox(allSelected)
                        Text(
                            if (allSelected) "Select none" else "Select all",
                            style = MaterialTheme.typography.bodyMedium,
                            color = RenzoColors.Primary,
                            modifier = Modifier.padding(start = 8.dp).weight(1f),
                        )
                        Text(
                            "${selectedSources.size}/${sources.size}",
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                        )
                    }
                    HorizontalDivider(color = RenzoColors.Border)
                    LazyColumn {
                        items(sources, key = { it.mihonProviderId }) { source ->
                            val checked = selectedSources.contains(source.mihonProviderId)
                            val sourceFocus = rememberFocusState()
                            val toggleSource = {
                                if (checked) {
                                    selectedSources.remove(source.mihonProviderId)
                                } else {
                                    selectedSources.add(source.mihonProviderId)
                                }
                                Unit
                            }
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .then(
                                        if (isTv) {
                                            // Ticked (colour + box) is state and
                                            // survives the cursor moving on; the
                                            // ring is only ever focus.
                                            Modifier.tvFocusTarget(
                                                focused = sourceFocus.focused,
                                                onFocused = sourceFocus::set,
                                                radius = 8.dp,
                                                fill = RenzoColors.Card,
                                                onClick = toggleSource,
                                            )
                                        } else {
                                            Modifier.clickable(onClick = toggleSource)
                                        },
                                    )
                                    .padding(
                                        horizontal = if (isTv) 8.dp else 0.dp,
                                        vertical = if (isTv) 10.dp else 6.dp,
                                    ),
                            ) {
                                CheckBox(checked)
                                Text(
                                    source.provider,
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = if (isTv) {
                                        tvContentColor(checked, sourceFocus.focused)
                                    } else {
                                        RenzoColors.Foreground
                                    },
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                    modifier = Modifier.padding(start = 8.dp).weight(1f),
                                )
                                Text(
                                    source.language.uppercase(),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = RenzoColors.MutedForeground,
                                )
                            }
                        }
                    }
                }
            }
        }

        HorizontalDivider(color = RenzoColors.Border)

        // Results.
        when {
            selectedSources.isEmpty() -> CenteredNote("Pick at least one source to search")
            searchValue.isEmpty() -> CenteredNote("Start typing to search…")
            results.isEmpty() && !searching -> CenteredNote(
                if (searchValue.trim().length < 3) {
                    "Keep typing — search starts at 3 characters"
                } else {
                    "No results found"
                },
            )
            else -> LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(results, key = { it.rowId }) { series ->
                    val isSelected = selectedRows.contains(series.rowId)
                    val rowFocus = rememberFocusState()
                    val toggleRow = {
                        if (isSelected) {
                            selectedRows.remove(series.rowId)
                        } else {
                            selectedRows.add(series.rowId)
                            // First pick also brings in its linked ids.
                            if (selectedRows.size == 1) {
                                series.linkedIds.forEach { linked ->
                                    if (!selectedRows.contains(linked)) selectedRows.add(linked)
                                }
                            }
                        }
                        Unit
                    }
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .fillMaxWidth()
                            // The primary wash is selection ("picked"); the ring
                            // is focus. A row can be both, and reads as both.
                            .background(if (isSelected) RenzoColors.Primary.copy(alpha = 0.08f) else Color.Transparent)
                            .then(
                                if (isTv) {
                                    Modifier
                                        .focusRing(rowFocus.focused, 8.dp)
                                        .tvClickable(onFocused = rowFocus::set, onClick = toggleRow)
                                } else {
                                    Modifier.clickable(onClick = toggleRow)
                                },
                            )
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                    ) {
                        // Accent bar.
                        Box(
                            modifier = Modifier
                                .width(3.dp)
                                .height(48.dp)
                                .clip(RoundedCornerShape(2.dp))
                                .background(if (isSelected) RenzoColors.Primary else Color.Transparent),
                        )
                        Box(
                            modifier = Modifier
                                .padding(start = 10.dp)
                                .width(44.dp)
                                .height(62.dp)
                                .clip(RoundedCornerShape(6.dp))
                                .background(RenzoColors.Muted),
                        ) {
                            if (!series.thumbnailUrl.isNullOrBlank()) {
                                AsyncImage(
                                    model = absoluteUrl(baseUrl, series.thumbnailUrl),
                                    contentDescription = series.title,
                                    contentScale = ContentScale.Crop,
                                    modifier = Modifier.fillMaxSize(),
                                )
                            }
                        }
                        Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                            Text(
                                series.title,
                                style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                                color = RenzoColors.Foreground,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
                                Text(
                                    series.provider,
                                    style = MaterialTheme.typography.labelSmall,
                                    color = RenzoColors.MutedForeground,
                                    modifier = Modifier
                                        .clip(RoundedCornerShape(4.dp))
                                        .background(RenzoColors.Muted)
                                        .padding(horizontal = 6.dp, vertical = 2.dp),
                                )
                                Text(
                                    series.lang.uppercase(),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = RenzoColors.MutedForeground,
                                    modifier = Modifier.padding(start = 8.dp),
                                )
                            }
                        }
                        if (isSelected) {
                            Text(
                                "✓ added",
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.Primary,
                            )
                        }
                    }
                    HorizontalDivider(color = RenzoColors.Border.copy(alpha = 0.5f))
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Stage 2
// ---------------------------------------------------------------------------

/** One augmented source, with the four per-source switches the web exposes. */
data class ConfirmRow(
    val index: Int,
    val provider: String,
    val scanlator: String,
    val lang: String,
    val title: String,
    val thumbnailUrl: String?,
    val chapterCount: Int,
    val isSelected: Boolean,
    val isStorage: Boolean,
    val useCover: Boolean,
    val useTitle: Boolean,
    val useStatus: Boolean,
)

@Composable
private fun ConfirmStage(
    rows: List<ConfirmRow>,
    onRows: (List<ConfirmRow>) -> Unit,
    baseUrl: String,
) {
    val isTv = LocalIsTv.current
    LazyColumn(modifier = Modifier.fillMaxSize()) {
        items(rows, key = { it.index }) { row ->
            Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp)) {
                val rowFocus = rememberFocusState()
                val toggleRow = {
                    onRows(rows.map { if (it.index == row.index) it.copy(isSelected = !it.isSelected) else it })
                }
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    // TV: the whole row is the target — an 18dp checkbox is not
                    // something a D-pad can aim at.
                    modifier = if (isTv) {
                        Modifier
                            .fillMaxWidth()
                            .focusRing(rowFocus.focused, 8.dp)
                            .tvClickable(onFocused = rowFocus::set, onClick = toggleRow)
                            .padding(6.dp)
                    } else {
                        Modifier
                    },
                ) {
                    Box(
                        modifier = if (isTv) Modifier else Modifier.clickable(onClick = toggleRow),
                    ) {
                        CheckBox(row.isSelected)
                    }
                    Box(
                        modifier = Modifier
                            .padding(start = 10.dp)
                            .width(40.dp)
                            .height(56.dp)
                            .clip(RoundedCornerShape(6.dp))
                            .background(RenzoColors.Muted),
                    ) {
                        if (!row.thumbnailUrl.isNullOrBlank()) {
                            AsyncImage(
                                model = absoluteUrl(baseUrl, row.thumbnailUrl),
                                contentDescription = row.title,
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxSize(),
                            )
                        }
                    }
                    Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                        Text(
                            row.title,
                            style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                            color = RenzoColors.Foreground,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            listOfNotNull(
                                row.provider.takeIf { it.isNotBlank() },
                                row.scanlator.takeIf { it.isNotBlank() && it != row.provider },
                                row.lang.uppercase().takeIf { it.isNotBlank() },
                                "${row.chapterCount} chapters",
                            ).joinToString(" · "),
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(top = 4.dp),
                        )
                    }
                }
                if (row.isSelected) {
                    // Storage / Cover / Title / Status — mutually exclusive
                    // across sources, exactly like the web's confirm step.
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.fillMaxWidth().padding(start = 62.dp, top = 8.dp),
                    ) {
                        ToggleChip("Storage", row.isStorage) {
                            onRows(rows.map { it.copy(isStorage = it.index == row.index && !row.isStorage) })
                        }
                        ToggleChip("Cover", row.useCover) {
                            onRows(rows.map { it.copy(useCover = it.index == row.index && !row.useCover) })
                        }
                        ToggleChip("Title", row.useTitle) {
                            onRows(rows.map { it.copy(useTitle = it.index == row.index && !row.useTitle) })
                        }
                        ToggleChip("Status", row.useStatus) {
                            onRows(rows.map { it.copy(useStatus = it.index == row.index && !row.useStatus) })
                        }
                    }
                }
            }
            HorizontalDivider(color = RenzoColors.Border.copy(alpha = 0.5f))
        }
    }
}

// ---------------------------------------------------------------------------
// Small pieces
// ---------------------------------------------------------------------------

@Composable
private fun CheckBox(checked: Boolean) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(18.dp)
            .clip(RoundedCornerShape(4.dp))
            .border(
                1.dp,
                if (checked) RenzoColors.Primary else RenzoColors.Border,
                RoundedCornerShape(4.dp),
            )
            .background(if (checked) RenzoColors.Primary else Color.Transparent),
    ) {
        if (checked) {
            Icon(
                Icons.Filled.Check,
                contentDescription = null,
                tint = RenzoColors.PrimaryForeground,
                modifier = Modifier.size(12.dp),
            )
        }
    }
}

@Composable
private fun ToggleChip(label: String, active: Boolean, onClick: () -> Unit) {
    val isTv = LocalIsTv.current
    val focus = rememberFocusState()
    Text(
        label,
        style = MaterialTheme.typography.labelSmall,
        color = if (active) RenzoColors.PrimaryForeground else RenzoColors.MutedForeground,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .border(
                1.dp,
                if (active) RenzoColors.Primary else RenzoColors.Border,
                RoundedCornerShape(50),
            )
            // Filled = this source owns Storage/Cover/Title/Status (state);
            // the ring is focus. Both stay readable together.
            .background(if (active) RenzoColors.Primary else Color.Transparent)
            .then(
                if (isTv) {
                    Modifier
                        .focusRing(focus.focused, 50.dp)
                        .tvClickable(onFocused = focus::set, onClick = onClick)
                } else {
                    Modifier.clickable(onClick = onClick)
                },
            )
            .padding(horizontal = if (isTv) 14.dp else 10.dp, vertical = if (isTv) 8.dp else 4.dp),
    )
}

@Composable
private fun CenteredNote(text: String) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier.fillMaxWidth().fillMaxHeight().padding(24.dp),
    ) {
        Text(text, style = MaterialTheme.typography.bodyMedium, color = RenzoColors.MutedForeground)
    }
}

// ---------------------------------------------------------------------------
// JSON plumbing — the augment payload is round-tripped verbatim
// ---------------------------------------------------------------------------

private fun buildJsonArrayOfSelected(raw: JsonArray?, selected: Set<String>): JsonArray {
    val picks = raw.orEmpty().filter { element ->
        val obj = runCatching { element.jsonObject }.getOrNull() ?: return@filter false
        val id = runCatching { obj["mihonId"]?.jsonPrimitive?.content }.getOrNull()
            ?: runCatching { obj["providerId"]?.jsonPrimitive?.content }.getOrNull()
        id != null && selected.contains(id)
    }
    return JsonArray(picks)
}

/**
 * Web handleNext(): pick the first source whose language is in
 * preferredLanguages and default Storage/Cover/Title/Status to it. The confirm
 * step then guarantees at least one selected row.
 */
private fun buildConfirmRows(response: JsonObject): List<ConfirmRow> {
    val series = response["series"]?.let { runCatching { it.jsonArray }.getOrNull() } ?: return emptyList()
    if (series.isEmpty()) return emptyList()
    val preferred = response["preferredLanguages"]?.let { runCatching { it.jsonArray }.getOrNull() }
        ?.mapNotNull { runCatching { it.jsonPrimitive.content }.getOrNull() }
        .orEmpty()

    var preferredIndex = 0
    outer@ for (lang in preferred) {
        series.forEachIndexed { index, element ->
            val l = runCatching { element.jsonObject["lang"]?.jsonPrimitive?.content }.getOrNull()
            if (l != null && l.equals(lang, ignoreCase = true)) {
                preferredIndex = index
                break@outer
            }
        }
    }

    return series.mapIndexed { index, element ->
        val obj = element.jsonObject
        fun str(key: String): String =
            runCatching { obj[key]?.jsonPrimitive?.content }.getOrNull().orEmpty()
        ConfirmRow(
            index = index,
            provider = str("provider"),
            scanlator = str("scanlator"),
            lang = str("lang"),
            title = str("title"),
            thumbnailUrl = str("thumbnailUrl").takeIf { it.isNotBlank() },
            chapterCount = runCatching { obj["chapterCount"]?.jsonPrimitive?.content?.toDouble()?.toInt() }
                .getOrNull() ?: 0,
            // At least one row must be selected — default to every returned
            // source, with the preferred one owning storage/cover/title/status.
            isSelected = true,
            isStorage = index == preferredIndex,
            useCover = index == preferredIndex,
            useTitle = index == preferredIndex,
            useStatus = index == preferredIndex,
        )
    }
}

/** The backend tags each dropped source with a reason; explain it like the web. */
private fun droppedMessage(response: JsonObject): String {
    val dropped = response["droppedSeries"]?.let { runCatching { it.jsonArray }.getOrNull() }.orEmpty()
    val providers = dropped.mapNotNull {
        runCatching { it.jsonObject["provider"]?.jsonPrimitive?.content }.getOrNull()
    }.filter { it.isNotBlank() }.distinct()
    val providerList = if (providers.isEmpty()) "the selected source" else providers.joinToString(", ")
    val langs = response["preferredLanguages"]?.let { runCatching { it.jsonArray }.getOrNull() }
        ?.mapNotNull { runCatching { it.jsonPrimitive.content }.getOrNull() }
        ?.joinToString(", ")?.uppercase().orEmpty()
    val reasons = dropped.mapNotNull { runCatching { it.jsonObject["reason"]?.jsonPrimitive?.content }.getOrNull() }
    val hasNoChapters = reasons.contains("no-chapters")
    val hasUnreachable = reasons.contains("unreachable")
    return when {
        hasNoChapters && !hasUnreachable ->
            "No chapters available from $providerList" +
                (if (langs.isNotEmpty()) " in your enabled languages ($langs)" else "") +
                ". This title likely isn't translated in those languages — pick a different source, " +
                "or add the language in the source's settings."
        hasUnreachable && !hasNoChapters ->
            "Couldn't reach $providerList — it may be down or rate-limited. " +
                "Try again in a moment, or pick a different source."
        else ->
            "Couldn't load chapters for $providerList — either no chapters in your enabled languages" +
                (if (langs.isNotEmpty()) " ($langs)" else "") +
                ", or the source is down/rate-limited. Try again, or pick a different source."
    }
}

/**
 * Rebuilds the augmented response with only the selected sources and the
 * user's switch choices — every other field is carried over untouched, which
 * is why the payload stays raw JSON all the way through.
 */
private fun buildSubmitPayload(original: JsonObject, rows: List<ConfirmRow>): JsonObject {
    val series = original["series"]?.let { runCatching { it.jsonArray }.getOrNull() } ?: JsonArray(emptyList())
    val byIndex = rows.associateBy { it.index }
    val updated = series.mapIndexedNotNull { index, element ->
        val row = byIndex[index] ?: return@mapIndexedNotNull null
        if (!row.isSelected) return@mapIndexedNotNull null
        val obj = element.jsonObject
        JsonObject(
            obj.toMutableMap().apply {
                put("isSelected", JsonPrimitive(true))
                put("isStorage", JsonPrimitive(row.isStorage))
                put("useCover", JsonPrimitive(row.useCover))
                put("useTitle", JsonPrimitive(row.useTitle))
                put("useStatus", JsonPrimitive(row.useStatus))
            },
        )
    }
    return JsonObject(
        original.toMutableMap().apply {
            put("series", JsonArray(updated))
        },
    )
}

/**
 * The source selection for Add Series, remembered between visits — the native
 * twin of the web step's localStorage entry. Persisting it is what keeps a
 * search scoped to a handful of sources instead of every installed extension.
 */
private const val SEARCH_SOURCES_PREFS = "renzo_prefs"
private const val SEARCH_SOURCES_KEY = "renzo_add_series_sources"

private fun loadSearchSources(context: android.content.Context): List<String> =
    context.getSharedPreferences(SEARCH_SOURCES_PREFS, android.content.Context.MODE_PRIVATE)
        .getStringSet(SEARCH_SOURCES_KEY, emptySet())
        ?.toList()
        .orEmpty()

private fun saveSearchSources(context: android.content.Context, ids: List<String>) {
    context.getSharedPreferences(SEARCH_SOURCES_PREFS, android.content.Context.MODE_PRIVATE)
        .edit()
        .putStringSet(SEARCH_SOURCES_KEY, ids.toSet())
        .apply()
}
