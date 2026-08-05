package app.renzoshiori.client.ui.status

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Circle
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.Timeline
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.RssFeed
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.TextUnitType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.ClearAlertRequestDto
import app.renzoshiori.client.data.model.HealthStatusLevel
import app.renzoshiori.client.data.model.HealthStatusTargetType
import app.renzoshiori.client.data.model.ProviderHealthDto
import app.renzoshiori.client.data.model.SeriesHealthDto
import app.renzoshiori.client.data.model.SetCadenceRequestDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.data.network.StatusApi
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.home.DpadSegmentedPills
import app.renzoshiori.client.ui.home.DpadToggleChip
import app.renzoshiori.client.ui.home.dpadClickable
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import coil3.compose.AsyncImage
import kotlinx.coroutines.launch

// ── Tailwind literals used by the web page, as Compose colors ────────────────
private val Red400 = Color(0xFFF87171)
private val Amber400 = Color(0xFFFBBF24)
private val Red500 = Color(0xFFEF4444)
private val Amber500 = Color(0xFFF59E0B)
private val White02 = Color.White.copy(alpha = 0.02f)      // bg-white/[0.02]
private val White03 = Color.White.copy(alpha = 0.03f)      // bg-white/[0.03]
private val White06 = Color.White.copy(alpha = 0.06f)      // border-white/[0.06]
private val White10 = Color.White.copy(alpha = 0.10f)      // border-white/10
private val White015 = Color.White.copy(alpha = 0.015f)    // bg-white/[0.015]

private const val TAB_PROVIDERS = "providers"
private const val TAB_SERIES = "series"

/**
 * Status — a 1:1 transliteration of RenzoFrontend src/app/status/page.tsx plus
 * src/components/comp/status. Four glass summary tiles (Series Warnings /
 * Series Critical / Source Warnings / Source Critical), the Sources|Series
 * segmented control, and the two alert panels: collapsible provider cards with
 * their affected-series list, and series cards with the admin cadence editor.
 *
 * The web lays the tiles out side by side (sm:grid-cols-2 lg:grid-cols-4) and
 * puts each card's action cluster on the right; both become vertical stacks
 * here. Everything else — wording, ordering, colours, sort — is unchanged.
 *
 * [onOpenSeries] is optional so the shell can call `StatusScreen()`; wiring it
 * makes the series titles (which carry the web's ↗ external-link affordance)
 * navigate to the series detail route.
 */
