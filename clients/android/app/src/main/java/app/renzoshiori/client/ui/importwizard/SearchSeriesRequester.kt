package app.renzoshiori.client.ui.importwizard

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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.WarningAmber
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.renzoshiori.client.data.model.WizardSearchSourceDto
import app.renzoshiori.client.data.network.SetupWizardApi
import app.renzoshiori.client.data.network.absoluteUrl
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull

/** One row of the results list, plus the raw object POSTed back to /api/setup/augment. */
private data class SearchHit(
    val raw: JsonObject,
    val id: String,
    val title: String,
    val provider: String,
    val lang: String,
    val thumbnailUrl: String?,
)

private fun JsonObject.text(key: String): String? =
    (this[key] as? JsonPrimitive)?.contentOrNull?.takeIf { it.isNotEmpty() && it != "null" }

/** getSeriesId() — mihonId when present, otherwise providerId. */
private fun JsonObject.toSearchHit(): SearchHit = SearchHit(
    raw = this,
    id = text("mihonId") ?: text("providerId") ?: "",
    title = text("title") ?: "",
    provider = text("provider") ?: "",
    lang = text("lang") ?: "",
    thumbnailUrl = text("thumbnailUrl"),
)

/**
 * Re-match dialog — setup-wizard/search-series-requester.tsx, in its ≤640px
 * full-screen form. Type a keyword (search fires at 3 characters, 300ms
 * debounced), narrow it by source, pick one or more results, then Apply Match
 * to POST them to /api/setup/augment for this import's path.
 */
