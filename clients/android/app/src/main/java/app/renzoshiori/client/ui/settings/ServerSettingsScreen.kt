package app.renzoshiori.client.ui.settings

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Hub
import androidx.compose.material.icons.filled.Power
import androidx.compose.material.icons.filled.Save
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.NsfwVisibility
import app.renzoshiori.client.data.model.ServerSettingsDto
import app.renzoshiori.client.data.model.TestEmailRequestDto
import app.renzoshiori.client.data.network.ServerSettingsApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch

/**
 * The server-wide Settings page — transliteration of
 * RenzoFrontend/src/components/comp/settings-manager.tsx (all eight sections)
 * with settings-section-nav.tsx's narrow-screen drawer as the section picker.
 *
 * The whole DTO is round-tripped on save: PUT /api/settings binds onto a fresh
 * object server-side, so anything not sent reverts to a C# default. GET is open
 * to any signed-in user; PUT is Owner-only and the server's own 403 message is
 * surfaced verbatim if a non-owner ever reaches it.
 *
 * Two deliberate departures from the web, both required by the brief:
 * side-by-side grids stack vertically, and the preferred-language list reorders
 * with arrow buttons instead of drag-and-drop.
 */
@Composable
fun ServerSettingsScreen(onBack: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }

    var settings by remember { mutableStateOf<ServerSettingsDto?>(null) }
    var languages by remember { mutableStateOf<List<String>>(emptyList()) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }
    var activeSection by remember { mutableStateOf("security") }

    LaunchedEffect(Unit) {
        val api = app.network.currentServiceOf<ServerSettingsApi>()
        runCatching { api?.settings() }
            .onSuccess { settings = it }
            .onFailure { loadError = it.apiMessage("Failed to load settings") }
        languages = runCatching { api?.languages() }.getOrNull() ?: emptyList()
    }

    val sections = listOf(
        SettingsNavSection("security", "Security", Icons.Filled.Security),
        SettingsNavSection("content-preferences", "Content Preferences", Icons.Filled.AutoAwesome),
        SettingsNavSection("mihon-repositories", "Mihon Repositories", Icons.Filled.Power),
        SettingsNavSection("download-settings", "Download Settings", Icons.Filled.CloudDownload),
        SettingsNavSection("schedule-tasks", "Schedule Tasks", Icons.Filled.Schedule),
        SettingsNavSection("storage", "Storage", Icons.Filled.FolderOpen),
        SettingsNavSection("flaresolverr", "FlareSolverr Settings", Icons.Filled.Shield),
        SettingsNavSection("socks-settings", "Socks Settings", Icons.Filled.Hub),
    )
    val descriptions = mapOf(
        "security" to "Configure authentication and security settings.",
        "content-preferences" to "Configure your languages and content filters.",
        "mihon-repositories" to "Configure external repositories for additional sources.",
        "download-settings" to "Configure download behavior and limits.",
        "schedule-tasks" to "Configure automatic update schedules and timings.",
        "storage" to "Configure how archives are stored and organized.",
        "flaresolverr" to "Configure FlareSolverr for bypassing Cloudflare protection.",
        "socks-settings" to "Configure SOCKS proxy settings for sources requests.",
    )

    SettingsScaffold(title = "Settings", onBack = onBack, snackbar = snackbar) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
        ) {
            Text(
                "Configure your Renzo Shiori application settings",
                style = MaterialTheme.typography.bodyMedium,
                color = RenzoColors.MutedForeground,
            )
            Spacer(Modifier.height(12.dp))

            val current = settings
            when {
                loadError != null -> ErrorBox(loadError!!)
                current == null -> LoadingBlock("Loading settings...")
                else -> {
                    RenzoButton(
                        text = if (saving) "Saving..." else "Save Settings",
                        icon = Icons.Filled.Save,
                        busy = saving,
                        modifier = Modifier.fillMaxWidth(),
                        onClick = {
                            val domain = current.externalDomain.orEmpty()
                            if (domain.isNotEmpty() && !Regex("^https?://").containsMatchIn(domain)) {
                                scope.launch {
                                    snackbar.showSnackbar(
                                        "External Domain is missing the URL schema (e.g. https://)",
                                    )
                                }
                            } else {
                                saving = true
                                scope.launch {
                                    runCatching {
                                        app.network.currentServiceOf<ServerSettingsApi>()
                                            ?.updateSettings(current)
                                    }
                                        .onSuccess { response ->
                                            // Turning authentication on for an
                                            // account with no password hands
                                            // back a set-password URL — follow
                                            // it or the owner locks themselves
                                            // out on the next request.
                                            val setPasswordUrl = response?.setPasswordUrl
                                            if (!setPasswordUrl.isNullOrBlank()) {
                                                runCatching {
                                                    context.startActivity(
                                                        Intent(Intent.ACTION_VIEW, Uri.parse(setPasswordUrl))
                                                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                                                    )
                                                }
                                            }
                                            snackbar.showSnackbar(
                                                response?.message ?: "Settings saved successfully",
                                            )
                                        }
                                        .onFailure {
                                            snackbar.showSnackbar(it.apiMessage("Failed to save settings"))
                                        }
                                    saving = false
                                }
                            }
                        },
                    )
                    Spacer(Modifier.height(14.dp))

                    SettingsSectionNav(
                        sections = sections,
                        activeId = activeSection,
                        onChange = { activeSection = it },
                        drawerTitle = "Settings",
                    )
                    Spacer(Modifier.height(14.dp))

                    val title = sections.firstOrNull { it.id == activeSection }?.title ?: ""
                    SettingsCard(title = title, description = descriptions[activeSection]) {
                        val update: (ServerSettingsDto) -> Unit = { settings = it }
                        when (activeSection) {
                            "security" -> SecuritySection(current, update, snackbar)
                            "content-preferences" -> ContentPreferencesSection(current, update, languages)
                            "mihon-repositories" -> MihonRepositoriesSection(current, update)
                            "download-settings" -> DownloadSettingsSection(current, update)
                            "schedule-tasks" -> ScheduleTasksSection(current, update)
                            "storage" -> StorageSection(current, update)
                            "flaresolverr" -> FlareSolverrSection(current, update)
                            "socks-settings" -> SocksSettingsSection(current, update)
                        }
                    }
                }
            }
            Spacer(Modifier.height(28.dp))
        }
    }
}

