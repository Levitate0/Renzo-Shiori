package app.renzoshiori.client.ui.series

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable

// ──────────────────────────────────────────────────────────────────────────
// Tailwind colours the web series page uses that aren't in RenzoColors.
// ──────────────────────────────────────────────────────────────────────────
val Emerald500 = Color(0xFF10B981)
val Emerald400 = Color(0xFF34D399)
val Amber500 = Color(0xFFF59E0B)
val Amber400 = Color(0xFFFBBF24)
val Violet400 = Color(0xFFA78BFA)
val Pink500 = Color(0xFFEC4899)
val Green500 = Color(0xFF22C55E)
val Blue500 = Color(0xFF3B82F6)
val Blue600 = Color(0xFF2563EB)
val Blue400 = Color(0xFF60A5FA)
val Blue300 = Color(0xFF93C5FD)
val Purple500 = Color(0xFFA855F7)
val Purple400 = Color(0xFFC084FC)
val Red500 = Color(0xFFEF4444)
val Red400 = Color(0xFFF87171)
val Yellow500 = Color(0xFFEAB308)
val Yellow400 = Color(0xFFFACC15)
val Yellow300 = Color(0xFFFDE047)
val Gray500 = Color(0xFF6B7280)
val Gray600 = Color(0xFF4B5563)

/** --destructive-foreground on the dark theme reads as a red-400-ish accent. */
val DestructiveText = Red400

/** text-muted-foreground */
val Muted = RenzoColors.MutedForeground

/** border-border/40 and border-border/60 as literal colours. */
val Border40 = RenzoColors.Border.copy(alpha = 0.4f)
val Border60 = RenzoColors.Border.copy(alpha = 0.6f)

/** bg-foreground/[0.04] — the web's neutral chip fill. */
val ForegroundFaint = RenzoColors.Foreground.copy(alpha = 0.04f)
val ForegroundFaint06 = RenzoColors.Foreground.copy(alpha = 0.06f)
val ForegroundFaint10 = RenzoColors.Foreground.copy(alpha = 0.10f)

/** rounded-full */
val PillShape = RoundedCornerShape(50)

// ──────────────────────────────────────────────────────────────────────────
// Chips + toggles
// ──────────────────────────────────────────────────────────────────────────

/**
 * The web's `inline-flex … rounded-full border px-3 py-1 text-[11px]` action
 * chip (Download all / Delete downloads / Mark all read / Select / Missing
 * only / the selection toolbar's buttons). Not a Material Button — the web
 * uses a bare <button> with these exact borders and fills.
 */
@Composable
fun RenzoChip(
    label: String,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    count: Int? = null,
    accent: Color? = null,
    active: Boolean = false,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    val tint = accent ?: if (active) MaterialTheme.colorScheme.primary else Muted
    val bg = when {
        active && accent != null -> accent.copy(alpha = 0.15f)
        active -> MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)
        accent != null -> accent.copy(alpha = 0.10f)
        else -> ForegroundFaint
    }
    val borderColor = if (accent != null || active) tint.copy(alpha = 0.4f) else Border40
    // TV: the ring says where the cursor is; `active` keeps saying what's on.
    // Both have to be readable at once, so they use different channels.
    val focus = rememberFocusState()
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        modifier = modifier
            .clip(PillShape)
            .background(bg)
            .border(1.dp, borderColor, PillShape)
            .focusRing(focus.focused, 999.dp)
            .tvClickable(onFocused = focus::set, enabled = enabled, onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 5.dp)
            .then(if (enabled) Modifier else Modifier.alphaHalf()),
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(14.dp))
        }
        Text(
            label,
            fontSize = 11.sp,
            fontWeight = FontWeight.Medium,
            color = tint,
            maxLines = 1,
        )
        if (count != null) {
            Text(
                "($count)",
                fontSize = 11.sp,
                fontWeight = FontWeight.Medium,
                color = tint.copy(alpha = 0.8f),
                maxLines = 1,
            )
        }
    }
}