@Composable
fun SearchSeriesRequester(
    api: SetupWizardApi?,
    baseUrl: String,
    importTitle: String,
    importPath: String,
    onDismiss: () -> Unit,
    onResult: (JsonObject) -> Unit,
) {
    val scope = rememberCoroutineScope()

    var searchValue by remember { mutableStateOf(importTitle) }
    var debounced by remember { mutableStateOf(importTitle) }
    var results by remember { mutableStateOf<List<SearchHit>>(emptyList()) }
    var isSearching by remember { mutableStateOf(false) }
    var isSubmitting by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var sourcesOpen by remember { mutableStateOf(false) }

    val availableSources = remember { mutableStateListOf<WizardSearchSourceDto>() }
    val selectedSources = remember { mutableStateListOf<String>() }
    val selectedSeries = remember { mutableStateListOf<String>() }

    LaunchedEffect(api) {
        val service = api ?: return@LaunchedEffect
        runCatching { service.searchSources() }.onSuccess { sources ->
            availableSources.clear()
            availableSources.addAll(sources)
            if (selectedSources.isEmpty()) {
                selectedSources.addAll(sources.map { it.mihonProviderId })
            }
        }
    }

    // 300ms debounce on the keyword (use-debounce on the web).
    LaunchedEffect(searchValue) {
        delay(300)
        debounced = searchValue
    }

    LaunchedEffect(debounced, selectedSources.toList()) {
        val service = api ?: return@LaunchedEffect
        if (debounced.length < 3 || selectedSources.isEmpty()) {
            results = emptyList()
            isSearching = false
            return@LaunchedEffect
        }
        isSearching = true
        runCatching { service.search(debounced, selectedSources.toList()) }
            .onSuccess {
                results = it.map { hit -> hit.toSearchHit() }
                error = null
            }
            .onFailure { error = it.message ?: "Search failed" }
        isSearching = false
    }

    val hasQuery = searchValue.isNotEmpty()
    val canSubmit = selectedSeries.isNotEmpty() && !isSubmitting

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(WizardColors.Shell)
                .statusBarsPadding(),
        ) {
            // ── Header ───────────────────────────────────────────────────
            Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp)) {
                Text(
                    "RE-MATCH SERIES",
                    style = wizardMono(10.5f, FontWeight.SemiBold, 0.32f, WizardColors.Primary),
                )
                Text(
                    "Search for a match",
                    style = MaterialTheme.typography.headlineSmall,
                    color = WizardColors.Fg,
                    modifier = Modifier.padding(top = 10.dp),
                )
                Row(modifier = Modifier.padding(top = 6.dp)) {
                    Text(
                        importPath,
                        style = wizardMono(9.5f, FontWeight.Normal, 0.02f, WizardColors.FgDim),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    Text(
                        "·",
                        style = wizardMono(9.5f, FontWeight.Normal, 0.02f, WizardColors.FgDim),
                        modifier = Modifier.padding(horizontal = 6.dp),
                    )
                    Text(
                        importTitle,
                        style = wizardMono(9.5f, FontWeight.SemiBold, 0.02f, WizardColors.FgMuted),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }

            // ── Search input ─────────────────────────────────────────────
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp)
                    .height(48.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(WizardColors.Panel)
                    .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(10.dp))
                    .padding(horizontal = 14.dp),
            ) {
                Icon(
                    Icons.Filled.Search,
                    contentDescription = null,
                    tint = WizardColors.FgMuted,
                    modifier = Modifier.size(20.dp),
                )
                BasicTextField(
                    value = searchValue,
                    onValueChange = { searchValue = it },
                    singleLine = true,
                    textStyle = MaterialTheme.typography.bodyMedium.copy(color = WizardColors.Fg),
                    cursorBrush = SolidColor(WizardColors.Primary),
                    decorationBox = { inner ->
                        Box(contentAlignment = Alignment.CenterStart) {
                            if (searchValue.isEmpty()) {
                                Text(
                                    "Search for a series…",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = WizardColors.FgDim,
                                )
                            }
                            inner()
                        }
                    },
                    modifier = Modifier.padding(horizontal = 12.dp).weight(1f),
                )
                if (isSearching) {
                    CircularProgressIndicator(
                        color = WizardColors.FgMuted,
                        strokeWidth = 2.dp,
                        modifier = Modifier.size(16.dp),
                    )
                }
            }

            // ── Source filter ────────────────────────────────────────────
            if (availableSources.isNotEmpty()) {
                Box(modifier = Modifier.padding(start = 16.dp, top = 10.dp)) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .height(36.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(8.dp))
                            .clickable { sourcesOpen = true }
                            .padding(horizontal = 12.dp),
                    ) {
                        Text(
                            if (selectedSources.size == availableSources.size) {
                                "All sources"
                            } else {
                                "${selectedSources.size} of ${availableSources.size} sources"
                            },
                            style = wizardMono(10f, FontWeight.SemiBold, 0.12f, WizardColors.FgMuted),
                        )
                        Icon(
                            Icons.Filled.ExpandMore,
                            contentDescription = null,
                            tint = WizardColors.FgMuted,
                            modifier = Modifier.padding(start = 6.dp).size(14.dp),
                        )
                    }
                    DropdownMenu(
                        expanded = sourcesOpen,
                        onDismissRequest = { sourcesOpen = false },
                        containerColor = WizardColors.Panel,
                        shape = RoundedCornerShape(10.dp),
                        modifier = Modifier.heightIn(max = 380.dp).width(280.dp),
                    ) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clickable {
                                    if (selectedSources.size == availableSources.size) {
                                        selectedSources.clear()
                                    } else {
                                        selectedSources.clear()
                                        selectedSources.addAll(availableSources.map { it.mihonProviderId })
                                    }
                                }
                                .padding(horizontal = 14.dp, vertical = 10.dp),
                        ) {
                            Text(
                                if (selectedSources.size == availableSources.size) "Clear all" else "Select all",
                                style = MaterialTheme.typography.bodyMedium,
                                color = WizardColors.Primary,
                            )
                        }
                        availableSources.forEach { source ->
                            val checked = selectedSources.contains(source.mihonProviderId)
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (checked) {
                                            selectedSources.remove(source.mihonProviderId)
                                        } else {
                                            selectedSources.add(source.mihonProviderId)
                                        }
                                    }
                                    .padding(horizontal = 14.dp, vertical = 10.dp),
                            ) {
                                Box(
                                    contentAlignment = Alignment.Center,
                                    modifier = Modifier
                                        .size(16.dp)
                                        .clip(RoundedCornerShape(4.dp))
                                        .background(
                                            if (checked) WizardColors.Primary else Color.Transparent,
                                        )
                                        .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(4.dp)),
                                ) {
                                    if (checked) {
                                        Icon(
                                            Icons.Filled.Check,
                                            contentDescription = null,
                                            tint = Color.White,
                                            modifier = Modifier.size(11.dp),
                                        )
                                    }
                                }
                                Column(modifier = Modifier.padding(start = 10.dp)) {
                                    Text(
                                        source.provider,
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = WizardColors.Fg,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                    if (source.language.isNotEmpty()) {
                                        Text(
                                            source.language.lowercase(),
                                            style = wizardMono(9f, FontWeight.Normal, 0.1f, WizardColors.FgDim),
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // ── Inline error ─────────────────────────────────────────────
            error?.let { message ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 10.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(WizardColors.Destructive.copy(alpha = 0.12f))
                        .border(1.dp, Color(0x59EF4444), RoundedCornerShape(8.dp))
                        .padding(10.dp),
                ) {
                    Icon(
                        Icons.Filled.WarningAmber,
                        contentDescription = null,
                        tint = Color(0xFFF87171),
                        modifier = Modifier.size(14.dp),
                    )
                    Text(
                        message,
                        style = MaterialTheme.typography.bodySmall,
                        color = Color(0xFFF87171),
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
            }

            // ── Results ──────────────────────────────────────────────────
            Box(modifier = Modifier.weight(1f).fillMaxWidth().padding(top = 10.dp)) {
                when {
                    !hasQuery -> SearchHint("Start typing to search…")
                    isSearching -> SearchHint("Searching…")
                    results.isEmpty() -> SearchHint(
                        if (debounced.length < 3) {
                            "Keep typing — search starts at 3 characters"
                        } else {
                            "No results found"
                        },
                    )
                    else -> LazyColumn(modifier = Modifier.fillMaxSize()) {
                        items(results) { hit ->
                            val isSelected = selectedSeries.contains(hit.id)
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (isSelected) selectedSeries.remove(hit.id) else selectedSeries.add(hit.id)
                                    }
                                    .padding(horizontal = 16.dp, vertical = 8.dp),
                            ) {
                                Box(
                                    modifier = Modifier
                                        .width(3.dp)
                                        .height(48.dp)
                                        .clip(RoundedCornerShape(2.dp))
                                        .background(
                                            if (isSelected) WizardColors.Primary else Color.Transparent,
                                        ),
                                )
                                Box(
                                    modifier = Modifier
                                        .padding(start = 10.dp)
                                        .width(44.dp)
                                        .height(64.dp)
                                        .clip(RoundedCornerShape(6.dp))
                                        .background(WizardColors.Panel),
                                ) {
                                    if (!hit.thumbnailUrl.isNullOrEmpty()) {
                                        AsyncImage(
                                            model = absoluteUrl(baseUrl, hit.thumbnailUrl),
                                            contentDescription = hit.title.ifEmpty { "Series thumbnail" },
                                            contentScale = ContentScale.Crop,
                                            modifier = Modifier.fillMaxSize(),
                                        )
                                    }
                                }
                                Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                                    Text(
                                        hit.title,
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = WizardColors.Fg,
                                        maxLines = 2,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                    Row(
                                        verticalAlignment = Alignment.CenterVertically,
                                        modifier = Modifier.padding(top = 4.dp),
                                    ) {
                                        Text(
                                            hit.provider,
                                            style = wizardMono(9f, FontWeight.SemiBold, 0.1f, WizardColors.FgMuted),
                                            modifier = Modifier
                                                .clip(RoundedCornerShape(4.dp))
                                                .background(WizardColors.Panel)
                                                .padding(horizontal = 6.dp, vertical = 2.dp),
                                        )
                                        Text(
                                            hit.lang.uppercase(),
                                            style = wizardMono(9f, FontWeight.Normal, 0.1f, WizardColors.FgDim),
                                            modifier = Modifier.padding(start = 8.dp),
                                        )
                                    }
                                }
                                if (isSelected) {
                                    Text(
                                        "✓ added",
                                        style = wizardMono(10f, FontWeight.SemiBold, 0.06f, WizardColors.Primary),
                                    )
                                }
                            }
                        }
                    }
                }
            }

            // ── Footer ───────────────────────────────────────────────────
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .background(Color(0x99090909))
                    .padding(horizontal = 16.dp, vertical = 12.dp)
                    .navigationBarsPadding(),
            ) {
                Text(
                    "${selectedSeries.size} selected · ${results.size} results",
                    style = wizardMono(10f, FontWeight.Normal, 0.08f, WizardColors.FgDim),
                    modifier = Modifier.weight(1f),
                )
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .heightIn(min = 44.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(8.dp))
                        .clickable(enabled = !isSubmitting, onClick = onDismiss)
                        .padding(horizontal = 16.dp),
                ) {
                    Text(
                        "CANCEL",
                        style = wizardMono(11f, FontWeight.SemiBold, 0.16f, WizardColors.FgMuted),
                    )
                }
                Spacer(Modifier.width(8.dp))
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .heightIn(min = 44.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(if (canSubmit) WizardColors.Primary else WizardColors.Primary.copy(alpha = 0.45f))
                        .clickable(enabled = canSubmit) {
                            val service = api ?: return@clickable
                            isSubmitting = true
                            error = null
                            scope.launch {
                                val payload = results.filter { selectedSeries.contains(it.id) }.map { it.raw }
                                runCatching { service.augment(importPath, payload) }
                                    .onSuccess { onResult(it) }
                                    .onFailure { error = it.message ?: "Failed to augment series" }
                                isSubmitting = false
                            }
                        }
                        .padding(horizontal = 16.dp),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        if (isSubmitting) {
                            CircularProgressIndicator(
                                color = Color.White,
                                strokeWidth = 2.dp,
                                modifier = Modifier.size(14.dp),
                            )
                            Spacer(Modifier.width(8.dp))
                        }
                        Text(
                            if (isSubmitting) "APPLYING…" else "APPLY MATCH",
                            style = wizardMono(11f, FontWeight.Bold, 0.16f, Color.White),
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun SearchHint(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Text(
            text,
            style = MaterialTheme.typography.bodyMedium,
            color = WizardColors.FgMuted,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 24.dp, start = 24.dp, end = 24.dp),
        )
    }
}