// ── Security ─────────────────────────────────────────────────────────────

@Composable
private fun ColumnScope.SecuritySection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
    snackbar: SnackbarHostState,
) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()

    SwitchRow(
        checked = s.authenticationEnabled ?: false,
        onCheckedChange = { update(s.copy(authenticationEnabled = it)) },
        label = "Enable Authentication",
    )

    val domain = s.externalDomain.orEmpty()
    LabelledField(
        label = "External Domain",
        value = domain,
        onValueChange = { update(s.copy(externalDomain = it)) },
        placeholder = "https://renzo.example.com",
        hint = "Used for invite links and OPDS URLs when accessed from outside your local network.",
    )
    if (domain.isNotEmpty() && !Regex("^https?://").containsMatchIn(domain)) {
        WarningLine("URL is missing the schema (e.g. https://)")
    }

    val originsText = s.allowedOrigins.orEmpty().joinToString("\n")
    FieldLabel("Allowed Origins (CORS)")
    RenzoTextField(
        value = originsText,
        onValueChange = { raw ->
            update(
                s.copy(
                    allowedOrigins = raw.split("\n")
                        .map { it.trim().trimEnd('/') }
                        .filter { it.isNotEmpty() },
                ),
            )
        },
        placeholder = "https://renzo.example.com",
        singleLine = false,
        minLines = 3,
    )
    val badOrigin = s.allowedOrigins.orEmpty().firstOrNull { !Regex("^https?://.").containsMatchIn(it) }
    if (badOrigin != null) {
        WarningLine("\"$badOrigin\" is not a valid origin (e.g. https://renzo.example.com)")
    }
    Hint(
        "One origin per line. Browsers may only call the API from these origins. Leave empty to " +
            "allow any origin (without credentials). Changes apply immediately, no restart needed.",
    )
    Spacer(Modifier.height(12.dp))

    NumberField(
        label = "Session Expiration (hours)",
        value = s.sessionExpirationHours ?: 24,
        min = 1, max = 8760,
        onValueChange = { update(s.copy(sessionExpirationHours = it)) },
        hint = "How long a login token stays valid.",
    )
    NumberField(
        label = "Remember Me Expiration (days)",
        value = s.rememberMeExpirationDays ?: 90,
        min = 1, max = 3650,
        onValueChange = { update(s.copy(rememberMeExpirationDays = it)) },
        hint = "How long \"Remember Me\" sessions stay signed in. The timer resets each time the " +
            "session refreshes.",
    )

    CardDivider()
    Text("Email (SMTP)", style = MaterialTheme.typography.titleSmall, color = RenzoColors.Foreground)
    Hint(
        "Outbound relay for password-reset emails (e.g. Gmail with an app password, Brevo, " +
            "SendGrid). Renzo Shiori only submits mail to the relay — it never hosts an email " +
            "server, so ISP hosting blocks don't apply. Leave the host empty to disable email " +
            "features.",
    )
    Spacer(Modifier.height(12.dp))

    LabelledField(
        label = "SMTP Host",
        value = s.smtpHost.orEmpty(),
        onValueChange = { update(s.copy(smtpHost = it.trim())) },
        placeholder = "smtp.gmail.com",
    )
    NumberField(
        label = "Port",
        value = s.smtpPort ?: 587,
        min = 1, max = 65535,
        onValueChange = { update(s.copy(smtpPort = it)) },
    )
    LabelledField(
        label = "Username",
        value = s.smtpUsername.orEmpty(),
        onValueChange = { update(s.copy(smtpUsername = it)) },
        placeholder = "you@gmail.com",
    )
    LabelledField(
        label = "Password",
        value = s.smtpPassword.orEmpty(),
        onValueChange = { update(s.copy(smtpPassword = it)) },
        placeholder = "App password / API key",
        password = true,
    )
    LabelledField(
        label = "From Address",
        value = s.smtpFromAddress.orEmpty(),
        onValueChange = { update(s.copy(smtpFromAddress = it.trim())) },
        placeholder = "renzo@yourdomain.com",
        keyboardType = KeyboardType.Email,
    )
    SwitchRow(
        checked = s.smtpUseSsl ?: true,
        onCheckedChange = { update(s.copy(smtpUseSsl = it)) },
        label = "Use TLS (STARTTLS, port 587)",
    )

    // Standalone test row — it uses the SAVED settings on the server.
    var testAddress by remember { mutableStateOf("") }
    var sending by remember { mutableStateOf(false) }
    FieldLabel("Send Test Email")
    RenzoTextField(
        value = testAddress,
        onValueChange = { testAddress = it },
        placeholder = "you@example.com",
        keyboardType = KeyboardType.Email,
    )
    Spacer(Modifier.height(8.dp))
    if (testAddress.isNotBlank()) {
        RenzoButton(
            text = if (sending) "Sending…" else "Send Test",
            variant = "secondary",
            small = true,
            busy = sending,
            onClick = {
                sending = true
                scope.launch {
                    runCatching {
                        app.network.currentServiceOf<ServerSettingsApi>()
                            ?.testEmail(TestEmailRequestDto(testAddress.trim()))
                    }
                        .onSuccess { snackbar.showSnackbar(it?.message ?: "Test email sent") }
                        .onFailure { snackbar.showSnackbar(it.apiMessage("Test email failed")) }
                    sending = false
                }
            },
        )
    }
    Hint("Uses the last saved SMTP settings — save your changes first.")
}