/**
 * PillToggle from provider-card.tsx — a dot + short label pill that turns
 * primary when on (Perm / Cover / Title / Status).
 */
@Composable
fun PillToggle(
    label: String,
    checked: Boolean,
    enabled: Boolean = true,
    onChange: (Boolean) -> Unit,
) {
    val primary = MaterialTheme.colorScheme.primary
    val focus = rememberFocusState()
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        modifier = Modifier
            .clip(PillShape)
            .background(if (checked) primary.copy(alpha = 0.15f) else ForegroundFaint)
            .border(1.dp, if (checked) primary.copy(alpha = 0.4f) else Border40, PillShape)
            .focusRing(focus.focused, 999.dp)
            .tvClickable(onFocused = focus::set, enabled = enabled) { onChange(!checked) }
            .padding(horizontal = 10.dp, vertical = 4.dp)
            .then(if (enabled) Modifier else Modifier.alphaHalf()),
    ) {
        // On a TV the dot alone is too small to read across a room, and colour
        // alone fails for colour-blind viewers — so an on toggle also gets a tick.
        if (LocalIsTv.current && checked) {
            Icon(
                Icons.Filled.Check,
                contentDescription = null,
                tint = primary,
                modifier = Modifier.size(12.dp),
            )
        }
        Box(
            Modifier
                .size(6.dp)
                .clip(PillShape)
                .background(if (checked) primary else RenzoColors.Foreground.copy(alpha = 0.3f)),
        )
        Text(
            label,
            fontSize = 11.sp,
            fontWeight = FontWeight.Medium,
            color = if (checked) primary else Muted,
            maxLines = 1,
        )
    }
}

/** The web's `h-8 w-8 rounded-md border` icon button (power / trash / match). */
@Composable
fun SquareIconButton(
    icon: ImageVector,
    contentDescription: String,
    tint: Color = Muted,
    borderColor: Color = Border60,
    background: Color = RenzoColors.Foreground.copy(alpha = 0.03f),
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    val focus = rememberFocusState()
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(32.dp)
            .clip(MaterialTheme.shapes.extraSmall)
            .background(background)
            .border(1.dp, borderColor, MaterialTheme.shapes.extraSmall)
            .focusRing(focus.focused, 6.dp)
            .tvClickable(onFocused = focus::set, enabled = enabled, onClick = onClick)
            .then(if (enabled) Modifier else Modifier.alphaHalf()),
    ) {
        Icon(icon, contentDescription = contentDescription, tint = tint, modifier = Modifier.size(16.dp))
    }
}

/** The web's `h-1.5 w-1.5 rounded-full bg-*` status dot. */
@Composable
fun StatusDot(color: Color, size: Int = 6) {
    Box(Modifier.size(size.dp).clip(PillShape).background(color))
}

/** A `rounded-full border px-2 py-0.5 text-[10px] uppercase` metadata badge. */
@Composable
fun MetaBadge(
    label: String,
    color: Color = Muted,
    borderColor: Color = Border40,
    background: Color = ForegroundFaint,
    leading: @Composable (() -> Unit)? = null,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
        modifier = Modifier
            .clip(PillShape)
            .background(background)
            .border(1.dp, borderColor, PillShape)
            .padding(horizontal = 8.dp, vertical = 2.dp),
    ) {
        leading?.invoke()
        Text(
            label.uppercase(),
            fontSize = 10.sp,
            fontWeight = FontWeight.Medium,
            color = color,
            maxLines = 1,
        )
    }
}

/** The web's section shell: `rounded-xl border border-border/60 bg-card`. */
@Composable
fun SectionCard(
    modifier: Modifier = Modifier,
    background: Color = RenzoColors.Card,
    content: @Composable androidx.compose.foundation.layout.ColumnScope.() -> Unit,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(MaterialTheme.shapes.large)
            .background(background)
            .border(1.dp, Border60, MaterialTheme.shapes.large),
        content = content,
    )
}