@Composable
fun StatusScreen(onOpenSeries: (String) -> Unit = {}) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val api = remember { app.network.currentServiceOf<StatusApi>() }
    val baseUrl = app.tokenStore.serverUrl ?: ""
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }

    var activeTab by remember { mutableStateOf(TAB_PROVIDERS) }
    var level by remember { mutableStateOf(UserLevel.USER) }
    val canAdmin = level >= UserLevel.ADMIN
    val canOwner = level >= UserLevel.OWNER

    // Each user's health alerts are isolated to their own library. Owner-level
    // accounts can flip to every user's alerts for support/troubleshooting.
    var viewAllLibraries by remember { mutableStateOf(false) }
    val effectiveViewAll = canOwner && viewAllLibraries

    var series by remember { mutableStateOf<List<SeriesHealthDto>?>(null) }
    var providers by remember { mutableStateOf<List<ProviderHealthDto>?>(null) }
    var reloadKey by remember { mutableStateOf(0) }

    LaunchedEffect(Unit) {
        level = runCatching { api?.me()?.level }.getOrNull() ?: UserLevel.USER
    }
    LaunchedEffect(effectiveViewAll, reloadKey) {
        series = null
        providers = null
        val flag = if (effectiveViewAll) true else null

        val seriesResult = runCatching { api?.seriesStatus(flag) ?: emptyList() }
        series = seriesResult.getOrDefault(emptyList())
        if (seriesResult.isFailure) snackbar.showSnackbar("Failed to load series status")

        val providersResult = runCatching { api?.providerStatus(flag) ?: emptyList() }
        providers = providersResult.getOrDefault(emptyList())
        if (providersResult.isFailure) snackbar.showSnackbar("Failed to load source status")
    }

    val clearAlert: (Int, String) -> Unit = { targetType, targetId ->
        scope.launch {
            val result = runCatching { api?.clearAlert(ClearAlertRequestDto(targetType, targetId)) }
            if (result.isSuccess) reloadKey++ else snackbar.showSnackbar("Failed to dismiss the alert")
        }
    }

    val seriesWarnings = series?.count { it.level == HealthStatusLevel.YELLOW } ?: 0
    val seriesCritical = series?.count { it.level == HealthStatusLevel.RED } ?: 0
    val sourceWarnings = providers?.count { it.level == HealthStatusLevel.YELLOW } ?: 0
    val sourceCritical = providers?.count { it.level == HealthStatusLevel.RED } ?: 0

    Box(modifier = Modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 12.dp, bottom = 28.dp),
        ) {
            // ── Ribbon: Status · Health of your sources and series ────────────
            item(key = "ribbon") {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    Icon(
                        Icons.Filled.Timeline,
                        contentDescription = null,
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.size(16.dp),
                    )
                    Text(
                        "Status",
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = RenzoColors.Foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                    Text(
                        "· Health of your sources and series",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 8.dp).weight(1f),
                    )
                    if (canOwner) {
                        LibraryScopeToggle(
                            viewAll = viewAllLibraries,
                            onToggle = { viewAllLibraries = !viewAllLibraries },
                        )
                    }
                }
                Spacer(Modifier.height(24.dp))
            }

            // ── Summary tiles (grid → vertical stack) ─────────────────────────
            item(key = "tiles") {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    StatTile("Series Warnings", seriesWarnings, tone = TONE_WARNING)
                    StatTile("Series Critical", seriesCritical, tone = TONE_CRITICAL)
                    StatTile("Source Warnings", sourceWarnings, tone = TONE_WARNING)
                    StatTile("Source Critical", sourceCritical, tone = TONE_CRITICAL)
                }
                Spacer(Modifier.height(24.dp))
            }

            // ── Tab selector ──────────────────────────────────────────────────
            item(key = "tabs") {
                SegmentedTabs(
                    value = activeTab,
                    onChange = { activeTab = it },
                    providerCount = providers?.size ?: 0,
                    seriesCount = series?.size ?: 0,
                )
                Spacer(Modifier.height(24.dp))
            }

            // ── Active panel ──────────────────────────────────────────────────
            if (activeTab == TAB_PROVIDERS) {
                val list = providers
                when {
                    list == null -> item(key = "skeleton-p") { PanelSkeleton(rows = 2) }
                    list.isEmpty() -> item(key = "empty-p") {
                        HealthyEmptyState("All sources are healthy", "No source alerts at this time")
                    }
                    else -> {
                        // Sort: Red first, then Yellow (verbatim from the web).
                        val sorted = list.sortedBy { it.level }
                        items(sorted, key = { "p-" + it.providerId }) { provider ->
                            ProviderCard(
                                provider = provider,
                                onClearAlert = clearAlert,
                                canAdmin = canAdmin,
                                baseUrl = baseUrl,
                                onOpenSeries = onOpenSeries,
                            )
                            Spacer(Modifier.height(12.dp))
                        }
                    }
                }
            } else {
                val list = series
                when {
                    list == null -> item(key = "skeleton-s") { PanelSkeleton(rows = 3) }
                    list.isEmpty() -> item(key = "empty-s") {
                        HealthyEmptyState("All series are healthy", "No series alerts at this time")
                    }
                    else -> {
                        val sorted = list.sortedBy { it.level }
                        items(sorted, key = { "s-" + it.id }) { s ->
                            SeriesAlertCard(
                                series = s,
                                onClearAlert = clearAlert,
                                canAdmin = canAdmin,
                                baseUrl = baseUrl,
                                onOpenSeries = onOpenSeries,
                                api = api,
                                onSaved = { reloadKey++ },
                                onError = { scope.launch { snackbar.showSnackbar(it) } },
                            )
                            Spacer(Modifier.height(8.dp))
                        }
                    }
                }
            }
        }
        SnackbarHost(snackbar, modifier = Modifier.align(Alignment.BottomCenter))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Ribbon toggle — "My library" / "All libraries" (Owner only)
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun LibraryScopeToggle(viewAll: Boolean, onToggle: () -> Unit) {
    if (LocalIsTv.current) {
        DpadToggleChip(
            label = if (viewAll) "All libraries" else "My library",
            active = viewAll,
            onClick = onToggle,
        )
        return
    }
    val shape = RoundedCornerShape(50)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .clip(shape)
            .background(if (viewAll) RenzoColors.Primary.copy(alpha = 0.15f) else Color.White.copy(alpha = 0.04f))
            .border(
                1.dp,
                if (viewAll) RenzoColors.Primary.copy(alpha = 0.40f) else RenzoColors.Border.copy(alpha = 0.40f),
                shape,
            )
            .clickable { onToggle() }
            .padding(horizontal = 12.dp, vertical = 4.dp),
    ) {
        Text(
            if (viewAll) "All libraries" else "My library",
            fontSize = 11.sp,
            fontWeight = FontWeight.Medium,
            color = if (viewAll) RenzoColors.Primary else RenzoColors.MutedForeground,
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Summary tile
// ─────────────────────────────────────────────────────────────────────────────

private const val TONE_WARNING = "warning"
private const val TONE_CRITICAL = "critical"

/** Glass summary tile — muted when the count is zero, accent-lit when there are alerts. */
@Composable
private fun StatTile(label: String, value: Int, tone: String) {
    val active = value > 0
    val accentText = if (tone == TONE_CRITICAL) Red400 else Amber400
    val ring = if (tone == TONE_CRITICAL) Red500.copy(alpha = 0.30f) else Amber500.copy(alpha = 0.30f)
    val shape = RoundedCornerShape(12.dp)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(White02)
            .border(1.dp, if (active) ring else White06, shape)
            .padding(16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text(
                label.uppercase(),
                fontSize = 10.5.sp,
                fontWeight = FontWeight.Medium,
                letterSpacing = TextUnit(1.7f, TextUnitType.Sp),
                color = RenzoColors.MutedForeground,
                modifier = Modifier.weight(1f),
            )
            Icon(
                if (tone == TONE_CRITICAL) Icons.Filled.RssFeed else Icons.Filled.Warning,
                contentDescription = null,
                tint = if (active) accentText else RenzoColors.MutedForeground.copy(alpha = 0.60f),
                modifier = Modifier.size(16.dp),
            )
        }
        Text(
            value.toString(),
            fontFamily = FontFamily.Monospace,
            fontSize = 30.sp,
            fontWeight = FontWeight.SemiBold,
            color = if (active) accentText else RenzoColors.MutedForeground.copy(alpha = 0.50f),
            modifier = Modifier.padding(top = 8.dp),
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Segmented tabs
// ─────────────────────────────────────────────────────────────────────────────

/** Sources / Series segmented control — matches the queue page's filter pills. */
@Composable
private fun SegmentedTabs(
    value: String,
    onChange: (String) -> Unit,
    providerCount: Int,
    seriesCount: Int,
) {
    // The open tab is *selected*, not focused: on TV it keeps its accent and
    // check mark while the cursor moves off it (§2.1 of the TV spec).
    if (LocalIsTv.current) {
        val ids = listOf(TAB_PROVIDERS, TAB_SERIES)
        DpadSegmentedPills(
            labels = listOf("Sources", "Series"),
            counts = listOf(providerCount, seriesCount),
            selectedIndex = ids.indexOf(value).coerceAtLeast(0),
            onSelect = { onChange(ids[it]) },
        )
        return
    }
    val outer = RoundedCornerShape(50)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
        modifier = Modifier
            .clip(outer)
            .background(White015)
            .border(1.dp, White06, outer)
            .padding(2.dp),
    ) {
        listOf(TAB_PROVIDERS to ("Sources" to providerCount), TAB_SERIES to ("Series" to seriesCount))
            .forEach { (id, labelAndCount) ->
                val (label, count) = labelAndCount
                val isActive = value == id
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(outer)
                        .background(if (isActive) RenzoColors.Primary else Color.Transparent)
                        .clickable { onChange(id) }
                        .padding(horizontal = 14.dp, vertical = 5.dp),
                ) {
                    Text(
                        label,
                        fontSize = 12.5.sp,
                        fontWeight = FontWeight.Medium,
                        color = if (isActive) RenzoColors.PrimaryForeground else RenzoColors.MutedForeground,
                    )
                    if (count > 0) {
                        Text(
                            count.toString(),
                            fontSize = 11.sp,
                            color = if (isActive) RenzoColors.PrimaryForeground.copy(alpha = 0.70f)
                            else RenzoColors.MutedForeground.copy(alpha = 0.60f),
                            modifier = Modifier.padding(start = 4.dp),
                        )
                    }
                }
            }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared bits (alert badge, meta pill, dismiss, skeleton, empty state)
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun AlertBadge(level: Int, modifier: Modifier = Modifier) {
    when (level) {
        HealthStatusLevel.GREEN -> Icon(
            Icons.Filled.CheckCircle, contentDescription = null,
            tint = RenzoColors.Green, modifier = modifier.size(12.dp),
        )
        HealthStatusLevel.YELLOW -> Icon(
            Icons.Filled.Warning, contentDescription = null,
            tint = RenzoColors.Yellow, modifier = modifier.size(12.dp),
        )
        else -> Icon(
            Icons.Filled.Circle, contentDescription = null,
            tint = RenzoColors.Red, modifier = modifier.size(12.dp),
        )
    }
}

/** Small translucent meta pill (provider tags, days-without-release, counts). */
@Composable
private fun MetaPill(text: String) {
    val shape = RoundedCornerShape(50)
    Box(
        modifier = Modifier
            .clip(shape)
            .background(White03)
            .border(1.dp, White10, shape)
            .padding(horizontal = 8.dp, vertical = 2.dp),
    ) {
        Text(text, fontSize = 11.sp, fontWeight = FontWeight.Medium, color = RenzoColors.MutedForeground)
    }
}

/** Glass dismiss action — replaces the old shadcn outline Button. */
@Composable
private fun DismissButton(onClick: () -> Unit) {
    val shape = RoundedCornerShape(6.dp)
    Box(
        modifier = Modifier
            .clip(shape)
            .background(White03)
            .border(1.dp, White10, shape)
            .dpadClickable(radius = 6.dp, fill = null) { onClick() }
            .padding(horizontal = 10.dp, vertical = 4.dp),
    ) {
        Text(
            "DISMISS",
            fontSize = 11.sp,
            fontWeight = FontWeight.Medium,
            letterSpacing = TextUnit(0.9f, TextUnitType.Sp),
            color = RenzoColors.MutedForeground,
        )
    }
}

@Composable
private fun PanelSkeleton(rows: Int) {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        repeat(rows) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(80.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(White02)
                    .border(1.dp, White06, RoundedCornerShape(12.dp))
                    .alpha(0.6f),
            )
        }
    }
}

@Composable
private fun HealthyEmptyState(title: String, subtitle: String) {
    val shape = RoundedCornerShape(12.dp)
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(White02)
            .border(1.dp, White06, shape)
            .padding(vertical = 48.dp),
    ) {
        Icon(
            Icons.Filled.CheckCircle,
            contentDescription = null,
            tint = RenzoColors.Green,
            modifier = Modifier.size(48.dp),
        )
        Text(
            title,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.Foreground,
            modifier = Modifier.padding(top = 16.dp),
        )
        Text(
            subtitle,
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
        )
    }
}

/** Accent border for a card based on its health level. */
private fun levelAccent(level: Int): Color =
    if (level == HealthStatusLevel.RED) Red500.copy(alpha = 0.25f) else Amber500.copy(alpha = 0.25f)

@Composable
private fun SeriesTitleLink(title: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier.dpadClickable(radius = 6.dp) { onClick() },
    ) {
        Text(
            title,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            color = RenzoColors.Foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f, fill = false),
        )
        Icon(
            Icons.Filled.OpenInNew,
            contentDescription = null,
            tint = RenzoColors.MutedForeground,
            modifier = Modifier.padding(start = 4.dp).size(12.dp),
        )
    }
}