// ── Content preferences ──────────────────────────────────────────────────

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ColumnScope.ContentPreferencesSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
    availableLanguages: List<String>,
) {
    val preferred = s.preferredLanguages.orEmpty()

    Text("Language", style = MaterialTheme.typography.titleSmall, color = RenzoColors.Foreground)
    Hint("Ordered by preference — the first match wins when a chapter exists in several languages.")
    Spacer(Modifier.height(8.dp))

    if (preferred.isEmpty()) {
        EmptyNote("No preferred languages yet — add one below.")
    } else {
        preferred.forEachIndexed { index, language ->
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 6.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                    .background(RenzoColors.Secondary.copy(alpha = 0.4f))
                    .padding(horizontal = 10.dp, vertical = 6.dp),
            ) {
                Text(
                    "${index + 1}.",
                    style = MaterialTheme.typography.labelSmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.width(22.dp),
                )
                Text(
                    language,
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.weight(1f),
                )
                if (index > 0) {
                    IconGhostButton(Icons.Filled.ArrowUpward, "Move up", RenzoColors.MutedForeground) {
                        val next = preferred.toMutableList()
                        next.add(index - 1, next.removeAt(index))
                        update(s.copy(preferredLanguages = next))
                    }
                }
                if (index < preferred.size - 1) {
                    IconGhostButton(Icons.Filled.ArrowDownward, "Move down", RenzoColors.MutedForeground) {
                        val next = preferred.toMutableList()
                        next.add(index + 1, next.removeAt(index))
                        update(s.copy(preferredLanguages = next))
                    }
                }
                IconGhostButton(Icons.Filled.Close, "Remove $language", RenzoColors.Red) {
                    update(s.copy(preferredLanguages = preferred.filter { it != language }))
                }
            }
        }
    }

    val addable = availableLanguages.filter { it !in preferred }
    if (addable.isNotEmpty()) {
        Spacer(Modifier.height(8.dp))
        FieldLabel("Available languages (Derived from your sources):")
        FlowRow(
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            addable.forEach { language ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .padding(bottom = 6.dp)
                        .clip(RoundedCornerShape(50))
                        .border(1.dp, RenzoColors.Border, RoundedCornerShape(50))
                        .clickable { update(s.copy(preferredLanguages = preferred + language)) }
                        .padding(horizontal = 10.dp, vertical = 5.dp),
                ) {
                    Icon(
                        Icons.Filled.Add, contentDescription = null,
                        tint = RenzoColors.MutedForeground, modifier = Modifier.size(12.dp),
                    )
                    Text(
                        language,
                        style = MaterialTheme.typography.labelSmall,
                        color = RenzoColors.Foreground,
                        modifier = Modifier.padding(start = 4.dp),
                    )
                }
            }
        }
    }

    CardDivider()
    Text("NSFW", style = MaterialTheme.typography.titleSmall, color = RenzoColors.Foreground)
    Spacer(Modifier.height(6.dp))
    val nsfw = s.nsfwVisibility ?: NsfwVisibility.HIDE_BY_DEFAULT
    RadioRow(nsfw == NsfwVisibility.ALWAYS_HIDE, "Always hide", "NSFW sources are never shown") {
        update(s.copy(nsfwVisibility = NsfwVisibility.ALWAYS_HIDE))
    }
    RadioRow(
        nsfw == NsfwVisibility.HIDE_BY_DEFAULT,
        "Hide by default",
        "Hidden by default, but can be toggled in source lists",
    ) {
        update(s.copy(nsfwVisibility = NsfwVisibility.HIDE_BY_DEFAULT))
    }
    RadioRow(nsfw == NsfwVisibility.SHOW, "Show", "NSFW sources are always visible") {
        update(s.copy(nsfwVisibility = NsfwVisibility.SHOW))
    }

    CardDivider()
    SwitchRow(
        checked = s.readerEnabled != false,
        onCheckedChange = { update(s.copy(readerEnabled = it)) },
        label = "Built-in Reader",
        hint = "Read downloaded chapters in the browser (smart webtoon/long-strip/paged modes, " +
            "progress tracking, bookmarks) and preview Browse series without downloading. " +
            "Turning this off hides all Read buttons.",
    )

    CardDivider()
    SwitchRow(
        checked = s.downloadAllChapters == true,
        onCheckedChange = { update(s.copy(downloadAllChapters = it)) },
        label = "Download all chapters",
        hint = "Pull every chapter of every series, ignoring each series' start-point cutoff — " +
            "including gaps below the newest downloaded. The update scan then fills the whole " +
            "library instead of only new chapters. This can queue a very large number of downloads.",
    )
}

