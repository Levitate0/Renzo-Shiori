package app.renzoshiori.client.ui.importwizard

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.draw.scale
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.WizardAction
import app.renzoshiori.client.data.model.WizardImport
import app.renzoshiori.client.data.model.WizardImportStatus
import app.renzoshiori.client.data.model.WizardMatch
import app.renzoshiori.client.data.model.WizardSettingsDto
import app.renzoshiori.client.data.model.wizardJsonOf
import app.renzoshiori.client.data.model.toWizardImport
import app.renzoshiori.client.data.model.withField
import app.renzoshiori.client.data.model.withSeriesField
import app.renzoshiori.client.data.network.SetupWizardApi
import app.renzoshiori.client.data.network.absoluteUrl
import coil3.compose.AsyncImage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject

/** How long an edit sits before it is pushed to POST /api/setup/update. */
private const val UPDATE_DEBOUNCE_MS = 5000L

private data class TabDef(
    val id: String,
    val label: String,
    val dotColor: Color,
)

private val CONFIRM_TABS = listOf(
    TabDef("import", "Add", WizardColors.Green),
    TabDef("completed", "Finished", WizardColors.Violet),
    TabDef("unchanged", "Already Imported", WizardColors.Blue),
    TabDef("skip", "Not Matched", WizardColors.Gray),
)

/**
 * Step 02 — setup-wizard/steps/confirm-imports-step.tsx and its
 * ./confirm-imports parts (sticky-head, cards-scroll, import-card, match-row).
 *
 * Edits are applied locally and pushed to POST /api/setup/update on a 5s
 * debounce per path; anything still pending is flushed immediately when the
 * step is left, so a freshly-matched item reliably reaches the backend before
 * the import runs.
 */
