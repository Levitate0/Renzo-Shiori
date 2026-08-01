package app.renzoshiori.client.ui.sources

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Power
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SortByAlpha
import androidx.compose.material.icons.filled.FileUpload
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.documentfile.provider.DocumentFile
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.ProviderDto
import app.renzoshiori.client.data.network.SourcesApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.toRequestBody

/** Number of rows visible before "Show all" is displayed. */
private const val INITIAL_LIMIT = 24

private const val TAB_SOURCES = "sources"
private const val TAB_DEFAULT_PRIORITY = "default-priority"

private const val SORT_NAME_ASC = "name-asc"
private const val SORT_NAME_DESC = "name-desc"

private const val NSFW_ALWAYS_HIDE = "AlwaysHide"
private const val NSFW_SHOW = "Show"

/**
 * Sources — a 1:1 transliteration of RenzoFrontend src/app/providers/page.tsx
 * (the page titled "Sources") and src/components/comp/sources. Keeps the web's
 * two tabs ("Sources" / "Default priority order"), the collapsible "Extension
 * versions" panel, the Installed and Available sections with their "Show all"
 * footers, and the filter toolbar (Languages / NSFW / Sort / Install from APK…)
 * that sits between them because its filters apply only to Available.
 *
 * The web's desktop-only affordances (hover dropdowns, the split
 * settings-icon + "Installed" pill) collapse into the same mobile treatment the
 * web itself uses below `md`: one ⋮ menu per row with Settings… and Uninstall.
 */
@Composable
fun SourcesScreen() {
    val app = LocalContext.current.applicationContext as RenzoApp
    val api = remember { app.network.currentServiceOf<SourcesApi>() }
    val baseUrl = app.tokenStore.serverUrl ?: ""
    val snackbar = remember { SnackbarHostState() }
    var tab by remember { mutableStateOf(TAB_SOURCES) }

    Box(modifier = Modifier.fillMaxSize()) {
        Column(modifier = Modifier.fillMaxSize()) {
            // ── Ribbon: Sources · Install, enable, and health-check Mihon extensions
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 12.dp),
            ) {
                Icon(
                    Icons.Filled.Power,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
                Text(
                    "Sources",
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = RenzoColors.Foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(start = 8.dp),
                )
                Text(
                    "· Install, enable, and health-check Mihon extensions",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }

            // ── TabsList ──────────────────────────────────────────────────────
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .padding(start = 16.dp, end = 16.dp, top = 16.dp)
                    .height(36.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(RenzoColors.Muted)
                    .padding(4.dp),
            ) {
                TabTrigger("Sources", tab == TAB_SOURCES) { tab = TAB_SOURCES }
                TabTrigger("Default priority order", tab == TAB_DEFAULT_PRIORITY) {
                    tab = TAB_DEFAULT_PRIORITY
                }
            }

            Spacer(Modifier.height(8.dp))

            Box(modifier = Modifier.weight(1f)) {
                when (tab) {
                    TAB_SOURCES -> SourcesTab(api, baseUrl, snackbar)
                    else -> DefaultPriorityOrderTab(api, snackbar)
                }
            }
        }
        SnackbarHost(snackbar, modifier = Modifier.align(Alignment.BottomCenter))
    }
}

@Composable
private fun RowScope.TabTrigger(label: String, active: Boolean, onClick: () -> Unit) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .weight(1f)
            .fillMaxHeight()
            .clip(RoundedCornerShape(6.dp))
            .background(if (active) RenzoColors.Background else Color.Transparent)
            .clickable { onClick() }
            .padding(horizontal = 12.dp),
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodySmall,
            fontWeight = FontWeight.Medium,
            color = if (active) RenzoColors.Foreground else RenzoColors.MutedForeground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Sources tab — ExtensionVersions + SourcesList