@Composable
private fun SeriesThumb(thumbnailUrl: String?, baseUrl: String, w: Int, h: Int, radius: Int) {
    Box(
        modifier = Modifier
            .width(w.dp)
            .height(h.dp)
            .clip(RoundedCornerShape(radius.dp))
            .background(RenzoColors.Card),
    ) {
        if (!thumbnailUrl.isNullOrEmpty()) {
            AsyncImage(
                model = absoluteUrl(baseUrl, thumbnailUrl),
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Provider panel
// ─────────────────────────────────────────────────────────────────────────────

@Composable
private fun ProviderCard(
    provider: ProviderHealthDto,
    onClearAlert: (Int, String) -> Unit,
    canAdmin: Boolean,
    baseUrl: String,
    onOpenSeries: (String) -> Unit,
) {
    var isOpen by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(12.dp)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(White02)
            .border(1.dp, levelAccent(provider.level), shape),
    ) {
        Column(modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .dpadClickable(radius = 8.dp) { isOpen = !isOpen },
            ) {
                Icon(
                    if (isOpen) Icons.Filled.KeyboardArrowDown else Icons.Filled.KeyboardArrowRight,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.size(16.dp),
                )
                // Source icon as thumbnail
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .padding(start = 12.dp)
                        .size(36.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(White03)
                        .border(1.dp, White06, RoundedCornerShape(8.dp)),
                ) {
                    Icon(
                        Icons.Filled.Storage,
                        contentDescription = null,
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.size(20.dp),
                    )
                }
                Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        AlertBadge(provider.level)
                        Text(
                            provider.providerName,
                            style = MaterialTheme.typography.bodyMedium,
                            fontWeight = FontWeight.Medium,
                            color = RenzoColors.Foreground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(start = 8.dp),
                        )
                    }
                    Text(
                        buildString {
                            append(provider.language)
                            if (provider.scanlator.isNotEmpty()) append(" · ${provider.scanlator}")
                            if (provider.consecutiveErrors > 0) append(" · ${provider.consecutiveErrors} errors")
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            // Right-hand action cluster (pills + Dismiss) — stacked under the
            // header on mobile instead of beside it.
            if (!provider.isMihonInstalled || provider.affectedSeries.isNotEmpty() || canAdmin) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.padding(start = 36.dp, top = 8.dp),
                ) {
                    if (!provider.isMihonInstalled) MetaPill("User Provider")
                    if (provider.affectedSeries.isNotEmpty()) MetaPill("${provider.affectedSeries.size} series")
                    if (canAdmin) {
                        DismissButton { onClearAlert(HealthStatusTargetType.PROVIDER, provider.providerId) }
                    }
                }
            }
            Text(
                provider.message,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(start = 36.dp, top = 4.dp),
            )
        }
        if (provider.affectedSeries.isNotEmpty() && isOpen) {
            // border-t border-white/[0.06]
            Box(Modifier.fillMaxWidth().height(1.dp).background(White06))
            Column(
                verticalArrangement = Arrangement.spacedBy(4.dp),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp),
            ) {
                provider.affectedSeries.forEach { s ->
                    AffectedSeriesRow(
                        series = s,
                        onClearAlert = onClearAlert,
                        canAdmin = canAdmin,
                        baseUrl = baseUrl,
                        onOpenSeries = onOpenSeries,
                    )
                }
            }
        }
    }
}

@Composable
private fun AffectedSeriesRow(
    series: SeriesHealthDto,
    onClearAlert: (Int, String) -> Unit,
    canAdmin: Boolean,
    baseUrl: String,
    onOpenSeries: (String) -> Unit,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        SeriesThumb(series.thumbnailUrl, baseUrl, w = 36, h = 48, radius = 4)
        Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                AlertBadge(series.level)
                SeriesTitleLink(
                    title = series.title,
                    onClick = { onOpenSeries(series.id) },
                    modifier = Modifier.padding(start = 8.dp),
                )
            }
            Text(
                series.message,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            series.daysWithoutRelease?.let { MetaPill("${it}d") }
            if (canAdmin) {
                DismissButton { onClearAlert(HealthStatusTargetType.SERIES, series.id) }
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Series panel
// ─────────────────────────────────────────────────────────────────────────────

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SeriesAlertCard(
    series: SeriesHealthDto,
    onClearAlert: (Int, String) -> Unit,
    canAdmin: Boolean,
    baseUrl: String,
    onOpenSeries: (String) -> Unit,
    api: StatusApi?,
    onSaved: () -> Unit,
    onError: (String) -> Unit,
) {
    val scope = rememberCoroutineScope()
    val keyboard = LocalSoftwareKeyboardController.current
    // Local cadence input + saving state for this series (the web keeps these
    // in Record<seriesId, …> maps owned by the panel).
    var input by remember(series.id) { mutableStateOf("") }
    var isSaving by remember(series.id) { mutableStateOf(false) }

    val parsed = input.trim().toIntOrNull()
    val hasValidInput = input.isNotEmpty() && parsed != null && parsed > 0

    fun saveCadence() {
        if (!hasValidInput || isSaving) return
        isSaving = true
        keyboard?.hide()
        scope.launch {
            runCatching { api?.setCadence(series.id, SetCadenceRequestDto(parsed)) }
                .onSuccess { input = ""; onSaved() }
                .onFailure { onError("Failed to save the cadence") }
            isSaving = false
        }
    }

    val shape = RoundedCornerShape(12.dp)
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(White02)
            .border(1.dp, levelAccent(series.level), shape)
            .padding(horizontal = 16.dp, vertical = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            SeriesThumb(series.thumbnailUrl, baseUrl, w = 48, h = 64, radius = 6)
            Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    AlertBadge(series.level)
                    SeriesTitleLink(
                        title = series.title,
                        onClick = { onOpenSeries(series.id) },
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
                Text(
                    series.message,
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 2.dp),
                )
                if (series.providers.isNotEmpty()) {
                    FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                        verticalArrangement = Arrangement.spacedBy(4.dp),
                        modifier = Modifier.padding(top = 6.dp),
                    ) {
                        series.providers.forEach { p ->
                            MetaPill("${p.providerName} (${p.language})")
                        }
                    }
                }
            }
        }

        // Actions — cadence edit (admin) + days badge + dismiss. The web keeps
        // these in a right-hand column; on a phone they stack underneath.
        Column(
            horizontalAlignment = Alignment.End,
            verticalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
        ) {
            if (canAdmin) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "Cadence:",
                        fontSize = 11.sp,
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                    )
                    CadenceInput(
                        value = input,
                        placeholder = series.releaseCadenceDays?.toString() ?: "auto",
                        onValueChange = { typed -> input = typed.filter { c -> c.isDigit() } },
                        onDone = { saveCadence() },
                        modifier = Modifier.padding(start = 6.dp),
                    )
                    SaveCadenceButton(
                        enabled = hasValidInput && !isSaving,
                        saving = isSaving,
                        onClick = { saveCadence() },
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }
            }
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                series.daysWithoutRelease?.let { MetaPill("${it}d") }
                if (canAdmin) {
                    DismissButton { onClearAlert(HealthStatusTargetType.SERIES, series.id) }
                }
            }
        }
    }
}