/** `border-b border-border/60` / `border-t border-border/40` rules. */
@Composable
fun HairLine(color: Color = Border60) {
    Box(Modifier.fillMaxWidth().height(1.dp).background(color))
}

/** Match the web's `opacity-50` disabled state without Material's alpha tokens. */
fun Modifier.alphaHalf(): Modifier = this.alpha(0.5f)

// ──────────────────────────────────────────────────────────────────────────
// Dialogs
// ──────────────────────────────────────────────────────────────────────────

/**
 * A 1:1 stand-in for the web's <Dialog>: card background, `rounded-xl`
 * border, semibold title, muted description, and a right-aligned footer.
 */
@Composable
fun RenzoDialog(
    title: String,
    onDismiss: () -> Unit,
    description: String? = null,
    titleLeading: @Composable (() -> Unit)? = null,
    body: @Composable (() -> Unit)? = null,
    footer: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = RenzoColors.Card,
        shape = MaterialTheme.shapes.large,
        title = {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                titleLeading?.invoke()
                Text(
                    title,
                    style = MaterialTheme.typography.titleMedium,
                    color = RenzoColors.Foreground,
                )
            }
        },
        text = {
            Column(
                Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                if (description != null) {
                    Text(description, style = MaterialTheme.typography.bodySmall, color = Muted)
                }
                body?.invoke()
            }
        },
        confirmButton = {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), content = footer)
        },
    )
}

/** The web's `variant="outline"` dialog button. */
@Composable
fun OutlineDialogButton(text: String, enabled: Boolean = true, onClick: () -> Unit) {
    androidx.compose.material3.OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        shape = MaterialTheme.shapes.small,
        border = BorderStroke(1.dp, Border60),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
    ) {
        Text(text, style = MaterialTheme.typography.labelLarge, color = RenzoColors.Foreground)
    }
}

/** The web's `variant="destructive"` dialog button. */
@Composable
fun DestructiveDialogButton(
    text: String,
    icon: ImageVector? = null,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    androidx.compose.material3.Button(
        onClick = onClick,
        enabled = enabled,
        shape = MaterialTheme.shapes.small,
        colors = androidx.compose.material3.ButtonDefaults.buttonColors(
            containerColor = RenzoColors.Destructive,
            contentColor = RenzoColors.Foreground,
        ),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, modifier = Modifier.size(16.dp))
            Spacer(Modifier.width(8.dp))
        }
        Text(text, style = MaterialTheme.typography.labelLarge)
    }
}

/** The web's `variant="default"` (primary) dialog button. */
@Composable
fun PrimaryDialogButton(text: String, enabled: Boolean = true, onClick: () -> Unit) {
    androidx.compose.material3.Button(
        onClick = onClick,
        enabled = enabled,
        shape = MaterialTheme.shapes.small,
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
    ) {
        Text(text, style = MaterialTheme.typography.labelLarge)
    }
}

/** The web's ghost/menu row inside a dialog (a full-width tappable item). */
@Composable
fun MenuRow(
    label: String,
    icon: ImageVector? = null,
    iconTint: Color = RenzoColors.Foreground,
    labelColor: Color = RenzoColors.Foreground,
    trailing: @Composable (() -> Unit)? = null,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        modifier = Modifier
            .fillMaxWidth()
            .clip(MaterialTheme.shapes.extraSmall)
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 8.dp, vertical = 10.dp)
            .then(if (enabled) Modifier else Modifier.alphaHalf()),
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = iconTint, modifier = Modifier.size(16.dp))
        }
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = labelColor,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        trailing?.invoke()
    }
}