@Composable
fun ConfirmImportsStep(
    api: SetupWizardApi?,
    outerScope: CoroutineScope,
    setError: (String?) -> Unit,
    setIsLoading: (Boolean) -> Unit,
    setCanProgress: (Boolean) -> Unit,
) {
    val context = LocalContext.current.applicationContext as RenzoApp
    val baseUrl = context.tokenStore.serverUrl ?: ""

    var imports by remember { mutableStateOf<List<WizardImport>?>(null) }
    var activeTab by remember { mutableStateOf("import") }
    var settings by remember { mutableStateOf<WizardSettingsDto?>(null) }
    var isUpdating by remember { mutableStateOf(false) }
    var searchTarget by remember { mutableStateOf<WizardImport?>(null) }

    val debounceJobs = remember { mutableMapOf<String, Job>() }
    val pendingPayloads = remember { mutableMapOf<String, JsonObject>() }

    suspend fun push(payload: JsonObject) {
        val service = api ?: return
        isUpdating = true
        runCatching { service.updateImport(payload) }
            .onFailure { setError("Failed to update import. Please try again.") }
        isUpdating = false
    }

    /** Mirrors updateImportField(): local state first, backend on a debounce. */
    fun updateImport(path: String, transform: (WizardImport) -> WizardImport) {
        val current = imports ?: return
        val updatedEntry = current.firstOrNull { it.path == path }?.let(transform) ?: return
        imports = current.map { if (it.path == path) updatedEntry else it }

        pendingPayloads[path] = updatedEntry.raw
        debounceJobs.remove(path)?.cancel()
        debounceJobs[path] = outerScope.launch {
            delay(UPDATE_DEBOUNCE_MS)
            val payload = pendingPayloads.remove(path) ?: return@launch
            debounceJobs.remove(path)
            push(payload)
        }
    }

    // Fetch on mount (imports + the settings that back the Category picker).
    LaunchedEffect(api) {
        val service = api ?: run {
            imports = emptyList()
            setError("Failed to load imports. Please try again.")
            return@LaunchedEffect
        }
        runCatching { service.imports() }
            .onSuccess { imports = it.map { entry -> entry.toWizardImport() } }
            .onFailure {
                imports = emptyList()
                setError("Failed to load imports. Please try again.")
            }
        runCatching { service.settings() }.onSuccess { settings = it }
    }

    // Flush any pending (debounced) edits when the user leaves this step.
    DisposableEffect(Unit) {
        onDispose {
            val payloads = pendingPayloads.toMap()
            pendingPayloads.clear()
            debounceJobs.values.forEach { it.cancel() }
            debounceJobs.clear()
            payloads.values.forEach { payload ->
                outerScope.launch { runCatching { api?.updateImport(payload) } }
            }
        }
    }

    val entries = imports
    LaunchedEffect(entries, isUpdating) {
        setIsLoading(entries == null)
        // A valid import is one that won't produce a broken payload: Import-status
        // items must have at least one series marked preferred.
        val valid = entries?.count {
            it.status != WizardImportStatus.IMPORT || it.series.any { s -> s.preferred }
        } ?: 0
        setCanProgress(
            entries != null && entries.isNotEmpty() && valid == entries.size,
        )
    }

    if (entries == null) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("Loading imports…", color = WizardColors.FgMuted, style = MaterialTheme.typography.bodyMedium)
        }
        return
    }
    if (entries.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("Loading Series", color = WizardColors.FgMuted, style = MaterialTheme.typography.bodyMedium)
        }
        return
    }

    val importsToProcess = entries.filter { it.status == WizardImportStatus.IMPORT }
    val skipped = entries.filter { it.status == WizardImportStatus.SKIP }
    val unchanged = entries.filter { it.status == WizardImportStatus.DO_NOT_CHANGE }
    val completed = entries.filter { it.status == WizardImportStatus.COMPLETED }
    val counts = mapOf(
        "import" to importsToProcess.size,
        "completed" to completed.size,
        "unchanged" to unchanged.size,
        "skip" to skipped.size,
    )
    val activeItems = when (activeTab) {
        "completed" -> completed
        "unchanged" -> unchanged
        "skip" -> skipped
        else -> importsToProcess
    }
    val emptyMessage = when (activeTab) {
        "skip" -> "No series marked to skip"
        "import" -> "No series marked for import"
        else -> "No items to display"
    }
    val reviewedCount = entries.count { it.status != WizardImportStatus.IMPORT }

    val showSearchButton = activeTab == "skip"
    val showSkipButton = activeTab == "import" || activeTab == "completed"
    val showAddButton = activeTab == "skip"

    Column(modifier = Modifier.fillMaxSize()) {
        StickyHead(
            activeTab = activeTab,
            onTabChange = { activeTab = it },
            counts = counts,
            reviewedCount = reviewedCount,
            totalCount = entries.size,
            readyCount = importsToProcess.size,
        )

        if (activeItems.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(emptyMessage, color = WizardColors.FgMuted, style = MaterialTheme.typography.bodyMedium)
            }
        } else {
            LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(activeItems, key = { it.path }) { item ->
                    ImportCard(
                        entry = item,
                        baseUrl = baseUrl,
                        categories = if (settings?.categorizedFolders == true) settings?.categories.orEmpty() else emptyList(),
                        showSearchButton = showSearchButton,
                        showSkipButton = showSkipButton,
                        showAddButton = showAddButton,
                        onCategoryChange = { value ->
                            updateImport(item.path) { it.withField("type", wizardJsonOf(value)) }
                        },
                        onChapterChange = { chapter ->
                            updateImport(item.path) { it.withField("continueAfterChapter", wizardJsonOf(chapter)) }
                        },
                        onSkip = {
                            updateImport(item.path) {
                                it.withField("status", wizardJsonOf(WizardImportStatus.SKIP))
                                    .withField("action", wizardJsonOf(WizardAction.SKIP))
                            }
                        },
                        onAdd = {
                            updateImport(item.path) {
                                it.withField("status", wizardJsonOf(WizardImportStatus.IMPORT))
                                    .withField("action", wizardJsonOf(WizardAction.ADD))
                            }
                        },
                        onSearch = { searchTarget = item },
                        onProviderToggle = { index ->
                            updateImport(item.path) {
                                it.withSeriesField(
                                    index,
                                    "preferred",
                                    wizardJsonOf(!(it.series.getOrNull(index)?.preferred ?: false)),
                                )
                            }
                        },
                        onSeriesPropertyChange = { index, property, value ->
                            updateImport(item.path) { it.withSeriesField(index, property, wizardJsonOf(value)) }
                        },
                        modifier = Modifier.padding(bottom = 14.dp),
                    )
                }
            }
        }
    }

    searchTarget?.let { target ->
        SearchSeriesRequester(
            api = api,
            baseUrl = baseUrl,
            importTitle = target.title.ifEmpty { "Unknown Title" },
            importPath = target.path,
            onDismiss = { searchTarget = null },
            onResult = { augmented ->
                // The backend's augment() only attaches the matched series — it leaves
                // the import's status untouched (still Skip for a Not-Matched item), so
                // promote a freshly-matched Skip item straight into the Add list. Any
                // meaningful status the backend did assign is kept.
                val matched = augmented.toWizardImport().let { entry ->
                    if (entry.status == WizardImportStatus.SKIP) {
                        entry.withField("status", wizardJsonOf(WizardImportStatus.IMPORT))
                            .withField("action", wizardJsonOf(WizardAction.ADD))
                    } else {
                        entry
                    }
                }
                updateImport(target.path) { matched }
                searchTarget = null
            },
        )
    }
}

