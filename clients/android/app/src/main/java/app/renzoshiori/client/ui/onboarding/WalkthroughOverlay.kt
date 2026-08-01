package app.renzoshiori.client.ui.onboarding

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.LibraryBooks
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Explore
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.RocketLaunch
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.VerifiedUser
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.boundsInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.R
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.UserDto
import app.renzoshiori.client.data.model.UserLevel
import app.renzoshiori.client.data.model.preferencesUpdateBody
import app.renzoshiori.client.data.network.OnboardingPrefsApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.intOrNull

/**
 * Bump to re-show the tour to everyone after a material change (e.g. a big new
 * feature worth re-introducing) — RenzoFrontend src/lib/utils/onboarding.ts.
 */
const val ONBOARDING_VERSION = 2

private val prefsJson = Json { ignoreUnknownKeys = true }

/** parseThemePrefs() — a tolerant parse of the per-user preferences blob. */
private fun parsePrefs(raw: String?): JsonObject {
    if (raw.isNullOrEmpty()) return JsonObject(emptyMap())
    return runCatching { prefsJson.parseToJsonElement(raw) as? JsonObject }.getOrNull()
        ?: JsonObject(emptyMap())
}

/**
 * hasSeenOnboarding() — whether this account has already completed or skipped
 * the current walkthrough. Signed-out (null) counts as seen: there is nothing
 * to show.
 */
fun hasSeenOnboarding(user: UserDto?): Boolean {
    if (user == null) return true
    val version = (parsePrefs(user.preferences)["onboardedVersion"] as? JsonPrimitive)?.intOrNull ?: 0
    return version >= ONBOARDING_VERSION
}

/**
 * Bounds of the app-chrome elements the tour spotlights, in window
 * coordinates — the native stand-in for the web's `data-tour` attributes.
 *
 * Chrome that applies [tourAnchor] is spotlighted exactly; anything that
 * doesn't falls back to the command bar's known geometry (see
 * `fallbackAnchor`), so the tour is never broken by an un-annotated screen.
 */
object TourAnchors {
    const val NAV = "nav"
    const val SEARCH = "search"
    const val ACCOUNT = "account"

    val bounds = mutableStateMapOf<String, Rect>()
}

/** Registers this element as the tour target for [id]. */
fun Modifier.tourAnchor(id: String): Modifier = this.onGloballyPositioned { coordinates ->
    TourAnchors.bounds[id] = coordinates.boundsInWindow()
}

private data class TourStep(
    val icon: ImageVector,
    val title: String,
    val body: String,
    /** Chrome id to spotlight; when absent the step is a centred card. */
    val target: String? = null,
)

private const val SPOTLIGHT_PAD_DP = 8f
private const val GAP_DP = 12f

/**
 * Interactive first-run walkthrough — the native transliteration of
 * RenzoFrontend comp/onboarding/onboarding-walkthrough.tsx. A guided tour that
 * spotlights real parts of the app (navigation, search, account) in anchored
 * windows, with clean centred cards for features that aren't a single
 * on-screen element.
 *
 * Render it on top of the current screen (e.g. as the last child of the shell's
 * root Box). Finishing or skipping persists `onboardedVersion` into the user's
 * preferences blob — merged into the existing JSON, never replacing it, because
 * that same blob carries the theme preset, accent and source-priority keys.
 */