/** The web's <Switch> — used by "Also delete Physical Files" and tracking. */
@Composable
fun RenzoSwitch(checked: Boolean, onCheckedChange: (Boolean) -> Unit, enabled: Boolean = true) {
    Switch(
        checked = checked,
        onCheckedChange = onCheckedChange,
        enabled = enabled,
        colors = SwitchDefaults.colors(
            checkedThumbColor = RenzoColors.PrimaryForeground,
            checkedTrackColor = MaterialTheme.colorScheme.primary,
            uncheckedThumbColor = Muted,
            uncheckedTrackColor = RenzoColors.Secondary,
            uncheckedBorderColor = Border60,
        ),
    )
}

// ──────────────────────────────────────────────────────────────────────────
// Formatting helpers (ports of the web's inline helpers)
// ──────────────────────────────────────────────────────────────────────────

/** formatRelative() from series-hero.tsx / provider-card.tsx. */
fun formatRelative(dateString: String?): String {
    if (dateString.isNullOrEmpty()) return "—"
    val millis = parseUtcMillis(dateString) ?: return "—"
    val diff = System.currentTimeMillis() - millis
    val minutes = diff / 60_000
    if (minutes < 1) return "just now"
    if (minutes < 60) return "${minutes}m ago"
    val hours = minutes / 60
    if (hours < 24) return "${hours}h ago"
    val days = hours / 24
    if (days < 30) return "${days}d ago"
    val months = days / 30
    if (months < 12) return "${months}mo ago"
    return "${months / 12}y ago"
}

/**
 * formatUpload() from chapter-row.tsx: "Today" / "Yesterday" / "N days ago"
 * within the last week, an absolute date beyond that.
 */
fun formatUpload(iso: String?): String? {
    if (iso.isNullOrEmpty()) return null
    val millis = parseUtcMillis(iso) ?: return null
    val days = ((System.currentTimeMillis() - millis) / 86_400_000L).toInt()
    if (days < 0) return absoluteDate(millis)
    if (days == 0) return "Today"
    if (days == 1) return "Yesterday"
    if (days < 7) return "$days days ago"
    return absoluteDate(millis)
}

/** relativeTime() from download-item.tsx ("2h ago", "in 5m", "just now"). */
fun downloadRelativeTime(iso: String?): String {
    val millis = parseUtcMillis(iso ?: return "") ?: return ""
    val diffSec = ((millis - System.currentTimeMillis()) / 1000).toInt()
    val absSec = kotlin.math.abs(diffSec)
    if (absSec < 30) return "just now"
    val future = diffSec > 0
    val (value, unit) = when {
        absSec < 60 -> absSec to "s"
        absSec < 3600 -> (absSec / 60) to "m"
        absSec < 86_400 -> (absSec / 3600) to "h"
        absSec < 604_800 -> (absSec / 86_400) to "d"
        absSec < 2_629_746 -> (absSec / 604_800) to "w"
        absSec < 31_556_952 -> (absSec / 2_629_746) to "mo"
        else -> (absSec / 31_556_952) to "y"
    }
    val rounded = maxOf(1, value)
    return if (future) "in $rounded$unit" else "$rounded$unit ago"
}

/**
 * The backend serializes UTC without a zone marker — normalize exactly like
 * normalizeUtcString() so relative labels aren't skewed by the device zone.
 */
private fun parseUtcMillis(raw: String): Long? {
    val normalized = if (raw.contains('Z') || raw.contains('+') || raw.indexOf('-', 10) > 0) raw else "${raw}Z"
    return runCatching { java.time.Instant.parse(normalized).toEpochMilli() }.getOrNull()
        ?: runCatching {
            java.time.LocalDateTime.parse(raw.substringBefore('Z'))
                .toInstant(java.time.ZoneOffset.UTC).toEpochMilli()
        }.getOrNull()
}

private fun absoluteDate(millis: Long): String =
    java.time.format.DateTimeFormatter.ofPattern("MMM d, yyyy")
        .withZone(java.time.ZoneId.systemDefault())
        .format(java.time.Instant.ofEpochMilli(millis))

/**
 * The flag react-country-flag renders next to a source's name, as a regional
 * indicator emoji pair. Mapping copied from language-country-mapping.ts.
 */