/** .iw-sticky-head — the filter chip strip plus the reviewed counter row. */
@Composable
private fun StickyHead(
    activeTab: String,
    onTabChange: (String) -> Unit,
    counts: Map<String, Int>,
    reviewedCount: Int,
    totalCount: Int,
    readyCount: Int,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(top = 12.dp, bottom = 14.dp)) {
        Row(
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(WizardColors.TabStrip)
                .border(1.dp, WizardColors.Border, RoundedCornerShape(12.dp))
                .horizontalScroll(rememberScrollState())
                .padding(4.dp),
        ) {
            CONFIRM_TABS.forEach { tab ->
                val active = tab.id == activeTab
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .height(44.dp)
                            .clip(RoundedCornerShape(10.dp))
                            .background(if (active) WizardColors.TabActive else Color(0x800F0F13))
                            .then(
                                if (active) {
                                    Modifier.border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(10.dp))
                                } else {
                                    Modifier
                                }
                            )
                            .clickable { onTabChange(tab.id) }
                            .padding(horizontal = 14.dp),
                    ) {
                        Box(
                            modifier = Modifier
                                .size(6.dp)
                                .clip(RoundedCornerShape(3.dp))
                                .background(tab.dotColor),
                        )
                        Text(
                            tab.label.uppercase(),
                            style = wizardMono(
                                10.5f,
                                FontWeight.SemiBold,
                                0.14f,
                                if (active) WizardColors.Fg else WizardColors.FgMuted,
                            ),
                            maxLines = 1,
                            modifier = Modifier.padding(start = 8.dp),
                        )
                        Text(
                            (counts[tab.id] ?: 0).toString(),
                            style = wizardMono(11f, FontWeight.Bold, 0.0f, WizardColors.Fg),
                            modifier = Modifier.padding(start = 8.dp),
                        )
                    }
                    if (active) {
                        Box(
                            modifier = Modifier
                                .padding(top = 4.dp)
                                .height(2.dp)
                                .width(48.dp)
                                .clip(RoundedCornerShape(2.dp))
                                .background(WizardColors.Primary),
                        )
                    }
                }
            }
        }

        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(top = 6.dp, start = 4.dp, end = 4.dp),
        ) {
            Text(
                "${reviewedCount.toString().padStart(2, '0')} OF $totalCount REVIEWED",
                style = wizardMono(9.5f, FontWeight.SemiBold, 0.16f, WizardColors.FgDim),
            )
            Text(
                "·",
                style = wizardMono(9.5f, FontWeight.Normal, 0.16f, WizardColors.FgDim),
                modifier = Modifier.padding(horizontal = 8.dp),
            )
            Text(
                "$readyCount READY TO IMPORT",
                style = wizardMono(9.5f, FontWeight.SemiBold, 0.16f, WizardColors.Primary),
            )
            Spacer(Modifier.width(12.dp))
            WizardProgressBar(
                progress = if (totalCount > 0) readyCount.toFloat() / totalCount else 0f,
                height = 1,
                modifier = Modifier.weight(1f),
            )
        }
    }
}