/** The web's `<Input type="number" className="h-7 w-16 text-right font-mono text-xs" />`. */
@Composable
private fun CadenceInput(
    value: String,
    placeholder: String,
    onValueChange: (String) -> Unit,
    onDone: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val shape = RoundedCornerShape(6.dp)
    var focused by remember { mutableStateOf(false) }
    val isTv = LocalIsTv.current
    Box(
        contentAlignment = Alignment.CenterEnd,
        modifier = modifier
            .width(if (isTv) 84.dp else 64.dp)
            .height(if (isTv) 36.dp else 28.dp)
            .clip(shape)
            .background(RenzoColors.Background)
            .border(1.dp, RenzoColors.Border, shape)
            .focusRing(isTv && focused, 6.dp)
            .padding(horizontal = 8.dp),
    ) {
        if (value.isEmpty()) {
            Text(
                placeholder,
                fontFamily = FontFamily.Monospace,
                fontSize = 12.sp,
                color = RenzoColors.MutedForeground,
                textAlign = TextAlign.End,
                modifier = Modifier.fillMaxWidth(),
            )
        }
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            singleLine = true,
            textStyle = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 12.sp,
                color = RenzoColors.Foreground,
                textAlign = TextAlign.End,
            ),
            cursorBrush = SolidColor(RenzoColors.Primary),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { onDone() }),
            modifier = Modifier.fillMaxWidth().onFocusChanged { focused = it.isFocused },
        )
    }
}

@Composable
private fun SaveCadenceButton(
    enabled: Boolean,
    saving: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val shape = RoundedCornerShape(6.dp)
    Box(
        modifier = modifier
            .clip(shape)
            .background(White03)
            .border(1.dp, White10, shape)
            .dpadClickable(radius = 6.dp, enabled = enabled, fill = null) { onClick() }
            .alpha(if (enabled) 1f else 0.4f)
            .padding(horizontal = 10.dp, vertical = 4.dp),
    ) {
        Text(
            if (saving) "…" else "SAVE",
            fontSize = 11.sp,
            fontWeight = FontWeight.Medium,
            letterSpacing = TextUnit(0.9f, TextUnitType.Sp),
            color = RenzoColors.MutedForeground,
        )
    }
}