// ─────────────────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SourcesTab(api: SourcesApi?, baseUrl: String, snackbar: SnackbarHostState) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    val extensions = remember { mutableStateListOf<ProviderDto>() }
    var loading by remember { mutableStateOf(true) }
    var actionLoading by remember { mutableStateOf<String?>(null) }
    var isUploadingApk by remember { mutableStateOf(false) }
    var showPreferencesFor by remember { mutableStateOf<Pair<String, String>?>(null) }

    // Filter state
    var searchTerm by remember { mutableStateOf("") }
    var hideNsfw by remember { mutableStateOf(true) }
    val selectedLanguages = remember { mutableStateListOf<String>() }
    var sort by remember { mutableStateOf(SORT_NAME_ASC) }
    var nsfwVisibility by remember { mutableStateOf("HideByDefault") }

    // "Show all" expansion
    var expandedInstalled by remember { mutableStateOf(false) }
    var expandedAvailable by remember { mutableStateOf(false) }

    var langSheetOpen by remember { mutableStateOf(false) }

    suspend fun reload() {
        val result = runCatching { api?.providers() ?: emptyList() }
        val list = result.getOrNull()
        if (list != null) {
            extensions.clear()
            extensions.addAll(list)
        } else {
            snackbar.showSnackbar("Failed to load sources")
        }
    }

    LaunchedEffect(Unit) {
        loading = true
        reload()
        loading = false
        // NSFW visibility from settings — mirrors the web's useSettings() sync.
        runCatching { api?.settings()?.nsfwVisibility }.getOrNull()?.let { v ->
            nsfwVisibility = v
            hideNsfw = v != NSFW_SHOW
        }
    }

    // ── APK upload (Install from APK…) ───────────────────────────────────────
    val apkPicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        val name = DocumentFile.fromSingleUri(context, uri)?.name ?: "extension.apk"
        if (!name.endsWith(".apk")) return@rememberLauncherForActivityResult
        scope.launch {
            isUploadingApk = true
            val bytes = runCatching { context.contentResolver.openInputStream(uri)?.readBytes() }.getOrNull()
            if (bytes == null) {
                snackbar.showSnackbar("Failed to install APK")
            } else {
                val part = MultipartBody.Part.createFormData(
                    "file", name,
                    bytes.toRequestBody("application/vnd.android.package-archive".toMediaType()),
                )
                val pkgName = runCatching {
                    api?.installProviderFromFile(part)?.string()?.trim()?.trim('"')
                }.getOrNull()
                if (pkgName.isNullOrEmpty()) {
                    snackbar.showSnackbar("Failed to install APK")
                } else {
                    reload()
                    extensions.firstOrNull { it.packageName == pkgName }?.let {
                        showPreferencesFor = it.packageName to it.name
                    }
                }
            }
            isUploadingApk = false
        }
    }

    // ── Derived lists (verbatim from sources-list.tsx) ───────────────────────
    val availableLanguageOptions = remember(extensions.toList()) {
        extensions.flatMap { extensionLanguages(it) }.filter { it.isNotEmpty() }
            .distinct().sorted()
    }

    val nameComparator: Comparator<ProviderDto> =
        if (sort == SORT_NAME_ASC) compareBy(String.CASE_INSENSITIVE_ORDER) { it.name }
        else compareByDescending(String.CASE_INSENSITIVE_ORDER) { it.name }

    // Installed = installed FOR ME; sorted only, never filtered.
    val filteredInstalled = remember(extensions.toList(), sort) {
        extensions.filter { it.isEnabledForMe }.sortedWith(nameComparator)
    }

    val filteredAvailable = remember(
        extensions.toList(), searchTerm, hideNsfw, selectedLanguages.toList(), sort,
    ) {
        var list = extensions.filter { !it.isEnabledForMe }
        if (searchTerm.isNotBlank()) {
            val term = searchTerm.lowercase()
            list = list.filter { ext ->
                ext.name.lowercase().contains(term) ||
                    extensionLanguages(ext).any { it.lowercase().contains(term) }
            }
        }
        if (hideNsfw) list = list.filter { !isExtensionNsfw(it) }
        if (selectedLanguages.isNotEmpty()) {
            list = list.filter { ext -> extensionLanguages(ext).any { it in selectedLanguages } }
        }
        list.sortedWith(nameComparator)
    }

    val visibleInstalled = if (expandedInstalled) filteredInstalled else filteredInstalled.take(INITIAL_LIMIT)
    val visibleAvailable = if (expandedAvailable) filteredAvailable else filteredAvailable.take(INITIAL_LIMIT)
    val installedRemaining = filteredInstalled.size - INITIAL_LIMIT
    val availableRemaining = filteredAvailable.size - INITIAL_LIMIT

    // ── Install / uninstall ──────────────────────────────────────────────────
    fun handleInstall(pkgName: String) {
        val target = extensions.firstOrNull { it.packageName == pkgName }
        val alreadyInstalledSystemWide = target?.isInstaled == true
        scope.launch {
            actionLoading = pkgName
            val result = runCatching {
                if (alreadyInstalledSystemWide) api?.enablePackageForMe(pkgName)
                else api?.installProvider(pkgName)
            }
            if (result.isSuccess) {
                // Optimistic update
                val i = extensions.indexOfFirst { it.packageName == pkgName }
                if (i >= 0) extensions[i] = extensions[i].copy(isInstaled = true, isEnabledForMe = true)
                searchTerm = ""
                if (target != null) showPreferencesFor = target.packageName to target.name
            } else {
                snackbar.showSnackbar("Failed to install source")
            }
            actionLoading = null
        }
    }

    fun handleUninstall(pkgName: String) {
        scope.launch {
            actionLoading = pkgName
            val result = runCatching { api?.disablePackageForMe(pkgName) }
            if (result.isSuccess) {
                // Optimistic update — the source stays installed instance-wide.
                val i = extensions.indexOfFirst { it.packageName == pkgName }
                if (i >= 0) extensions[i] = extensions[i].copy(isEnabledForMe = false)
                searchTerm = ""
            } else {
                snackbar.showSnackbar("Failed to remove source")
            }
            actionLoading = null
        }
    }

    if (loading) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("Loading sources…", style = MaterialTheme.typography.bodySmall, color = RenzoColors.MutedForeground)
        }
        return
    }

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 32.dp),
    ) {
        // The web takes its search term from the global command bar; there is no
        // command-bar search on a phone, so this renders the very same search
        // field sources-list.tsx uses in its embedded (dialog) mode.
        item(key = "search") {
            SourcesSearchField(value = searchTerm, onValueChange = { searchTerm = it })
            Spacer(Modifier.height(16.dp))
        }

        item(key = "extension-versions") {
            ExtensionVersionsPanel(api, snackbar)
            Spacer(Modifier.height(24.dp))
        }

        // ── Installed section ────────────────────────────────────────────────
        if (filteredInstalled.isNotEmpty()) {
            item(key = "installed-header") {
                SourcesSectionHeader("Installed", filteredInstalled.size)
            }
            val showAll = !expandedInstalled && installedRemaining > 0
            itemsIndexed(visibleInstalled, key = { _, ext -> "i-" + ext.packageName }) { index, ext ->
                SourceRow(
                    extension = ext,
                    baseUrl = baseUrl,
                    installed = true,
                    isLoading = actionLoading == ext.packageName,
                    isFirst = index == 0,
                    isLast = !showAll && index == visibleInstalled.lastIndex,
                    onInstall = { handleInstall(it) },
                    onUninstall = { handleUninstall(it) },
                    onOpenSettings = { showPreferencesFor = ext.packageName to ext.name },
                )
            }
            if (showAll) {
                item(key = "installed-show-all") {
                    ShowAllFooter("Show all ${filteredInstalled.size} installed sources") {
                        expandedInstalled = true
                    }
                }
            }
            item(key = "installed-gap") { Spacer(Modifier.height(32.dp)) }
        }

        // ── Filter toolbar ───────────────────────────────────────────────────
        item(key = "toolbar") {
            SourcesToolbar(
                hideNsfw = hideNsfw,
                onToggleNsfw = { hideNsfw = !hideNsfw },
                selectedLanguages = selectedLanguages,
                sort = sort,
                onSort = { sort = it },
                nsfwVisibility = nsfwVisibility,
                onOpenLanguages = { langSheetOpen = true },
                onInstallFromApk = { apkPicker.launch("*/*") },
            )
            Spacer(Modifier.height(16.dp))
        }

        // ── Available section ────────────────────────────────────────────────
        if (filteredAvailable.isNotEmpty()) {
            item(key = "available-header") {
                SourcesSectionHeader("Available", filteredAvailable.size)
            }
            val showAll = !expandedAvailable && availableRemaining > 0
            itemsIndexed(visibleAvailable, key = { _, ext -> "a-" + ext.packageName }) { index, ext ->
                SourceRow(
                    extension = ext,
                    baseUrl = baseUrl,
                    installed = false,
                    isLoading = actionLoading == ext.packageName,
                    isFirst = index == 0,
                    isLast = !showAll && index == visibleAvailable.lastIndex,
                    onInstall = { handleInstall(it) },
                    onUninstall = { handleUninstall(it) },
                    onOpenSettings = { showPreferencesFor = ext.packageName to ext.name },
                )
            }
            if (showAll) {
                item(key = "available-show-all") {
                    ShowAllFooter("Browse all ${filteredAvailable.size} available sources") {
                        expandedAvailable = true
                    }
                }
            }
        }

        // ── Empty state ──────────────────────────────────────────────────────
        if (filteredInstalled.isEmpty() && filteredAvailable.isEmpty()) {
            item(key = "empty") {
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 48.dp),
                ) {
                    if (searchTerm.isNotBlank()) {
                        Text(
                            "No sources found matching “$searchTerm”.",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                        )
                        Text(
                            "View all sources",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.Primary,
                            modifier = Modifier.padding(top = 4.dp).clickable { searchTerm = "" },
                        )
                    } else {
                        Text(
                            "No sources available.",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                        )
                    }
                }
            }
        }

        // ── APK upload indicator ─────────────────────────────────────────────
        if (isUploadingApk) {
            item(key = "apk-progress") {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.Center,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                ) {
                    CircularProgressIndicator(
                        strokeWidth = 2.dp,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier.size(16.dp),
                    )
                    Text(
                        "Installing APK…",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
            }
        }
    }

    // ── Mobile language sheet ────────────────────────────────────────────────
    if (langSheetOpen) {
        val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
        ModalBottomSheet(
            onDismissRequest = { langSheetOpen = false },
            sheetState = sheetState,
            containerColor = RenzoColors.Popover,
        ) {
            Column(modifier = Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
                Text(
                    "Filter by language",
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.SemiBold,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.padding(bottom = 16.dp),
                )
                val allSelected = availableLanguageOptions.isNotEmpty() &&
                    selectedLanguages.size == availableLanguageOptions.size
                if (availableLanguageOptions.size > 1) {
                    LanguageCheckRow("Select All", allSelected, bold = true) {
                        if (allSelected) selectedLanguages.clear()
                        else {
                            selectedLanguages.clear()
                            selectedLanguages.addAll(availableLanguageOptions)
                        }
                    }
                    HorizontalDivider(color = SourcesLine)
                }
                Column(modifier = Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) {
                    availableLanguageOptions.forEach { lang ->
                        LanguageCheckRow(lang.uppercase(), lang in selectedLanguages) {
                            if (lang in selectedLanguages) selectedLanguages.remove(lang)
                            else selectedLanguages.add(lang)
                        }
                    }
                }
            }
        }
    }

    // ── Auto-open preferences after install / from the row menu ──────────────
    showPreferencesFor?.let { (pkgName, providerName) ->
        ProviderPreferencesDialog(
            api = api,
            pkgName = pkgName,
            providerName = providerName,
            onDismiss = { showPreferencesFor = null },
        )
    }
}