/** .iw-import-card — poster + title/path, the action cluster, then the match rows. */
@Composable
private fun ImportCard(
    entry: WizardImport,
    baseUrl: String,
    categories: List<String>,
    showSearchButton: Boolean,
    showSkipButton: Boolean,
    showAddButton: Boolean,
    onCategoryChange: (String) -> Unit,
    onChapterChange: (Double) -> Unit,
    onSkip: () -> Unit,
    onAdd: () -> Unit,
    onSearch: () -> Unit,
    onProviderToggle: (Int) -> Unit,
    onSeriesPropertyChange: (Int, String, Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    val preferred = entry.series.firstOrNull { it.preferred }
    val thumbnail = preferred?.thumbnailUrl
    var chapterText by remember(entry.path) {
        mutableStateOf((entry.continueAfterChapter ?: 0.0).let { value ->
            if (value == value.toLong().toDouble()) value.toLong().toString() else value.toString()
        })
    }
    var categoryOpen by remember { mutableStateOf(false) }

    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(WizardColors.CardBg)
            .border(1.dp, WizardColors.Border, RoundedCornerShape(12.dp)),
    ) {
        Row(modifier = Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, top = 14.dp)) {
            if (!thumbnail.isNullOrEmpty()) {
                Box(
                    modifier = Modifier
                        .width(56.dp)
                        .height(80.dp)
                        .clip(RoundedCornerShape(6.dp))
                        .background(WizardColors.Panel),
                ) {
                    AsyncImage(
                        model = absoluteUrl(baseUrl, thumbnail),
                        contentDescription = entry.title.ifEmpty { "Series thumbnail" },
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                }
                Spacer(Modifier.width(16.dp))
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    entry.title.ifEmpty { "Unknown Title" },
                    style = MaterialTheme.typography.titleMedium,
                    color = WizardColors.Fg,
                )
                Text(
                    entry.path,
                    style = wizardMono(9.5f, FontWeight.Normal, 0.02f, WizardColors.FgDim),
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 4.dp),
                )
                if (entry.isTitleOnly && categories.isNotEmpty()) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.padding(top = 6.dp),
                    ) {
                        Text(
                            "Category:",
                            style = MaterialTheme.typography.labelSmall,
                            color = WizardColors.FgMuted,
                        )
                        Box(modifier = Modifier.padding(start = 8.dp)) {
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .height(28.dp)
                                    .clip(RoundedCornerShape(6.dp))
                                    .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(6.dp))
                                    .clickable { categoryOpen = true }
                                    .padding(horizontal = 10.dp),
                            ) {
                                Text(
                                    entry.type.ifEmpty { categories.first() },
                                    style = MaterialTheme.typography.labelSmall,
                                    color = WizardColors.Fg,
                                    maxLines = 1,
                                )
                            }
                            DropdownMenu(
                                expanded = categoryOpen,
                                onDismissRequest = { categoryOpen = false },
                                containerColor = WizardColors.Panel,
                                shape = RoundedCornerShape(8.dp),
                            ) {
                                categories.forEach { category ->
                                    Text(
                                        category,
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = WizardColors.Fg,
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .clickable {
                                                categoryOpen = false
                                                onCategoryChange(category)
                                            }
                                            .padding(horizontal = 14.dp, vertical = 10.dp),
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }

        // Action cluster — full-width row below the poster/title grid on phones.
        Row(
            verticalAlignment = Alignment.Top,
            modifier = Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, top = 12.dp, bottom = 14.dp),
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    "CONTINUE AFTER",
                    style = wizardMono(8.5f, FontWeight.SemiBold, 0.16f, WizardColors.FgDim),
                )
                BasicTextField(
                    value = chapterText,
                    onValueChange = { raw ->
                        val filtered = raw.filter { it.isDigit() || it == '.' }
                        chapterText = filtered
                        onChapterChange(filtered.toDoubleOrNull() ?: 0.0)
                    },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    textStyle = wizardMono(13f, FontWeight.Normal, 0.0f, WizardColors.Fg)
                        .copy(textAlign = TextAlign.Center),
                    cursorBrush = SolidColor(WizardColors.Primary),
                    decorationBox = { inner ->
                        Box(
                            contentAlignment = Alignment.Center,
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(36.dp)
                                .clip(RoundedCornerShape(8.dp))
                                .background(WizardColors.Panel)
                                .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(8.dp))
                                .padding(horizontal = 10.dp),
                        ) {
                            if (chapterText.isEmpty()) {
                                Text(
                                    "0",
                                    style = wizardMono(13f, FontWeight.Normal, 0.0f, WizardColors.FgDim),
                                )
                            }
                            inner()
                        }
                    },
                    modifier = Modifier.fillMaxWidth().padding(top = 3.dp),
                )
            }

            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.padding(start = 8.dp, top = 12.dp),
            ) {
                if (showSearchButton || entry.series.isEmpty()) {
                    ActionButton(icon = Icons.Filled.Search, label = "Search", onClick = onSearch)
                }
                if (showSkipButton) {
                    ActionButton(icon = Icons.Filled.Close, label = "Mismatch", onClick = onSkip)
                }
                if (showAddButton) {
                    ActionButton(icon = Icons.Filled.Add, label = "Add", onClick = onAdd)
                }
            }
        }

        if (entry.series.isNotEmpty()) {
            HorizontalDivider(color = WizardColors.Border)
            Column(modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
                entry.series.forEachIndexed { index, match ->
                    if (index > 0) HorizontalDivider(color = Color(0x08FFFFFF))
                    MatchRow(
                        match = match,
                        baseUrl = baseUrl,
                        onToggle = { onProviderToggle(index) },
                        onPropertyChange = { property, value -> onSeriesPropertyChange(index, property, value) },
                    )
                }
            }
        }
    }
}