// ── Mihon repositories ───────────────────────────────────────────────────

@Composable
private fun ColumnScope.MihonRepositoriesSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
) {
    var newRepository by remember { mutableStateOf("") }
    val repositories = s.mihonRepositories.orEmpty()

    repositories.forEach { repository ->
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
        ) {
            Box(modifier = Modifier.weight(1f)) {
                RenzoTextField(value = repository, onValueChange = {}, readOnly = true)
            }
            Spacer(Modifier.width(8.dp))
            RenzoButton(
                text = "",
                icon = Icons.Filled.Close,
                variant = "outline",
                small = true,
                onClick = { update(s.copy(mihonRepositories = repositories.filter { it != repository })) },
            )
        }
    }

    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
        Box(modifier = Modifier.weight(1f)) {
            RenzoTextField(
                value = newRepository,
                onValueChange = { newRepository = it },
                placeholder = "Enter repository URL",
                keyboardType = KeyboardType.Uri,
            )
        }
        Spacer(Modifier.width(8.dp))
        if (isValidUrl(newRepository)) {
            RenzoButton(
                text = "",
                icon = Icons.Filled.Add,
                small = true,
                onClick = {
                    if (newRepository !in repositories) {
                        update(s.copy(mihonRepositories = repositories + newRepository))
                    }
                    newRepository = ""
                },
            )
        }
    }
}