/** sources-list.tsx embedded search — `<Search/>` + `<Input placeholder="Search sources…" className="pl-9" />`. */
@Composable
private fun SourcesSearchField(value: String, onValueChange: (String) -> Unit) {
    val shape = RoundedCornerShape(6.dp)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .height(36.dp)
            .clip(shape)
            .background(RenzoColors.Background)
            .border(1.dp, RenzoColors.Border, shape)
            .padding(horizontal = 12.dp),
    ) {
        Icon(
            Icons.Filled.Search,
            contentDescription = null,
            tint = RenzoColors.MutedForeground,
            modifier = Modifier.size(16.dp),
        )
        Box(modifier = Modifier.padding(start = 8.dp).weight(1f)) {
            if (value.isEmpty()) {
                Text(
                    "Search sources…",
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.MutedForeground,
                )
            }
            BasicTextField(
                value = value,
                onValueChange = onValueChange,
                singleLine = true,
                textStyle = MaterialTheme.typography.bodyMedium.copy(color = RenzoColors.Foreground),
                cursorBrush = SolidColor(RenzoColors.Primary),
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }
}

@Composable
private fun LanguageCheckRow(label: String, checked: Boolean, bold: Boolean = false, onToggle: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().clickable { onToggle() }.padding(vertical = 4.dp),
    ) {
        Checkbox(
            checked = checked,
            onCheckedChange = { onToggle() },
            colors = CheckboxDefaults.colors(
                checkedColor = RenzoColors.Primary,
                uncheckedColor = RenzoColors.MutedForeground,
                checkmarkColor = RenzoColors.PrimaryForeground,
            ),
        )
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = if (bold) FontWeight.Medium else FontWeight.Normal,
            color = RenzoColors.Foreground,
            modifier = Modifier.padding(start = 4.dp),
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// sources-section.tsx / sources-list.tsx card chrome
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun SourcesSectionHeader(title: String, count: Int) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().padding(bottom = 10.dp),
    ) {
        Text(
            title,
            fontSize = 17.sp,
            fontWeight = FontWeight.Bold,
            color = RenzoColors.Foreground,
        )
        Box(Modifier.padding(start = 10.dp)) { SectionCountBadge(count) }
    }
}

