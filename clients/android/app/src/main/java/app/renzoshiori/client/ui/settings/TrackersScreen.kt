package app.renzoshiori.client.ui.settings

import android.content.Intent
import android.net.Uri
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.VpnKey
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.ComicVineApiKeyDto
import app.renzoshiori.client.data.model.ConfirmMatchRequestDto
import app.renzoshiori.client.data.model.KitsuDirectAuthDto
import app.renzoshiori.client.data.model.MangaDexDirectAuthDto
import app.renzoshiori.client.data.model.ScrobblerConfigDto
import app.renzoshiori.client.data.model.ScrobblerConfigUpdateDto
import app.renzoshiori.client.data.model.ScrobblerProvider
import app.renzoshiori.client.data.model.ScrobblerSearchResultDto
import app.renzoshiori.client.data.model.SeriesMatchSearchDto
import app.renzoshiori.client.data.model.SeriesMatchStatusDto
import app.renzoshiori.client.data.model.scrobblerRouteName
import app.renzoshiori.client.data.model.scrobblerShortIcon
import app.renzoshiori.client.data.network.ScrobblerApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * Account menu → "Trackers…". The same ScrobblerSettings block the web renders
 * inside /account's Scrobbler section, on its own screen.
 */
@Composable
fun TrackersScreen(onBack: () -> Unit) {
    val snackbar = remember { SnackbarHostState() }
    SettingsScaffold(title = "Trackers", onBack = onBack, snackbar = snackbar) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
        ) {
            SettingsCard(
                title = "Scrobbler",
                description = "Link external trackers (AniList, MyAnimeList, Kitsu, MangaDex) to " +
                    "sync your reading progress. Your connections are private to your account — " +
                    "no other user can see them.",
            ) {
                ScrobblerSettings(snackbar)
            }
            Spacer(Modifier.height(24.dp))
        }
    }
}

/**
 * Transliteration of
 * RenzoFrontend/src/components/comp/scrobbler/scrobbler-settings.tsx.
 *
 * Native OAuth differs from the web in exactly one way, and it's a
 * simplification: the web has to redirect its own window and resume from
 * localStorage on return, because a WebView can't do popups. Here the authorize
 * URL opens in the system browser, this screen never unloads, and we poll
 * `GET /api/scrobbler/callback/{Provider}?state=…` until the proxy hands the
 * tokens over — no redirect capture, no custom scheme.
 */