fun flagForLanguage(lang: String): String {
    val code = languageToCountry[lang.lowercase()] ?: "UN"
    if (code == "UN") return "🏳"
    return code.map { Character.toChars(0x1F1E6 + (it - 'A')).concatToString() }.joinToString("")
}

private val languageToCountry: Map<String, String> = mapOf(
    "af" to "ZA", "ar" to "SA", "az" to "AZ", "be" to "BY", "bg" to "BG", "bn" to "BD",
    "ca" to "ES", "cs" to "CZ", "cv" to "RU", "da" to "DK", "de" to "DE", "el" to "GR",
    "en" to "GB", "eo" to "UN", "es" to "ES", "es-419" to "MX", "et" to "EE", "eu" to "ES",
    "fa" to "IR", "fi" to "FI", "fil" to "PH", "fr" to "FR", "ga" to "IE", "gl" to "ES",
    "he" to "IL", "hi" to "IN", "hr" to "HR", "hu" to "HU", "id" to "ID", "it" to "IT",
    "ja" to "JP", "jv" to "ID", "ka" to "GE", "kk" to "KZ", "ko" to "KR", "la" to "VA",
    "lt" to "LT", "lv" to "LV", "mn" to "MN", "ms" to "MY", "my" to "MM", "ne" to "NP",
    "nl" to "NL", "no" to "NO", "pl" to "PL", "pt" to "PT", "pt-br" to "BR", "ro" to "RO",
    "ru" to "RU", "sk" to "SK", "sl" to "SI", "sq" to "AL", "sr" to "RS", "sv" to "SE",
    "ta" to "IN", "te" to "IN", "th" to "TH", "tl" to "PH", "tr" to "TR", "uk" to "UA",
    "ur" to "PK", "uz" to "UZ", "vi" to "VN", "zh" to "CN", "zh-hans" to "CN",
    "zh-hant" to "TW", "all" to "UN",
)

/** getStatusDisplay() from series-status.ts — the source row's status pill. */
fun providerStatusDisplay(status: Int): Pair<String, Color> = when (status) {
    1 -> "Ongoing" to Green500
    2 -> "Completed" to Blue500
    3 -> "Licensed" to Purple500
    4 -> "Publishing Finished" to Blue600
    5 -> "Cancelled" to Red500
    6 -> "On Hiatus" to Yellow500
    SERIES_STATUS_DISABLED -> "Disabled" to Gray600
    else -> "Unknown" to Gray500
}

/** getStatusLabel()/getStatusPillConfig() from series-hero.tsx. */
fun heroStatusLabel(status: Int): String = when (status) {
    1 -> "Ongoing"
    2 -> "Completed"
    3 -> "Licensed"
    4 -> "Finished"
    5 -> "Cancelled"
    6 -> "On Hiatus"
    SERIES_STATUS_DISABLED -> "Disabled"
    else -> "Unknown"
}

/** (text, dot, border, background) — the hero pill's four tailwind tokens. */
fun heroStatusColors(status: Int): HeroStatusColors = when (status) {
    1 -> HeroStatusColors(Color(0xFF4ADE80), Green500, true)
    2 -> HeroStatusColors(Blue400, Blue500, false)
    3 -> HeroStatusColors(Purple400, Purple500, false)
    4 -> HeroStatusColors(Blue300, Blue600, false)
    5 -> HeroStatusColors(Red400, Red500, false)
    6 -> HeroStatusColors(Yellow400, Yellow500, false)
    else -> HeroStatusColors(Muted, Muted, false)
}

data class HeroStatusColors(val text: Color, val dot: Color, val pulse: Boolean)

/** getArchiveResultDisplay() from the series page's cleanup dialog. */
fun archiveResultDisplay(result: Int): Pair<String, Color> = when (result) {
    0 -> "Fine" to Green500
    1 -> "Not an Archive" to Red500
    2 -> "No Images" to Yellow500
    3 -> "Not Found" to Red500
    else -> "Unknown" to Gray500
}