/** .iw-action-btn */
@Composable
private fun ActionButton(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .height(36.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(Color(0x991A1A1F))
            .border(1.dp, WizardColors.Border, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp),
    ) {
        Icon(icon, contentDescription = null, tint = WizardColors.FgMuted, modifier = Modifier.size(14.dp))
        Text(
            label.uppercase(),
            style = wizardMono(10f, FontWeight.SemiBold, 0.14f, WizardColors.FgMuted),
            maxLines = 1,
            modifier = Modifier.padding(start = 5.dp),
        )
    }
}

/**
 * .iw-match-row — one provider match. Tapping the info column toggles
 * "preferred" (rose accent bar + tinted row + PREFERRED tag); the three
 * switches below are Permanent / Cover / Title.
 */
@Composable
private fun MatchRow(
    match: WizardMatch,
    baseUrl: String,
    onToggle: () -> Unit,
    onPropertyChange: (String, Boolean) -> Unit,
) {
    var permanentInfoOpen by remember { mutableStateOf(false) }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(
                if (match.preferred) {
                    Modifier.background(
                        Brush.horizontalGradient(
                            0f to WizardColors.Primary.copy(alpha = 0.15f),
                            0.6f to Color.Transparent,
                        ),
                    )
                } else {
                    Modifier
                }
            )
            .padding(end = 14.dp, top = 14.dp, bottom = 14.dp),
    ) {
        // Rose accent bar on the left edge of a preferred row.
        Box(
            modifier = Modifier
                .width(3.dp)
                .height(52.dp)
                .clip(RoundedCornerShape(topEnd = 2.dp, bottomEnd = 2.dp))
                .background(if (match.preferred) WizardColors.Primary else Color.Transparent),
        )
        Spacer(Modifier.width(14.dp))

        Column(modifier = Modifier.weight(1f)) {
            Row {
                Box(
                    modifier = Modifier
                        .width(36.dp)
                        .height(52.dp)
                        .clip(RoundedCornerShape(4.dp))
                        .background(WizardColors.Panel)
                        .then(
                            if (match.preferred) {
                                Modifier.border(1.5.dp, WizardColors.Primary, RoundedCornerShape(4.dp))
                            } else {
                                Modifier
                            }
                        ),
                ) {
                    if (!match.thumbnailUrl.isNullOrEmpty()) {
                        AsyncImage(
                            model = absoluteUrl(baseUrl, match.thumbnailUrl),
                            contentDescription = "${match.title} cover",
                            contentScale = ContentScale.Crop,
                            modifier = Modifier.fillMaxSize(),
                        )
                    }
                }
                Column(
                    modifier = Modifier
                        .padding(start = 14.dp)
                        .weight(1f)
                        .clickable(onClick = onToggle),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            match.provider,
                            style = MaterialTheme.typography.bodyMedium,
                            fontWeight = FontWeight.SemiBold,
                            color = WizardColors.Fg,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        if (!match.url.isNullOrEmpty()) {
                            Icon(
                                Icons.AutoMirrored.Filled.OpenInNew,
                                contentDescription = null,
                                tint = WizardColors.Fg.copy(alpha = 0.6f),
                                modifier = Modifier.padding(start = 4.dp).size(10.dp),
                            )
                        }
                        Text(
                            match.lang.lowercase(),
                            style = wizardMono(9f, FontWeight.Normal, 0.1f, WizardColors.FgMuted),
                            modifier = Modifier.padding(start = 8.dp),
                        )
                        if (match.preferred) {
                            Text(
                                "PREFERRED",
                                style = wizardMono(8.5f, FontWeight.Bold, 0.18f, WizardColors.Primary),
                                modifier = Modifier
                                    .padding(start = 8.dp)
                                    .clip(RoundedCornerShape(4.dp))
                                    .background(WizardColors.Primary.copy(alpha = 0.15f))
                                    .border(
                                        1.dp,
                                        WizardColors.Primary.copy(alpha = 0.35f),
                                        RoundedCornerShape(4.dp),
                                    )
                                    .padding(horizontal = 6.dp, vertical = 2.dp),
                            )
                        }
                    }
                    Row(modifier = Modifier.padding(top = 3.dp)) {
                        if (match.scanlator.isNotEmpty() && match.scanlator != match.provider) {
                            Text(
                                match.scanlator,
                                style = wizardMono(10.5f, FontWeight.Normal, 0.04f, WizardColors.FgMuted),
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                "·",
                                style = wizardMono(10.5f, FontWeight.Normal, 0.04f, WizardColors.FgDim),
                                modifier = Modifier.padding(horizontal = 4.dp),
                            )
                        }
                        Text(
                            "${match.chapterCount} chapters",
                            style = wizardMono(10.5f, FontWeight.SemiBold, 0.04f, WizardColors.Fg),
                        )
                        match.lastChapter?.let { last ->
                            Text(
                                "·",
                                style = wizardMono(10.5f, FontWeight.Normal, 0.04f, WizardColors.FgDim),
                                modifier = Modifier.padding(horizontal = 4.dp),
                            )
                            Text(
                                "Last: ${if (last == last.toLong().toDouble()) last.toLong().toString() else last.toString()}",
                                style = wizardMono(10.5f, FontWeight.Normal, 0.04f, WizardColors.FgMuted),
                            )
                        }
                    }
                }
            }

            // Switch cluster — a 3-column grid under the cover/info row on phones.
            Row(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
            ) {
                Box(modifier = Modifier.weight(1f)) {
                    SwitchCell(
                        label = "Permanent",
                        checked = match.isStorage,
                        onCheckedChange = { onPropertyChange("isStorage", it) },
                        onLabelClick = { permanentInfoOpen = true },
                    )
                    DropdownMenu(
                        expanded = permanentInfoOpen,
                        onDismissRequest = { permanentInfoOpen = false },
                        containerColor = WizardColors.Panel,
                        shape = RoundedCornerShape(8.dp),
                        modifier = Modifier.width(260.dp),
                    ) {
                        Text(
                            "Permanent sources always download new chapters and replace any " +
                                "existing copies from non-permanent sources.",
                            style = MaterialTheme.typography.bodySmall,
                            color = WizardColors.Fg,
                            modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp),
                        )
                        Text(
                            "Non-permanent sources only download a chapter if they are the first " +
                                "to have it available.",
                            style = MaterialTheme.typography.bodySmall,
                            color = WizardColors.Fg,
                            modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp),
                        )
                    }
                }
                Box(modifier = Modifier.weight(1f)) {
                    SwitchCell(
                        label = "Cover",
                        checked = match.useCover,
                        onCheckedChange = { onPropertyChange("useCover", it) },
                    )
                }
                Box(modifier = Modifier.weight(1f)) {
                    SwitchCell(
                        label = "Title",
                        checked = match.useTitle,
                        onCheckedChange = { onPropertyChange("useTitle", it) },
                    )
                }
            }
        }
    }
}

/** .iw-match-switch-cell (mobile variant: label left, switch right, 44px tall). */
@Composable
private fun SwitchCell(
    label: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    onLabelClick: (() -> Unit)? = null,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
        modifier = Modifier
            .fillMaxWidth()
            .height(44.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(WizardColors.SwitchCell)
            .padding(horizontal = 10.dp),
    ) {
        Text(
            label.uppercase(),
            style = wizardMono(8.5f, FontWeight.Normal, 0.16f, WizardColors.FgDim),
            maxLines = 1,
            modifier = if (onLabelClick != null) Modifier.clickable(onClick = onLabelClick) else Modifier,
        )
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = Color.White,
                checkedTrackColor = WizardColors.Primary,
                checkedBorderColor = WizardColors.Primary,
                uncheckedThumbColor = WizardColors.FgMuted,
                uncheckedTrackColor = WizardColors.Panel,
                uncheckedBorderColor = WizardColors.BorderStrong,
            ),
            modifier = Modifier.scale(0.75f),
        )
    }
}