@Composable
fun ScrobblerSettings(snackbar: SnackbarHostState) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var configs by remember { mutableStateOf<List<ScrobblerConfigDto>?>(null) }
    var unmatched by remember { mutableStateOf<List<SeriesMatchStatusDto>>(emptyList()) }
    var connecting by remember { mutableStateOf<Int?>(null) }
    var syncing by remember { mutableStateOf(false) }
    var matchingAll by remember { mutableStateOf(false) }
    var matchTarget by remember { mutableStateOf<SeriesMatchStatusDto?>(null) }

    var comicVineApiKey by remember { mutableStateOf("") }
    var kitsuEmail by remember { mutableStateOf("") }
    var kitsuPassword by remember { mutableStateOf("") }
    var mdUsername by remember { mutableStateOf("") }
    var mdPassword by remember { mutableStateOf("") }
    var mdClientId by remember { mutableStateOf("") }
    var mdClientSecret by remember { mutableStateOf("") }

    suspend fun refresh() {
        val api = app.network.currentServiceOf<ScrobblerApi>() ?: return
        runCatching { api.configs() }
            .onSuccess { configs = it }
            .onFailure {
                configs = emptyList()
                snackbar.showSnackbar(it.apiMessage("Couldn't load scrobbler settings"))
            }
        unmatched = runCatching { api.unmatched() }.getOrDefault(emptyList())
    }

    LaunchedEffect(Unit) { refresh() }

    if (configs == null) {
        LoadingBlock("Loading scrobbler settings...")
        return
    }

    val unmatchedCount = unmatched.count { it.mappingStatus == 0 }

    Column(modifier = Modifier.fillMaxWidth()) {
        // ── Header + library-wide actions ───────────────────────────────
        Text("Scrobbler / Tracking", style = MaterialTheme.typography.titleLarge, color = RenzoColors.Foreground)
        Text(
            "Connect your reading progress to external tracking services",
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
            modifier = Modifier.padding(top = 2.dp, bottom = 12.dp),
        )
        RenzoButton(
            text = "Track all series",
            icon = Icons.Filled.Checklist,
            variant = "outline",
            busy = matchingAll,
            small = true,
            modifier = Modifier.fillMaxWidth(),
            onClick = {
                val connected = configs.orEmpty().filter { it.isConnected }
                if (connected.isEmpty()) {
                    scope.launch { snackbar.showSnackbar("Connect a tracker below first.") }
                } else {
                    matchingAll = true
                    scope.launch {
                        val api = app.network.currentServiceOf<ScrobblerApi>()
                        connected.forEach { config ->
                            runCatching { api?.autoMatchAll(config.provider) }
                        }
                        matchingAll = false
                        snackbar.showSnackbar("Matching your whole library to your trackers…")
                        refresh()
                    }
                }
            },
        )
        Spacer(Modifier.height(8.dp))
        RenzoButton(
            text = "Sync All",
            icon = Icons.Filled.Refresh,
            variant = "outline",
            busy = syncing,
            small = true,
            modifier = Modifier.fillMaxWidth(),
            onClick = {
                syncing = true
                scope.launch {
                    val api = app.network.currentServiceOf<ScrobblerApi>()
                    runCatching { api?.triggerSync() }
                        .onFailure { snackbar.showSnackbar(it.apiMessage("Sync failed")) }
                    syncing = false
                    refresh()
                }
            },
        )

        CardDivider()

        // ── Provider cards ──────────────────────────────────────────────
        configs.orEmpty().forEach { config ->
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 12.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                    .background(RenzoColors.Card)
                    .padding(14.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .size(40.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .background(RenzoColors.Primary.copy(alpha = 0.1f)),
                    ) {
                        Text(
                            scrobblerShortIcon(config.provider),
                            style = MaterialTheme.typography.labelLarge,
                            color = RenzoColors.Primary,
                        )
                    }
                    Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                        Text(
                            config.displayName,
                            style = MaterialTheme.typography.titleMedium,
                            color = RenzoColors.Foreground,
                        )
                        Spacer(Modifier.height(4.dp))
                        if (config.isConnected) {
                            RenzoBadge("Connected", RenzoColors.Primary, filled = true)
                        } else {
                            RenzoBadge("Disconnected", RenzoColors.MutedForeground)
                        }
                    }
                }

                // Toggles only exist for a connected tracker — the web dims
                // them when disconnected; here they simply aren't there yet.
                if (config.isConnected) {
                    Spacer(Modifier.height(12.dp))
                    SwitchRow(
                        checked = config.isEnabled,
                        label = "Enabled",
                        onCheckedChange = { next ->
                            configs = configs.orEmpty().map {
                                if (it.provider == config.provider) it.copy(isEnabled = next) else it
                            }
                            scope.launch {
                                val api = app.network.currentServiceOf<ScrobblerApi>()
                                runCatching {
                                    api?.updateConfig(config.provider, ScrobblerConfigUpdateDto(isEnabled = next))
                                }.onFailure { snackbar.showSnackbar(it.apiMessage("Couldn't update the tracker")) }
                                refresh()
                            }
                        },
                    )
                    SwitchRow(
                        checked = config.autoSync,
                        label = "Auto Sync",
                        onCheckedChange = { next ->
                            configs = configs.orEmpty().map {
                                if (it.provider == config.provider) it.copy(autoSync = next) else it
                            }
                            scope.launch {
                                val api = app.network.currentServiceOf<ScrobblerApi>()
                                runCatching {
                                    api?.updateConfig(config.provider, ScrobblerConfigUpdateDto(autoSync = next))
                                }.onFailure { snackbar.showSnackbar(it.apiMessage("Couldn't update the tracker")) }
                                refresh()
                            }
                        },
                    )
                    config.lastSyncAt?.let { iso ->
                        Text(
                            "Last sync: ${formatShortDate(iso)}",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.padding(bottom = 8.dp),
                        )
                    }
                    RenzoButton(
                        text = "Disconnect",
                        icon = Icons.Filled.LinkOff,
                        variant = "destructive",
                        small = true,
                        onClick = {
                            scope.launch {
                                val api = app.network.currentServiceOf<ScrobblerApi>()
                                runCatching { api?.disconnect(scrobblerRouteName(config.provider)) }
                                    .onFailure { snackbar.showSnackbar(it.apiMessage("Disconnect failed")) }
                                refresh()
                            }
                        },
                    )
                } else {
                    Spacer(Modifier.height(12.dp))
                    when {
                        config.supportsDirectAuth && config.provider == ScrobblerProvider.KITSU -> {
                            LabelledField("Email", kitsuEmail, { kitsuEmail = it }, placeholder = "Email")
                            LabelledField("Password", kitsuPassword, { kitsuPassword = it }, placeholder = "Password", password = true)
                            if (kitsuEmail.isNotBlank() && kitsuPassword.isNotBlank()) {
                                RenzoButton(
                                    text = "Connect",
                                    icon = Icons.Filled.Link,
                                    small = true,
                                    busy = connecting == config.provider,
                                    onClick = {
                                        connecting = config.provider
                                        scope.launch {
                                            val api = app.network.currentServiceOf<ScrobblerApi>()
                                            runCatching {
                                                api?.kitsuDirect(KitsuDirectAuthDto(kitsuEmail, kitsuPassword))
                                            }
                                                .onSuccess { kitsuEmail = ""; kitsuPassword = "" }
                                                .onFailure { snackbar.showSnackbar(it.apiMessage("Kitsu login failed")) }
                                            connecting = null
                                            refresh()
                                        }
                                    },
                                )
                            }
                        }

                        config.supportsDirectAuth && config.provider == ScrobblerProvider.MANGADEX -> {
                            Text(
                                "Create personal API client on MangaDex",
                                style = MaterialTheme.typography.bodySmall,
                                color = RenzoColors.Blue,
                                modifier = Modifier
                                    .clickable {
                                        context.startActivity(
                                            Intent(Intent.ACTION_VIEW, Uri.parse("https://mangadex.org/settings"))
                                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                                        )
                                    }
                                    .padding(bottom = 10.dp),
                            )
                            LabelledField("Username", mdUsername, { mdUsername = it }, placeholder = "Username")
                            LabelledField("Password", mdPassword, { mdPassword = it }, placeholder = "Password", password = true)
                            LabelledField("Client ID", mdClientId, { mdClientId = it }, placeholder = "Client ID")
                            LabelledField("Client Secret", mdClientSecret, { mdClientSecret = it }, placeholder = "Client Secret", password = true)
                            if (mdUsername.isNotBlank() && mdPassword.isNotBlank() &&
                                mdClientId.isNotBlank() && mdClientSecret.isNotBlank()
                            ) {
                                RenzoButton(
                                    text = "Connect",
                                    icon = Icons.Filled.Link,
                                    small = true,
                                    busy = connecting == config.provider,
                                    onClick = {
                                        connecting = config.provider
                                        scope.launch {
                                            val api = app.network.currentServiceOf<ScrobblerApi>()
                                            runCatching {
                                                api?.mangaDexDirect(
                                                    MangaDexDirectAuthDto(mdUsername, mdPassword, mdClientId, mdClientSecret),
                                                )
                                            }
                                                .onSuccess { mdUsername = ""; mdPassword = ""; mdClientId = ""; mdClientSecret = "" }
                                                .onFailure { snackbar.showSnackbar(it.apiMessage("MangaDex login failed")) }
                                            connecting = null
                                            refresh()
                                        }
                                    },
                                )
                            }
                        }

                        config.provider == ScrobblerProvider.COMIC_VINE -> {
                            LabelledField(
                                label = "API key",
                                value = comicVineApiKey,
                                onValueChange = { comicVineApiKey = it },
                                placeholder = "Enter ComicVine API key",
                                password = true,
                            )
                            if (comicVineApiKey.isNotBlank()) {
                                RenzoButton(
                                    text = "Save Key",
                                    icon = Icons.Filled.VpnKey,
                                    small = true,
                                    onClick = {
                                        scope.launch {
                                            val api = app.network.currentServiceOf<ScrobblerApi>()
                                            runCatching { api?.comicVineApiKey(ComicVineApiKeyDto(comicVineApiKey.trim())) }
                                                .onSuccess { comicVineApiKey = "" }
                                                .onFailure { snackbar.showSnackbar(it.apiMessage("Couldn't save the API key")) }
                                            refresh()
                                        }
                                    },
                                )
                            }
                        }

                        else -> RenzoButton(
                            text = if (connecting == config.provider) "Connecting…" else "Connect",
                            icon = Icons.Filled.Link,
                            small = true,
                            busy = connecting == config.provider,
                            onClick = {
                                // Re-entrancy guard: a second authorize during
                                // the redirect/poll window issues a second
                                // `state`, and whichever one loses the race is
                                // rejected as "authorization session not found".
                                if (connecting != null) return@RenzoButton
                                connecting = config.provider
                                scope.launch {
                                    val api = app.network.currentServiceOf<ScrobblerApi>()
                                    val name = scrobblerRouteName(config.provider)
                                    val authorized = runCatching { api?.authorize(name) }.getOrNull()
                                    if (authorized == null || authorized.authUrl.isBlank()) {
                                        connecting = null
                                        snackbar.showSnackbar("Couldn't start the ${config.displayName} sign-in.")
                                    } else {
                                        runCatching {
                                            context.startActivity(
                                                Intent(Intent.ACTION_VIEW, Uri.parse(authorized.authUrl))
                                                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                                            )
                                        }
                                        // The proxy holds the tokens keyed by
                                        // `state`; a failure just means "not
                                        // ready yet", so keep asking.
                                        var connected = false
                                        var attempt = 0
                                        while (!connected && attempt < 60) {
                                            val result = runCatching { api?.pollCallback(name, authorized.state) }.getOrNull()
                                            if (result?.connected == true) {
                                                connected = true
                                            } else {
                                                attempt++
                                                delay(2000)
                                            }
                                        }
                                        connecting = null
                                        if (connected) {
                                            runCatching { api?.autoMatchAll(config.provider) }
                                            runCatching { api?.triggerSync() }
                                            snackbar.showSnackbar("${config.displayName} connected.")
                                        } else {
                                            snackbar.showSnackbar("${config.displayName} didn't finish connecting in time.")
                                        }
                                        refresh()
                                    }
                                }
                            },
                        )
                    }
                }
            }
        }

        CardDivider()

        // ── Unmatched series ────────────────────────────────────────────
        Text("Unmatched Series", style = MaterialTheme.typography.titleMedium, color = RenzoColors.Foreground)
        Text(
            if (unmatchedCount > 0) "$unmatchedCount series need manual matching" else "All series are matched",
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
            modifier = Modifier.padding(top = 2.dp, bottom = 10.dp),
        )
        if (unmatchedCount > 0) {
            RenzoButton(
                text = "Auto-Match All",
                icon = Icons.Filled.Refresh,
                variant = "outline",
                small = true,
                busy = matchingAll,
                modifier = Modifier.fillMaxWidth(),
                onClick = {
                    matchingAll = true
                    scope.launch {
                        val api = app.network.currentServiceOf<ScrobblerApi>()
                        ScrobblerProvider.all.forEach { provider ->
                            runCatching { api?.autoMatchAll(provider) }
                        }
                        matchingAll = false
                        refresh()
                    }
                },
            )
            Spacer(Modifier.height(12.dp))
        }

        unmatched.filter { it.mappingStatus != 2 }.take(20).forEach { status ->
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 8.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border.copy(alpha = 0.5f), RoundedCornerShape(8.dp))
                    .padding(horizontal = 12.dp, vertical = 10.dp),
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        status.seriesTitle,
                        style = MaterialTheme.typography.bodyMedium,
                        color = RenzoColors.Foreground,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
                        Text(
                            scrobblerRouteName(status.provider),
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.padding(end = 8.dp),
                        )
                        when (status.mappingStatus) {
                            0 -> RenzoBadge("Not matched", RenzoColors.MutedForeground)
                            1 -> RenzoBadge(
                                "Auto-matched (${((status.matchScore ?: 0.0) * 100).toInt()}%)",
                                RenzoColors.Primary,
                                filled = true,
                            )
                            3 -> RenzoBadge("Disabled", RenzoColors.MutedForeground)
                        }
                    }
                }
                RenzoButton(
                    text = "Match",
                    icon = Icons.AutoMirrored.Filled.OpenInNew,
                    variant = "ghost",
                    small = true,
                    onClick = { matchTarget = status },
                )
            }
        }
    }

    matchTarget?.let { target ->
        SeriesMatchDialog(
            target = target,
            snackbar = snackbar,
            onDismiss = { matchTarget = null },
            onConfirmed = {
                matchTarget = null
                scope.launch { refresh() }
            },
        )
    }
}

