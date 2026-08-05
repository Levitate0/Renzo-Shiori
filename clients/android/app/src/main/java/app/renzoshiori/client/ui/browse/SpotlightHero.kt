package app.renzoshiori.client.ui.browse

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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Explore
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.ui.library.getStatusDisplay
import app.renzoshiori.client.ui.library.spotlightStripColor
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable
import coil3.compose.AsyncImage
import kotlinx.coroutines.delay

/** SpotlightItem — the hero's view model (spotlight-hero.tsx). */
data class SpotlightItem(
    val id: String,
    val title: String,
    val author: String? = null,
    val description: String? = null,
    val thumbnailUrl: String? = null,
    val status: Int = 0,
    val genres: List<String> = emptyList(),
    val availableChapters: Int? = null,
    val sourceName: String? = null,
)

private const val AUTO_ADVANCE_MS = 8000L
private const val PAUSE_AFTER_MANUAL_MS = 12000L

/** hsl(346.8 77.2% 49.8%) — the hero's pink. */
private val PINK = Color(0xFFE11D48)
private val PINK_BRIGHT = Color(0xFFF06A8B) // hsl(346.8 90% 70%)

/**
 * SpotlightHero — the cinematic hero at the top of Browse, transliterated from
 * spotlight-hero.tsx at its `<lg` (stacked) breakpoint, which is the only one a
 * phone ever renders: blurred cover backdrop with the pink radial + dark veil,
 * the 200×280 floating cover with its status-colored top strip, the eyebrow,
 * the display title, the status pill + meta strip, the gradient-faded
 * description with Read more, up to four tag chips, the pink CTA, and the row
 * of spotlight thumbnails underneath. Auto-advances every 8s, pausing for 12s
 * after the user taps a thumbnail.
 */