@Composable
fun WalkthroughOverlay(onFinish: () -> Unit) {
    val context = LocalContext.current.applicationContext as RenzoApp
    val prefsApi = remember(context.network.serverUrl) {
        context.network.currentServiceOf<OnboardingPrefsApi>()
    }
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current

    var user by remember { mutableStateOf<UserDto?>(null) }
    var step by remember { mutableIntStateOf(0) }
    var closed by remember { mutableStateOf(false) }
    var cardHeightPx by remember { mutableIntStateOf(0) }

    LaunchedEffect(prefsApi) {
        runCatching { prefsApi?.me() }.onSuccess { user = it }
    }

    val canManage = (user?.level ?: 0) >= UserLevel.MANAGER
    val canAdmin = (user?.level ?: 0) >= UserLevel.ADMIN

    val steps = remember(canManage, canAdmin) {
        buildList {
            add(
                TourStep(
                    icon = Icons.Filled.Explore,
                    title = "Move around",
                    target = TourAnchors.NAV,
                    body = if (canManage) {
                        "Jump between Library, Browse, Updates, Sources and more from here. On a phone " +
                            "it's the ☰ menu; on a wide screen it's the tabs up top."
                    } else {
                        "Jump between Library, Updates and your reading from here. On a phone it's the " +
                            "☰ menu; on a wide screen it's the tabs up top."
                    },
                ),
            )
            add(
                TourStep(
                    icon = Icons.Filled.Search,
                    title = "Find anything",
                    target = TourAnchors.SEARCH,
                    body = "Search your library — or, on Browse, search straight across your enabled " +
                        "sources to discover new series. ⌘/Ctrl+K focuses it anywhere.",
                ),
            )
            add(
                TourStep(
                    icon = Icons.AutoMirrored.Filled.LibraryBooks,
                    title = "Your library",
                    body = "Everything you follow lives in Library with covers and reading progress. " +
                        "Open a series to see its chapters, resume where you left off, and manage tracking.",
                ),
            )
            add(
                TourStep(
                    icon = Icons.AutoMirrored.Filled.MenuBook,
                    title = "A reader you can tune",
                    body = "Continuous or paged, fit-to-width or original, themes, tap zones and keyboard " +
                        "shortcuts. It remembers your place and syncs progress to your trackers.",
                ),
            )
            add(
                TourStep(
                    icon = Icons.Filled.CloudDownload,
                    title = "Read offline on the go",
                    body = "In the Android and desktop apps, save chapters — or a whole series — for a " +
                        "trip. Back online, it offers to clean them up without touching what you're mid-read.",
                ),
            )
            add(
                TourStep(
                    icon = Icons.Filled.Palette,
                    title = "Everything you, in one place",
                    target = TourAnchors.ACCOUNT,
                    body = "Your account menu holds trackers (MAL/AniList/…), Appearance themes, offline " +
                        "downloads, and — any time — this tour under “Take a tour.”",
                ),
            )
            if (canAdmin) {
                add(
                    TourStep(
                        icon = Icons.Filled.VerifiedUser,
                        title = "You're an admin",
                        body = "Manage users, sources, scheduled updates and server settings from the " +
                            "account menu. Invite people — each gets their own library, progress and theme.",
                    ),
                )
            }
        }
    }

    val total = steps.size + 1 // +1 welcome
    val isWelcome = step == 0
    val isLast = step == total - 1
    val content = if (isWelcome) null else steps.getOrNull(step - 1)

    /** close() — persist that this user finished (or skipped) the walkthrough. */
    fun close() {
        if (closed) return
        closed = true
        val current = user
        val api = prefsApi
        if (current != null && api != null) {
            val prefs = parsePrefs(current.preferences)
            val seen = (prefs["onboardedVersion"] as? JsonPrimitive)?.intOrNull ?: 0
            if (seen < ONBOARDING_VERSION) {
                // Merge into the existing blob: theme/accent/priority keys live here too.
                val merged = JsonObject(
                    prefs.toMutableMap().apply {
                        put("onboardedVersion", JsonPrimitive(ONBOARDING_VERSION))
                    },
                )
                scope.launch {
                    // Non-fatal: they just might see the tour again on next load.
                    runCatching {
                        api.updateMe(preferencesUpdateBody(prefsJson.encodeToString(JsonObject.serializer(), merged)))
                    }
                }
            }
        }
        onFinish()
    }

    if (closed) return

    val screenWidthPx = context.resources.displayMetrics.widthPixels.toFloat()
    val screenHeightPx = context.resources.displayMetrics.heightPixels.toFloat()
    val marginPx = with(density) { 12.dp.toPx() }
    val padPx = with(density) { SPOTLIGHT_PAD_DP.dp.toPx() }
    val gapPx = with(density) { GAP_DP.dp.toPx() }
    val cardWidthDp = minOf(360f, (screenWidthPx / density.density) - 24f)
    val cardWidthPx = with(density) { cardWidthDp.dp.toPx() }

    // Anchoring only makes sense when the chrome is actually on screen behind the
    // overlay (i.e. something called [tourAnchor]). With no anchors registered —
    // e.g. the tour opened as its own destination — every step falls back to a
    // centred card, exactly as the web does when findTarget() finds nothing.
    val anchorsKnown = TourAnchors.bounds.isNotEmpty()
    val target = content?.target
    val anchorRect = target?.takeIf { anchorsKnown }?.let { id ->
        TourAnchors.bounds[id] ?: fallbackAnchor(id, screenWidthPx, density.density)
    }

    val spotlight = anchorRect?.let {
        Rect(it.left - padPx, it.top - padPx, it.right + padPx, it.bottom + padPx)
    }

    // Prefer below the target, flip above if it would run off the bottom.
    val measuredHeight = if (cardHeightPx > 0) cardHeightPx.toFloat() else with(density) { 240.dp.toPx() }
    val cardTop: Float
    val cardLeft: Float
    if (spotlight == null) {
        cardTop = maxOf(marginPx, (screenHeightPx - measuredHeight) / 2f)
        cardLeft = maxOf(marginPx, (screenWidthPx - cardWidthPx) / 2f)
    } else {
        val below = spotlight.bottom + gapPx
        val placeBelow = below + measuredHeight + marginPx <= screenHeightPx ||
            spotlight.top - gapPx - measuredHeight < marginPx
        cardTop = if (placeBelow) below else spotlight.top - gapPx - measuredHeight
        val targetCx = spotlight.left + spotlight.width / 2f
        cardLeft = minOf(
            maxOf(marginPx, targetCx - cardWidthPx / 2f),
            screenWidthPx - cardWidthPx - marginPx,
        )
    }

    Box(modifier = Modifier.fillMaxSize()) {
        // Backdrop — dims everything and skips on tap. When a target is spotlit
        // the dimming is drawn by the spotlight canvas instead.
        Box(
            modifier = Modifier
                .fillMaxSize()
                .clickable(
                    interactionSource = remember { MutableInteractionSource() },
                    indication = null,
                    onClick = { close() },
                )
                .background(if (spotlight == null) Color.Black.copy(alpha = 0.7f) else Color.Transparent),
        )

        if (spotlight != null) {
            val radiusPx = with(density) { 12.dp.toPx() }
            val ringPx = with(density) { 2.dp.toPx() }
            Canvas(
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen),
            ) {
                drawRect(Color.Black.copy(alpha = 0.72f))
                drawRoundRect(
                    color = Color.Transparent,
                    topLeft = Offset(spotlight.left, spotlight.top),
                    size = Size(spotlight.width, spotlight.height),
                    cornerRadius = CornerRadius(radiusPx, radiusPx),
                    blendMode = BlendMode.Clear,
                )
                drawRoundRect(
                    color = RenzoColors.Primary.copy(alpha = 0.7f),
                    topLeft = Offset(spotlight.left, spotlight.top),
                    size = Size(spotlight.width, spotlight.height),
                    cornerRadius = CornerRadius(radiusPx, radiusPx),
                    style = Stroke(width = ringPx),
                )
            }
        }

        // ── The window ───────────────────────────────────────────────────
        Column(
            modifier = Modifier
                .align(Alignment.TopStart)
                .offset { IntOffset(cardLeft.toInt(), cardTop.toInt()) }
                .width(cardWidthDp.dp)
                .clip(RoundedCornerShape(16.dp))
                .background(RenzoColors.Card)
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(16.dp))
                .onSizeChanged { cardHeightPx = it.height },
        ) {
            Box(modifier = Modifier.fillMaxWidth()) {
                Column(
                    horizontalAlignment = if (spotlight == null) {
                        Alignment.CenterHorizontally
                    } else {
                        Alignment.Start
                    },
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(start = 20.dp, end = 20.dp, top = 24.dp, bottom = 20.dp),
                ) {
                    if (isWelcome) {
                        Image(
                            painter = painterResource(R.drawable.renzo_login_banner),
                            contentDescription = "Renzo Shiori",
                            modifier = Modifier.height(44.dp),
                        )
                        Text(
                            "Welcome${user?.username?.let { ", $it" } ?: ""}!",
                            style = MaterialTheme.typography.titleLarge,
                            color = RenzoColors.Foreground,
                            textAlign = TextAlign.Center,
                            modifier = Modifier.padding(top = 20.dp),
                        )
                        Text(
                            "Your self-hosted library for manga, manhwa & manhua — read, track and take " +
                                "it offline. Here's a quick tour.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = RenzoColors.MutedForeground,
                            textAlign = TextAlign.Center,
                            modifier = Modifier.padding(top = 8.dp),
                        )
                    } else if (content != null) {
                        Box(
                            contentAlignment = Alignment.Center,
                            modifier = Modifier
                                .size(48.dp)
                                .clip(RoundedCornerShape(16.dp))
                                .background(RenzoColors.Primary.copy(alpha = 0.1f)),
                        ) {
                            Icon(
                                content.icon,
                                contentDescription = null,
                                tint = RenzoColors.Primary,
                                modifier = Modifier.size(28.dp),
                            )
                        }
                        Text(
                            content.title,
                            style = MaterialTheme.typography.titleMedium,
                            color = RenzoColors.Foreground,
                            textAlign = if (spotlight == null) TextAlign.Center else TextAlign.Start,
                            modifier = Modifier.padding(top = 16.dp),
                        )
                        Text(
                            content.body,
                            style = MaterialTheme.typography.bodyMedium,
                            color = RenzoColors.MutedForeground,
                            textAlign = if (spotlight == null) TextAlign.Center else TextAlign.Start,
                            modifier = Modifier.padding(top = 6.dp),
                        )
                    }
                }

                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .padding(top = 12.dp, end = 12.dp)
                        .size(32.dp)
                        .clip(CircleShape)
                        .clickable { close() },
                ) {
                    Icon(
                        Icons.Filled.Close,
                        contentDescription = "Skip",
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.size(16.dp),
                    )
                }
            }

            // ── Footer ───────────────────────────────────────────────────
            Box(modifier = Modifier.fillMaxWidth().height(1.dp).background(RenzoColors.Border))
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
            ) {
                if (isLast) {
                    Spacer(Modifier.width(1.dp))
                } else {
                    Text(
                        "Skip",
                        style = MaterialTheme.typography.labelSmall,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier.clickable { close() },
                    )
                }

                Row(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.weight(1f).padding(horizontal = 12.dp),
                ) {
                    repeat(total) { index ->
                        Box(
                            modifier = Modifier
                                .height(6.dp)
                                .width(if (index == step) 16.dp else 6.dp)
                                .clip(CircleShape)
                                .background(
                                    if (index == step) {
                                        RenzoColors.Primary
                                    } else {
                                        RenzoColors.Foreground.copy(alpha = 0.2f)
                                    },
                                ),
                        )
                    }
                }

                if (step > 0) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .clip(RoundedCornerShape(8.dp))
                            .clickable { step = maxOf(0, step - 1) }
                            .padding(horizontal = 8.dp, vertical = 6.dp),
                    ) {
                        Icon(
                            Icons.AutoMirrored.Filled.KeyboardArrowLeft,
                            contentDescription = null,
                            tint = RenzoColors.Foreground,
                            modifier = Modifier.size(16.dp),
                        )
                        Text(
                            "Back",
                            style = MaterialTheme.typography.labelLarge,
                            color = RenzoColors.Foreground,
                            modifier = Modifier.padding(start = 4.dp),
                        )
                    }
                    Spacer(Modifier.width(8.dp))
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(8.dp))
                        .background(RenzoColors.Primary)
                        .clickable { if (isLast) close() else step += 1 }
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                ) {
                    if (isLast) {
                        Icon(
                            Icons.Filled.RocketLaunch,
                            contentDescription = null,
                            tint = RenzoColors.PrimaryForeground,
                            modifier = Modifier.size(16.dp),
                        )
                        Text(
                            "Get started",
                            style = MaterialTheme.typography.labelLarge,
                            fontWeight = FontWeight.Medium,
                            color = RenzoColors.PrimaryForeground,
                            modifier = Modifier.padding(start = 4.dp),
                        )
                    } else {
                        Text(
                            "Next",
                            style = MaterialTheme.typography.labelLarge,
                            fontWeight = FontWeight.Medium,
                            color = RenzoColors.PrimaryForeground,
                        )
                        Icon(
                            Icons.AutoMirrored.Filled.KeyboardArrowRight,
                            contentDescription = null,
                            tint = RenzoColors.PrimaryForeground,
                            modifier = Modifier.padding(start = 4.dp).size(16.dp),
                        )
                    }
                }
            }
        }
    }
}

/**
 * Command-bar geometry for chrome that hasn't opted into [tourAnchor] — the
 * 56dp bar in HomeShell: hamburger at the left (48dp IconButton inside 6dp
 * padding), the search IconButton left of the Online/Offline pill, and the
 * 32dp initials avatar hard right.
 */
private fun fallbackAnchor(id: String, screenWidthPx: Float, densityScale: Float): Rect {
    fun dp(value: Float) = value * densityScale
    val statusBar = dp(24f) // typical status-bar height; the bar sits directly below it
    val barTop = statusBar
    return when (id) {
        TourAnchors.NAV -> Rect(dp(6f), barTop + dp(4f), dp(54f), barTop + dp(52f))
        TourAnchors.ACCOUNT -> Rect(
            screenWidthPx - dp(42f),
            barTop + dp(12f),
            screenWidthPx - dp(10f),
            barTop + dp(44f),
        )
        // Search button: left of the ~79dp Online pill, itself 48dp wide.
        else -> Rect(
            screenWidthPx - dp(133f),
            barTop + dp(4f),
            screenWidthPx - dp(85f),
            barTop + dp(52f),
        )
    }
}