// ── Downloads ────────────────────────────────────────────────────────────

@Composable
private fun ColumnScope.DownloadSettingsSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
) {
    NumberField(
        label = "Number of Simultaneous Downloads",
        value = s.numberOfSimultaneousDownloads ?: 10,
        min = 1, max = 20,
        onValueChange = { update(s.copy(numberOfSimultaneousDownloads = it)) },
        hint = "Maximum number of downloads that can run simultaneously",
    )
    NumberField(
        label = "Downloads Per Source",
        value = s.numberOfSimultaneousDownloadsPerProvider ?: 3,
        min = 1, max = 10,
        onValueChange = { update(s.copy(numberOfSimultaneousDownloadsPerProvider = it)) },
        hint = "Maximum number of simultaneous downloads per source",
    )
    NumberField(
        label = "Pages In Parallel (per chapter)",
        value = s.pagesInParallelPerChapter ?: 5,
        min = 1, max = 16,
        onValueChange = { update(s.copy(pagesInParallelPerChapter = it)) },
        hint = "How many page images are fetched at once within a single chapter. Pages are still " +
            "saved in order. This is the biggest lever on download speed — 1 is the old " +
            "one-page-at-a-time behaviour. Lower it for sources that rate-limit or return errors " +
            "under load.",
    )
    NumberField(
        label = "Download Memory Budget (MB)",
        value = s.downloadMemoryBudgetMB ?: 2048,
        min = 128, max = 32768,
        onValueChange = { update(s.copy(downloadMemoryBudgetMB = it)) },
        hint = "Hard ceiling on page images held in memory across all downloads at once. Fetches " +
            "wait for room instead of piling up, so high concurrency throttles itself rather than " +
            "exhausting RAM. Keep it comfortably below the container's memory limit.",
    )
    NumberField(
        label = "Max Requests Per Host (5–12)",
        value = s.maxRequestsPerHost ?: 5,
        min = 5, max = 12,
        onValueChange = { update(s.copy(maxRequestsPerHost = it)) },
        hint = "The real ceiling on per-source download speed: every request to one site shares " +
            "this budget, no matter how many chapters run at once. 5 is the default. Raise it to " +
            "drain a large backlog on a single source faster.",
        warning = "⚠ Ban risk: higher values hammer a single host harder. Values near 12 make " +
            "rate-limiting or an IP ban materially more likely on strict sources — raise this " +
            "gradually and back off if a source starts failing.",
    )
    NumberField(
        label = "Number of Simultaneous Searches",
        value = s.numberOfSimultaneousSearches ?: 10,
        min = 1, max = 20,
        onValueChange = { update(s.copy(numberOfSimultaneousSearches = it)) },
        hint = "Maximum number of searches that can run simultaneously",
    )
    TimeSpanField(
        label = "Chapter Download Retry Time",
        timeSpan = s.chapterDownloadFailRetryTime,
        hint = "How long to wait before retrying a failed chapter download",
        onChange = { update(s.copy(chapterDownloadFailRetryTime = it)) },
    )
    NumberField(
        label = "Chapter Download Max Retries",
        value = s.chapterDownloadFailRetries ?: 144,
        min = 0, max = 1000,
        onValueChange = { update(s.copy(chapterDownloadFailRetries = it)) },
        hint = "Maximum number of retry attempts for failed chapter downloads",
    )
}