@Composable
fun SpotlightHero(
    items: List<SpotlightItem>,
    eyebrow: String,
    ctaLabel: String,
    onCtaClick: (SpotlightItem) -> Unit,
) {
    val safeItems = remember(items) { items.take(7) }
    if (safeItems.isEmpty()) return

    val isTv = LocalIsTv.current
    var active by remember(safeItems) { mutableStateOf(0) }
    var pauseUntil by remember { mutableStateOf(0L) }
    var descExpanded by remember(active) { mutableStateOf(false) }

    // On TV, focus anywhere in the hero pauses the carousel: a slide that
    // rotates out from under the cursor changes what Centre does mid-press.
    fun holdCarousel() {
        if (isTv) pauseUntil = System.currentTimeMillis() + PAUSE_AFTER_MANUAL_MS
    }

    // Auto-advance loop.
    LaunchedEffect(safeItems.size, active, pauseUntil) {
        if (safeItems.size <= 1) return@LaunchedEffect
        val wait = maxOf(AUTO_ADVANCE_MS, pauseUntil - System.currentTimeMillis())
        delay(wait)
        active = (active + 1) % safeItems.size
    }

    val current = safeItems[active.coerceIn(0, safeItems.lastIndex)]
    val statusInfo = getStatusDisplay(current.status)
    val stripColor = spotlightStripColor(current.status)
    val tags = current.genres.take(4)

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .border(1.dp, Color.White.copy(alpha = 0.06f), RoundedCornerShape(16.dp))
            .background(Color(0xFF111119)), // hsl(240 8% 7%)
    ) {
        // Backdrop — blurred + darkened copy of the active cover.
        if (current.thumbnailUrl != null) {
            AsyncImage(
                model = current.thumbnailUrl,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier
                    .matchParentSize()
                    .scale(1.15f)
                    .blur(40.dp),
            )
        }
        // Dark veil + pink radial glow, top-right.
        Box(
            modifier = Modifier
                .matchParentSize()
                .background(Color.Black.copy(alpha = 0.55f))
                .background(
                    Brush.radialGradient(
                        colors = listOf(PINK.copy(alpha = 0.42f), Color.Transparent),
                        center = androidx.compose.ui.geometry.Offset(1400f, -200f),
                        radius = 1400f,
                    ),
                )
                .background(
                    Brush.verticalGradient(
                        listOf(Color.Black.copy(alpha = 0.10f), Color.Black.copy(alpha = 0.55f)),
                    ),
                ),
        )

        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 24.dp),
        ) {
            // Floating cover.
            Box(
                modifier = Modifier
                    .width(200.dp)
                    .height(280.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(RenzoColors.Muted),
            ) {
                if (current.thumbnailUrl != null) {
                    AsyncImage(
                        model = current.thumbnailUrl,
                        contentDescription = current.title,
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                }
                // Status-colored top strip.
                Box(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .fillMaxWidth()
                        .height(2.dp)
                        .background(stripColor),
                )
                Box(
                    modifier = Modifier
                        .matchParentSize()
                        .background(
                            Brush.verticalGradient(
                                0.0f to Color.Transparent,
                                0.65f to Color.Black.copy(alpha = 0.25f),
                                1.0f to Color.Black.copy(alpha = 0.85f),
                            ),
                        ),
                )
                Column(modifier = Modifier.align(Alignment.BottomStart).padding(12.dp)) {
                    Text(
                        current.title.uppercase(),
                        style = MaterialTheme.typography.labelSmall.copy(
                            fontSize = 10.sp,
                            fontWeight = FontWeight.SemiBold,
                        ),
                        color = Color.White.copy(alpha = 0.8f),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (!current.author.isNullOrBlank()) {
                        Text(
                            current.author,
                            style = MaterialTheme.typography.labelSmall.copy(fontSize = 10.sp),
                            color = Color.White.copy(alpha = 0.55f),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }

            Spacer(Modifier.height(20.dp))

            // Eyebrow — the phone shows only the segment before the "·".
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.Explore,
                    contentDescription = null,
                    tint = PINK_BRIGHT,
                    modifier = Modifier.size(12.dp),
                )
                Text(
                    eyebrow.substringBefore("·").trim(),
                    style = MaterialTheme.typography.labelSmall.copy(
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                    ),
                    color = PINK_BRIGHT,
                    letterSpacing = 2.8.sp,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }

            Spacer(Modifier.height(8.dp))

            // Title.
            Text(
                current.title,
                style = MaterialTheme.typography.headlineMedium.copy(
                    fontSize = 30.sp,
                    fontWeight = FontWeight.ExtraBold,
                ),
                color = Color.White,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                textAlign = TextAlign.Center,
            )

            Spacer(Modifier.height(16.dp))

            // Meta strip: status pill · N chapters available · Source: X.
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterHorizontally),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .border(1.dp, Color.White.copy(alpha = 0.10f), RoundedCornerShape(50))
                        .background(Color.White.copy(alpha = 0.06f))
                        .padding(horizontal = 10.dp, vertical = 4.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(8.dp)
                            .clip(CircleShape)
                            .background(stripColor),
                    )
                    Text(
                        statusInfo.text,
                        style = MaterialTheme.typography.labelSmall.copy(fontSize = 11.sp),
                        color = Color.White,
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }
                if (current.availableChapters != null) {
                    Text(
                        "${current.availableChapters} chapters available",
                        style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                        color = Color.White.copy(alpha = 0.55f),
                    )
                }
                if (!current.sourceName.isNullOrBlank()) {
                    Text(
                        "Source: ${current.sourceName}",
                        style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                        color = Color.White.copy(alpha = 0.55f),
                    )
                }
            }

            // Description with Read more / Read less.
            if (!current.description.isNullOrBlank()) {
                Spacer(Modifier.height(16.dp))
                Text(
                    current.description,
                    style = MaterialTheme.typography.bodyMedium.copy(fontSize = 14.sp),
                    color = Color.White.copy(alpha = 0.65f),
                    maxLines = if (descExpanded) Int.MAX_VALUE else 3,
                    overflow = TextOverflow.Ellipsis,
                    textAlign = TextAlign.Center,
                )
                val moreFocus = rememberFocusState()
                Text(
                    if (descExpanded) "READ LESS" else "READ MORE",
                    style = MaterialTheme.typography.labelSmall.copy(
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                    ),
                    color = PINK_BRIGHT,
                    letterSpacing = 1.2.sp,
                    modifier = Modifier
                        .padding(top = 6.dp)
                        .clip(RoundedCornerShape(4.dp))
                        .then(
                            if (isTv) {
                                Modifier
                                    .focusRing(moreFocus.focused, 4.dp)
                                    .tvClickable(
                                        onFocused = {
                                            moreFocus.set(it)
                                            if (it) holdCarousel()
                                        },
                                        onClick = { descExpanded = !descExpanded },
                                    )
                            } else {
                                Modifier.clickable { descExpanded = !descExpanded }
                            },
                        )
                        .padding(horizontal = 4.dp, vertical = 2.dp),
                )
            }

            // Tags — the first one gets the pink treatment.
            if (tags.isNotEmpty()) {
                Spacer(Modifier.height(16.dp))
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    tags.forEachIndexed { index, tag ->
                        Text(
                            tag,
                            style = MaterialTheme.typography.labelSmall.copy(fontSize = 10.5.sp),
                            color = if (index == 0) Color(0xFFF490AA) else Color.White.copy(alpha = 0.65f),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier
                                .clip(RoundedCornerShape(50))
                                .border(
                                    1.dp,
                                    if (index == 0) PINK.copy(alpha = 0.32f) else Color.White.copy(alpha = 0.10f),
                                    RoundedCornerShape(50),
                                )
                                .background(
                                    if (index == 0) PINK.copy(alpha = 0.14f) else Color.White.copy(alpha = 0.06f),
                                )
                                .padding(horizontal = 8.dp, vertical = 3.dp),
                        )
                    }
                }
            }

            // CTA.
            Spacer(Modifier.height(20.dp))
            val ctaFocus = rememberFocusState()
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .height(if (isTv) 48.dp else 40.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(PINK)
                    .then(
                        if (isTv) {
                            Modifier
                                .focusRing(ctaFocus.focused, 8.dp)
                                .tvClickable(
                                    onFocused = {
                                        ctaFocus.set(it)
                                        if (it) holdCarousel()
                                    },
                                    onClick = { onCtaClick(current) },
                                )
                        } else {
                            Modifier.clickable { onCtaClick(current) }
                        },
                    )
                    .padding(horizontal = 20.dp),
            ) {
                Icon(
                    Icons.Filled.Add,
                    contentDescription = null,
                    tint = Color.White,
                    modifier = Modifier.size(16.dp),
                )
                Text(
                    ctaLabel,
                    style = MaterialTheme.typography.labelLarge.copy(
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold,
                    ),
                    color = Color.White,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }

            // Thumbnail strip.
            if (safeItems.size > 1) {
                Spacer(Modifier.height(20.dp))
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    safeItems.forEachIndexed { index, item ->
                        val isActive = index == active
                        // Selection is the un-dimmed thumbnail plus the pink
                        // outline drawn *over* the cover below; the accent ring
                        // sits outside the whole tile and is focus alone. Both
                        // are legible at the same time.
                        val thumbFocus = rememberFocusState()
                        Box(
                            modifier = Modifier
                                .then(
                                    if (isTv) {
                                        Modifier.focusRing(thumbFocus.focused, 8.dp).padding(3.dp)
                                    } else {
                                        Modifier
                                    },
                                )
                                .width(if (isTv) 40.dp else 28.dp)
                                .height(if (isTv) 56.dp else 40.dp)
                                .clip(RoundedCornerShape(6.dp))
                                .border(
                                    1.dp,
                                    if (isActive) PINK else Color.White.copy(alpha = 0.10f),
                                    RoundedCornerShape(6.dp),
                                )
                                .background(RenzoColors.Muted)
                                .then(
                                    if (isTv) {
                                        Modifier.tvClickable(
                                            onFocused = {
                                                thumbFocus.set(it)
                                                if (it) holdCarousel()
                                            },
                                            onClick = {
                                                active = index
                                                pauseUntil =
                                                    System.currentTimeMillis() + PAUSE_AFTER_MANUAL_MS
                                            },
                                        )
                                    } else {
                                        Modifier.clickable {
                                            active = index
                                            pauseUntil = System.currentTimeMillis() + PAUSE_AFTER_MANUAL_MS
                                        }
                                    },
                                ),
                        ) {
                            if (item.thumbnailUrl != null) {
                                AsyncImage(
                                    model = item.thumbnailUrl,
                                    contentDescription = item.title,
                                    contentScale = ContentScale.Crop,
                                    modifier = Modifier.fillMaxSize(),
                                )
                            }
                            if (!isActive) {
                                Box(
                                    modifier = Modifier
                                        .matchParentSize()
                                        .background(Color.Black.copy(alpha = 0.45f)),
                                )
                            } else if (isTv) {
                                // Drawn as a child so it lands on top of the
                                // cover — a border in the modifier chain paints
                                // before the image and would be invisible.
                                Box(
                                    modifier = Modifier
                                        .matchParentSize()
                                        .border(2.dp, PINK, RoundedCornerShape(6.dp)),
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