/**
 * `.src-list` frame drawn per row so the list can stay lazy — left/right edges
 * on every row, a top edge on the first, and a bottom line that doubles as the
 * `.src-row` separator (and as the card's bottom edge on the last row).
 */
private fun Modifier.srcListFrame(isFirst: Boolean): Modifier = this.drawBehind {
    val w = 1.dp.toPx()
    drawRect(SourcesLine, topLeft = Offset(0f, 0f), size = Size(w, size.height))
    drawRect(SourcesLine, topLeft = Offset(size.width - w, 0f), size = Size(w, size.height))
    if (isFirst) drawRect(SourcesLine, topLeft = Offset(0f, 0f), size = Size(size.width, w))
    drawRect(SourcesLine, topLeft = Offset(0f, size.height - w), size = Size(size.width, w))
}

private fun srcListShape(isFirst: Boolean, isLast: Boolean) = when {
    isFirst && isLast -> RoundedCornerShape(14.dp)
    isFirst -> RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp)
    isLast -> RoundedCornerShape(bottomStart = 14.dp, bottomEnd = 14.dp)
    else -> RectangleShape
}

/** `.src-show-all` — the full-width footer button inside the list card. */
@Composable
private fun ShowAllFooter(label: String, onClick: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
        modifier = Modifier
            .fillMaxWidth()
            .clip(srcListShape(isFirst = false, isLast = true))
            .background(SourcesBgCard)
            .srcListFrame(isFirst = false)
            .clickable { onClick() }
            .padding(12.dp),
    ) {
        Text(
            label,
            fontSize = 13.sp,
            fontWeight = FontWeight.SemiBold,
            color = RenzoColors.MutedForeground,
        )
        Icon(
            Icons.AutoMirrored.Filled.ArrowForward,
            contentDescription = null,
            tint = RenzoColors.MutedForeground,
            modifier = Modifier.padding(start = 6.dp).size(14.dp),
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// source-row.tsx + row-actions-*.tsx
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun SourceRow(
    extension: ProviderDto,
    baseUrl: String,
    installed: Boolean,
    isLoading: Boolean,
    isFirst: Boolean,
    isLast: Boolean,
    onInstall: (String) -> Unit,
    onUninstall: (String) -> Unit,
    onOpenSettings: () -> Unit,
) {
    val isFailing = installed && (extension.isBroken || extension.isDead)
    val meta = formatLanguageMeta(extension)
    val language = primaryLanguage(extension)

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clip(srcListShape(isFirst, isLast))
            .background(if (isFailing) SourcesFailingSoft else SourcesBgCard)
            .srcListFrame(isFirst)
            .padding(horizontal = 16.dp, vertical = 13.dp),
    ) {
        SourceThumb(extension, baseUrl)

        Column(modifier = Modifier.padding(start = 14.dp).weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (isFailing) {
                    FailDot()
                    Spacer(Modifier.size(6.dp))
                }
                Text(
                    extension.name,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = RenzoColors.Foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                Text(
                    flagEmoji(countryCodeForLanguage(language)),
                    fontSize = 12.sp,
                    modifier = Modifier.padding(start = 6.dp),
                )
                if (isExtensionNsfw(extension)) {
                    Box(Modifier.padding(start = 6.dp)) { NsfwPill() }
                }
            }
            if (meta.isNotEmpty()) {
                Text(
                    meta,
                    fontSize = 12.sp,
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
            if (isFailing) {
                Text(
                    "Source is broken or unreachable",
                    fontSize = 11.sp,
                    color = SourcesFailing,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
        }

        Box(modifier = Modifier.padding(start = 6.dp)) {
            if (installed) {
                InstalledRowActions(
                    extensionName = extension.name,
                    isLoading = isLoading,
                    onSettings = onOpenSettings,
                    onUninstall = { onUninstall(extension.packageName) },
                )
            } else {
                InstallButton(isLoading = isLoading) { onInstall(extension.packageName) }
            }
        }
    }
}

/** `.btn-src-install` — the pink primary pill. */
@Composable
private fun InstallButton(isLoading: Boolean, onClick: () -> Unit) {
    val shape = RoundedCornerShape(50)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
        modifier = Modifier
            .height(32.dp)
            .clip(shape)
            .background(RenzoColors.Primary)
            .clickable(enabled = !isLoading) { onClick() }
            .padding(horizontal = 14.dp),
    ) {
        if (isLoading) {
            CircularProgressIndicator(
                strokeWidth = 2.dp,
                color = RenzoColors.PrimaryForeground,
                modifier = Modifier.size(14.dp),
            )
        } else {
            Icon(
                Icons.Filled.Add,
                contentDescription = null,
                tint = RenzoColors.PrimaryForeground,
                modifier = Modifier.size(14.dp),
            )
        }
        Text(
            "Install",
            fontSize = 13.sp,
            fontWeight = FontWeight.Bold,
            color = RenzoColors.PrimaryForeground,
            modifier = Modifier.padding(start = 6.dp),
        )
    }
}

/** The web's below-md treatment: one ⋮ button with Settings… / Uninstall. */
@Composable
private fun InstalledRowActions(
    extensionName: String,
    isLoading: Boolean,
    onSettings: () -> Unit,
    onUninstall: () -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    Box {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(32.dp)
                .clip(RoundedCornerShape(8.dp))
                .clickable(enabled = !isLoading) { open = true },
        ) {
            if (isLoading) {
                CircularProgressIndicator(
                    strokeWidth = 2.dp,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
            } else {
                Icon(
                    Icons.Filled.MoreVert,
                    contentDescription = "$extensionName options",
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
            }
        }
        DropdownMenu(
            expanded = open,
            onDismissRequest = { open = false },
            containerColor = RenzoColors.Popover,
        ) {
            DropdownMenuItem(
                text = { Text("Settings…", color = RenzoColors.Foreground) },
                leadingIcon = {
                    Icon(Icons.Filled.Settings, contentDescription = null, tint = RenzoColors.Foreground)
                },
                onClick = { open = false; onSettings() },
            )
            HorizontalDivider(color = SourcesLine)
            DropdownMenuItem(
                text = { Text("Uninstall", color = RenzoColors.Red) },
                leadingIcon = {
                    Icon(Icons.Filled.Delete, contentDescription = null, tint = RenzoColors.Red)
                },
                onClick = { open = false; onUninstall() },
            )
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// sources-toolbar.tsx
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun SourcesToolbar(
    hideNsfw: Boolean,
    onToggleNsfw: () -> Unit,
    selectedLanguages: List<String>,
    sort: String,
    onSort: (String) -> Unit,
    nsfwVisibility: String,
    onOpenLanguages: () -> Unit,
    onInstallFromApk: () -> Unit,
) {
    val sortLabel = if (sort == SORT_NAME_ASC) "A–Z" else "Z–A"
    val langLabel = when {
        selectedLanguages.isEmpty() -> "Languages"
        selectedLanguages.size <= 3 -> selectedLanguages.joinToString(", ") { it.uppercase() }
        else -> "${selectedLanguages.size} langs"
    }
    var sortOpen by remember { mutableStateOf(false) }
    var overflowOpen by remember { mutableStateOf(false) }

    Column {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp),
        ) {
            SrcChip(
                label = langLabel,
                icon = Icons.Filled.Language,
                isOn = selectedLanguages.isNotEmpty(),
                onClick = onOpenLanguages,
            )
            if (nsfwVisibility != NSFW_ALWAYS_HIDE) {
                SrcChip(
                    label = "NSFW",
                    icon = Icons.Filled.Warning,
                    isOn = !hideNsfw,
                    onClick = onToggleNsfw,
                )
            }
            Box {
                SrcChip(
                    label = "Sort: $sortLabel",
                    icon = Icons.Filled.SortByAlpha,
                    isOn = false,
                    onClick = { sortOpen = true },
                )
                DropdownMenu(
                    expanded = sortOpen,
                    onDismissRequest = { sortOpen = false },
                    containerColor = RenzoColors.Popover,
                ) {
                    DropdownMenuItem(
                        text = {
                            Text(
                                "Name A–Z",
                                color = RenzoColors.Foreground,
                                fontWeight = if (sort == SORT_NAME_ASC) FontWeight.SemiBold else FontWeight.Normal,
                            )
                        },
                        onClick = { sortOpen = false; onSort(SORT_NAME_ASC) },
                    )
                    DropdownMenuItem(
                        text = {
                            Text(
                                "Name Z–A",
                                color = RenzoColors.Foreground,
                                fontWeight = if (sort == SORT_NAME_DESC) FontWeight.SemiBold else FontWeight.Normal,
                            )
                        },
                        onClick = { sortOpen = false; onSort(SORT_NAME_DESC) },
                    )
                }
            }
            Spacer(Modifier.weight(1f))
            Box {
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .size(32.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .clickable { overflowOpen = true },
                ) {
                    Icon(
                        Icons.Filled.MoreVert,
                        contentDescription = "More options",
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.size(16.dp),
                    )
                }
                DropdownMenu(
                    expanded = overflowOpen,
                    onDismissRequest = { overflowOpen = false },
                    containerColor = RenzoColors.Popover,
                ) {
                    DropdownMenuItem(
                        text = { Text("Install from APK…", color = RenzoColors.Foreground) },
                        leadingIcon = {
                            Icon(Icons.Filled.FileUpload, contentDescription = null, tint = RenzoColors.Foreground)
                        },
                        onClick = { overflowOpen = false; onInstallFromApk() },
                    )
                }
            }
        }
        HorizontalDivider(color = Color.White.copy(alpha = 0.05f))
    }
}