// ── Schedules ────────────────────────────────────────────────────────────

@Composable
private fun ColumnScope.ScheduleTasksSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
) {
    TimeSpanField(
        label = "Per Title Update Schedule",
        timeSpan = s.perTitleUpdateSchedule,
        hint = "How often to check for updates per title",
        onChange = { update(s.copy(perTitleUpdateSchedule = it)) },
    )
    NumberField(
        label = "Library Rolling Scan (3–12 h)",
        value = s.libraryScanIntervalHours ?: 6,
        min = 3, max = 12,
        onValueChange = { update(s.copy(libraryScanIntervalHours = it)) },
        hint = "Full library check for new chapters on every source, every N hours. \"Update now\" " +
            "on the Updates page runs the same scan immediately.",
    )
    TimeSpanField(
        label = "Per Source Update Schedule",
        timeSpan = s.perSourceUpdateSchedule,
        hint = "How often to check for updates per source",
        onChange = { update(s.copy(perSourceUpdateSchedule = it)) },
    )
    TimeSpanField(
        label = "Extensions Update Check Schedule",
        timeSpan = s.extensionsCheckForUpdateSchedule,
        hint = "How often to check for extension updates",
        onChange = { update(s.copy(extensionsCheckForUpdateSchedule = it)) },
    )
}

// ── Storage ──────────────────────────────────────────────────────────────

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ColumnScope.StorageSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
) {
    var newCategory by remember { mutableStateOf("") }
    val categories = s.categories.orEmpty()

    LabelledField(
        label = "Storage Folder",
        value = s.storageFolder.orEmpty(),
        onValueChange = {},
        readOnly = true,
        hint = "Current folder where series archives are stored",
    )
    SwitchRow(
        checked = s.categorizedFolders ?: true,
        onCheckedChange = { update(s.copy(categorizedFolders = it)) },
        label = "Enable Categorized Folders",
    )

    if (s.categorizedFolders != false) {
        FieldLabel("Categories")
        Hint("Define categories for organizing series. Category will be selectable when adding series.")
        Spacer(Modifier.height(8.dp))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
            categories.forEach { category ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .padding(bottom = 6.dp)
                        .clip(RoundedCornerShape(50))
                        .background(RenzoColors.Secondary)
                        .padding(start = 10.dp, end = 4.dp, top = 4.dp, bottom = 4.dp),
                ) {
                    Text(
                        category,
                        style = MaterialTheme.typography.labelSmall,
                        color = RenzoColors.Foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    IconGhostButton(Icons.Filled.Close, "Remove $category", RenzoColors.MutedForeground) {
                        update(s.copy(categories = categories.filter { it != category }))
                    }
                }
            }
        }
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Box(modifier = Modifier.weight(1f)) {
                RenzoTextField(
                    value = newCategory,
                    onValueChange = { newCategory = it },
                    placeholder = "Enter category name",
                )
            }
            Spacer(Modifier.width(8.dp))
            if (newCategory.isNotBlank()) {
                RenzoButton(
                    text = "",
                    icon = Icons.Filled.Add,
                    small = true,
                    onClick = {
                        if (newCategory !in categories) {
                            update(s.copy(categories = categories + newCategory))
                        }
                        newCategory = ""
                    },
                )
            }
        }
    }
}