/**
 * series-match-dialog.tsx: search the tracker for the right title and confirm
 * the link. Without it the "Match" button would be a dead end, which is the one
 * thing this port must never ship.
 */
@Composable
private fun SeriesMatchDialog(
    target: SeriesMatchStatusDto,
    snackbar: SnackbarHostState,
    onDismiss: () -> Unit,
    onConfirmed: () -> Unit,
) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    var query by remember { mutableStateOf(target.seriesTitle) }
    var results by remember { mutableStateOf<List<ScrobblerSearchResultDto>?>(null) }
    var searching by remember { mutableStateOf(false) }

    suspend fun search() {
        searching = true
        val api = app.network.currentServiceOf<ScrobblerApi>()
        runCatching { api?.searchExternal(SeriesMatchSearchDto(target.provider, query.trim())) }
            .onSuccess { results = it?.results ?: emptyList() }
            .onFailure {
                results = emptyList()
                snackbar.showSnackbar(it.apiMessage("Search failed"))
            }
        searching = false
    }

    LaunchedEffect(target.seriesId, target.provider) { search() }

    RenzoDialog(
        onDismiss = onDismiss,
        title = "Match “${target.seriesTitle}”",
        description = "Pick the ${scrobblerRouteName(target.provider)} entry this series should sync with.",
    ) {
        RenzoTextField(value = query, onValueChange = { query = it }, placeholder = "Search title")
        Spacer(Modifier.height(10.dp))
        RenzoButton(
            text = "Search",
            small = true,
            busy = searching,
            modifier = Modifier.fillMaxWidth(),
            onClick = { scope.launch { search() } },
        )
        Spacer(Modifier.height(12.dp))

        when {
            results == null -> LoadingBlock("Searching…")
            results!!.isEmpty() -> EmptyNote("No results — try a different title.")
            else -> Column(modifier = Modifier.fillMaxWidth().heightIn(max = 300.dp).verticalScroll(rememberScrollState())) {
                results!!.forEach { result ->
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(bottom = 6.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                            .clickable {
                                scope.launch {
                                    val api = app.network.currentServiceOf<ScrobblerApi>()
                                    runCatching {
                                        api?.confirmMatch(
                                            ConfirmMatchRequestDto(
                                                seriesId = target.seriesId,
                                                provider = target.provider,
                                                externalSeriesId = result.externalId,
                                                externalSeriesTitle = result.title,
                                            ),
                                        )
                                    }
                                        .onSuccess { onConfirmed() }
                                        .onFailure { snackbar.showSnackbar(it.apiMessage("Couldn't save the match")) }
                                }
                            }
                            .padding(horizontal = 12.dp, vertical = 10.dp),
                    ) {
                        Text(
                            result.title,
                            style = MaterialTheme.typography.bodyMedium,
                            color = RenzoColors.Foreground,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                        )
                        if (!result.type.isNullOrBlank()) {
                            Text(
                                result.type!!,
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.MutedForeground,
                            )
                        }
                    }
                }
            }
        }

        Spacer(Modifier.height(12.dp))
        Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
            RenzoButton("Cancel", variant = "outline", onClick = onDismiss)
        }
    }
}

/** Backend DateTimes are UTC, sometimes without an explicit offset. */
internal fun formatShortDate(iso: String): String = runCatching {
    val normalized = if (iso.endsWith("Z") || iso.contains("+")) iso else "${iso}Z"
    Instant.parse(normalized)
        .atZone(ZoneId.systemDefault())
        .format(DateTimeFormatter.ofPattern("d MMM yyyy"))
}.getOrElse { iso.take(10) }