// ── FlareSolverr ─────────────────────────────────────────────────────────

@Composable
private fun ColumnScope.FlareSolverrSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
) {
    SwitchRow(
        checked = s.flareSolverrEnabled ?: false,
        onCheckedChange = { update(s.copy(flareSolverrEnabled = it)) },
        label = "Enable FlareSolverr",
    )
    if (s.flareSolverrEnabled == true) {
        LabelledField(
            label = "FlareSolverr URL",
            value = s.flareSolverrUrl.orEmpty(),
            onValueChange = { update(s.copy(flareSolverrUrl = it)) },
            placeholder = "http://localhost:8191",
            keyboardType = KeyboardType.Uri,
        )
        TimeSpanField(
            label = "FlareSolverr Timeout",
            timeSpan = s.flareSolverrTimeout,
            withSeconds = true,
            hint = "Request timeout for FlareSolverr operations",
            onChange = { update(s.copy(flareSolverrTimeout = it)) },
        )
        TimeSpanField(
            label = "Session TTL",
            timeSpan = s.flareSolverrSessionTtl,
            hint = "How long FlareSolverr sessions should remain active",
            onChange = { update(s.copy(flareSolverrSessionTtl = it)) },
        )
        SwitchRow(
            checked = s.flareSolverrAsResponseFallback ?: false,
            onCheckedChange = { update(s.copy(flareSolverrAsResponseFallback = it)) },
            label = "Use as Response Fallback",
        )
    }
}

// ── SOCKS proxy ──────────────────────────────────────────────────────────

@Composable
private fun ColumnScope.SocksSettingsSection(
    s: ServerSettingsDto,
    update: (ServerSettingsDto) -> Unit,
) {
    SwitchRow(
        checked = s.socksProxyEnabled ?: false,
        onCheckedChange = { update(s.copy(socksProxyEnabled = it)) },
        label = "Enable SOCKS Proxy",
    )
    // The web greys these out when the proxy is off; here they stay live so the
    // details can be filled in before flipping the switch.
    NumberField(
        label = "SOCKS Version",
        value = s.socksProxyVersion ?: 5,
        min = 4, max = 5,
        onValueChange = { update(s.copy(socksProxyVersion = it)) },
    )
    LabelledField(
        label = "Host",
        value = s.socksProxyHost.orEmpty(),
        onValueChange = { update(s.copy(socksProxyHost = it)) },
        placeholder = "127.0.0.1",
    )
    NumberField(
        label = "Port",
        value = s.socksProxyPort ?: 0,
        min = 0, max = 65535,
        onValueChange = { update(s.copy(socksProxyPort = it)) },
    )
    LabelledField(
        label = "Username",
        value = s.socksProxyUsername.orEmpty(),
        onValueChange = { update(s.copy(socksProxyUsername = it)) },
        placeholder = "Optional",
    )
    LabelledField(
        label = "Password",
        value = s.socksProxyPassword.orEmpty(),
        onValueChange = { update(s.copy(socksProxyPassword = it)) },
        placeholder = "Optional",
        password = true,
    )
    Hint("Configure a SOCKS4/5 proxy for provider requests.")
}

// ── Helpers ──────────────────────────────────────────────────────────────

/**
 * A TimeSpan-backed field that keeps the raw keystrokes locally and only pushes
 * the normalised "hh:mm:ss" upward. Deriving the text straight from the DTO (as
 * the web does) makes the box fight the user: typing "1" reformats to "01:00"
 * before the second digit lands.
 */
@Composable
private fun TimeSpanField(
    label: String,
    timeSpan: String?,
    onChange: (String) -> Unit,
    withSeconds: Boolean = false,
    hint: String? = null,
) {
    var text by remember {
        mutableStateOf(if (withSeconds) timeSpanToTimeInputSeconds(timeSpan) else timeSpanToTimeInput(timeSpan))
    }
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp)) {
        FieldLabel(label)
        RenzoTextField(
            value = text,
            onValueChange = { raw ->
                text = raw.filter { it.isDigit() || it == ':' }
                onChange(if (withSeconds) timeInputToTimeSpanSeconds(text) else timeInputToTimeSpan(text))
            },
            placeholder = if (withSeconds) "HH:MM:SS" else "HH:MM",
        )
        if (hint != null) Hint(hint)
    }
}

@Composable
private fun WarningLine(text: String) {
    Text(
        "⚠ $text",
        style = MaterialTheme.typography.bodySmall,
        color = RenzoColors.Amber,
        modifier = Modifier.padding(bottom = 6.dp),
    )
}

private fun isValidUrl(value: String): Boolean =
    Regex("^https?://[^\\s]+$").matches(value.trim())

/**
 * TimeSpan helpers — direct ports of settings-manager.tsx. The backend
 * serializes TimeSpans as "hh:mm:ss" (or "d.hh:mm:ss" once a day is involved),
 * which is why the day part is split off before parsing.
 */
internal fun timeSpanToTimeInput(timeSpan: String?): String {
    if (timeSpan.isNullOrEmpty()) return "00:00"
    val parts = timeSpan.split(".")
    val timePart = if (parts.size == 2 && parts[1].isNotEmpty()) parts[1] else timeSpan
    val pieces = timePart.split(":")
    val hours = pieces.getOrNull(0)?.toIntOrNull() ?: 0
    val minutes = pieces.getOrNull(1)?.toIntOrNull() ?: 0
    return "%02d:%02d".format(hours, minutes)
}

internal fun timeSpanToTimeInputSeconds(timeSpan: String?): String {
    if (timeSpan.isNullOrEmpty()) return "00:00:00"
    val parts = timeSpan.split(".")
    val timePart = if (parts.size == 2 && parts[1].isNotEmpty()) parts[1] else timeSpan
    val pieces = timePart.split(":")
    val hours = pieces.getOrNull(0)?.toIntOrNull() ?: 0
    val minutes = pieces.getOrNull(1)?.toIntOrNull() ?: 0
    val seconds = pieces.getOrNull(2)?.toIntOrNull() ?: 0
    return "%02d:%02d:%02d".format(hours, minutes, seconds)
}

internal fun timeInputToTimeSpan(timeInput: String): String {
    if (timeInput.isEmpty()) return "00:00:00"
    val pieces = timeInput.split(":")
    val hours = pieces.getOrNull(0)?.toIntOrNull() ?: 0
    val minutes = pieces.getOrNull(1)?.toIntOrNull() ?: 0
    return "%02d:%02d:00".format(hours, minutes)
}

internal fun timeInputToTimeSpanSeconds(timeInput: String): String {
    if (timeInput.isEmpty()) return "00:00:00"
    val pieces = timeInput.split(":")
    val hours = pieces.getOrNull(0)?.toIntOrNull() ?: 0
    val minutes = pieces.getOrNull(1)?.toIntOrNull() ?: 0
    val seconds = pieces.getOrNull(2)?.toIntOrNull() ?: 0
    return "%02d:%02d:%02d".format(hours, minutes, seconds)
}
