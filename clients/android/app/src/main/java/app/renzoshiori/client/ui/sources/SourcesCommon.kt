package app.renzoshiori.client.ui.sources

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.TextUnitType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.data.model.ExtensionEntryDto
import app.renzoshiori.client.data.model.ProviderDto
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

// ── globals.css --sources-* custom properties ───────────────────────────────
internal val SourcesLine = Color.White.copy(alpha = 0.06f)
internal val SourcesLineStrong = Color.White.copy(alpha = 0.12f)
internal val SourcesBgCard = Color(0xFF0E0E11)              // hsl(240 9% 6%)
internal val SourcesFailing = Color(0xFFEF4444)
internal val SourcesFailingSoft = Color(0x1AEF4444)          // hsla(0 84% 60% / 0.10)
internal val SourcesPrimarySoft = RenzoColors.Primary.copy(alpha = 0.18f)
internal val SourcesChipBg = Color.White.copy(alpha = 0.04f)

// ─────────────────────────────────────────────────────────────────────────────
// components/comp/sources/lib.ts — verbatim
// ─────────────────────────────────────────────────────────────────────────────

internal fun extensionEntries(extension: ProviderDto): List<ExtensionEntryDto> =
    extension.onlineRepositories.flatMap { it.entries }

internal fun primaryEntry(extension: ProviderDto): ExtensionEntryDto? {
    val allEntries = extensionEntries(extension)
    if (allEntries.isEmpty()) return null

    if (extension.isInstaled) {
        val localRepo = extension.onlineRepositories.firstOrNull { repo ->
            repo.entries.any { it.isLocal }
        }
        if (localRepo != null) {
            val index = min(max(extension.activeEntry, 0), localRepo.entries.size - 1)
            return localRepo.entries.getOrNull(index) ?: localRepo.entries.firstOrNull()
        }
    }
    return allEntries.firstOrNull()
}

internal fun extensionLanguages(extension: ProviderDto): List<String> =
    extensionEntries(extension)
        .flatMap { entry -> entry.sources.map { it.lang } }
        .filter { it.isNotEmpty() }
        .distinct()

internal fun primaryLanguage(extension: ProviderDto): String =
    extensionLanguages(extension).firstOrNull() ?: "all"

internal fun extensionVersion(extension: ProviderDto): String =
    primaryEntry(extension)?.version ?: ""

internal fun isExtensionNsfw(extension: ProviderDto): Boolean =
    extensionEntries(extension).any { it.nsfw }

/** source-row.tsx formatLanguageMeta — "v1.4.7 · en, fr" / "Multi (7)". */
internal fun formatLanguageMeta(extension: ProviderDto): String {
    val langs = extensionLanguages(extension)
    val version = extensionVersion(extension)
    val versionStr = if (version.isNotEmpty()) "v$version" else ""

    val langStr = when {
        langs.isEmpty() -> ""
        langs.size == 1 -> langs[0]
        langs.size <= 3 -> langs.joinToString(", ")
        else -> "Multi (${langs.size})"
    }

    if (versionStr.isNotEmpty() && langStr.isNotEmpty()) return "$versionStr · $langStr"
    if (versionStr.isNotEmpty()) return versionStr
    return langStr
}

// ─────────────────────────────────────────────────────────────────────────────
// lib/utils/language-country-mapping.ts — verbatim
// ─────────────────────────────────────────────────────────────────────────────

private val languageToCountryMap = mapOf(
    "af" to "ZA", "ar" to "SA", "az" to "AZ", "be" to "BY", "bg" to "BG",
    "bn" to "BD", "ca" to "ES", "cs" to "CZ", "cv" to "RU", "da" to "DK",
    "de" to "DE", "el" to "GR", "en" to "GB", "eo" to "UN", "es" to "ES",
    "es-419" to "MX", "et" to "EE", "eu" to "ES", "fa" to "IR", "fi" to "FI",
    "fil" to "PH", "fr" to "FR", "ga" to "IE", "gl" to "ES", "he" to "IL",
    "hi" to "IN", "hr" to "HR", "hu" to "HU", "id" to "ID", "it" to "IT",
    "ja" to "JP", "jv" to "ID", "ka" to "GE", "kk" to "KZ", "ko" to "KR",
    "la" to "VA", "lt" to "LT", "lv" to "LV", "mn" to "MN", "ms" to "MY",
    "my" to "MM", "ne" to "NP", "nl" to "NL", "no" to "NO", "pl" to "PL",
    "pt" to "PT", "pt-br" to "BR", "ro" to "RO", "ru" to "RU", "sk" to "SK",
    "sl" to "SI", "sq" to "AL", "sr" to "RS", "sv" to "SE", "ta" to "IN",
    "te" to "IN", "th" to "TH", "tl" to "PH", "tr" to "TR", "uk" to "UA",
    "ur" to "PK", "uz" to "UZ", "vi" to "VN", "zh" to "CN", "zh-hans" to "CN",
    "zh-hant" to "TW", "all" to "UN",
)

internal fun countryCodeForLanguage(languageCode: String): String =
    languageToCountryMap[languageCode.lowercase()] ?: "UN"

/**
 * The web renders <ReactCountryFlag> SVGs; Android renders the regional-
 * indicator flag emoji for the same ISO code. "UN" (Esperanto / "all") has no
 * country flag, so it falls back to the globe the web's UN flag stands in for.
 */
internal fun flagEmoji(countryCode: String): String {
    if (countryCode.length != 2 || countryCode == "UN") return "🌐" // 🌐
    val base = 0x1F1E6
    val a = base + (countryCode[0].uppercaseChar() - 'A')
    val b = base + (countryCode[1].uppercaseChar() - 'A')
    return String(Character.toChars(a)) + String(Character.toChars(b))
}

// ─────────────────────────────────────────────────────────────────────────────
// source-thumb.tsx — deterministic gradient placeholder
// ─────────────────────────────────────────────────────────────────────────────

/** Deterministic hash of a package name → 0–11, picking a stable gradient. */
private fun hashPackage(pkg: String): Int {
    var h = 0
    for (c in pkg) h = 31 * h + c.code
    return (abs(h.toLong()) % 12).toInt()
}

private val srcGradients = listOf(
    Color(0xFFFF6740) to Color(0xFFFF2D55),   // src-g-0
    Color(0xFF4F46E5) to Color(0xFF7C3AED),   // src-g-1
    Color(0xFFF59E0B) to Color(0xFFEF4444),   // src-g-2
    Color(0xFFEF4444) to Color(0xFFB91C1C),   // src-g-3
    Color(0xFF16A34A) to Color(0xFF0F766E),   // src-g-4
    Color(0xFFF97316) to Color(0xFFC2410C),   // src-g-5
    Color(0xFFEC4899) to Color(0xFFBE185D),   // src-g-6
    Color(0xFF00D564) to Color(0xFF00A37C),   // src-g-7
    Color(0xFF475569) to Color(0xFF1E293B),   // src-g-8
    Color(0xFF0096FA) to Color(0xFF0066CC),   // src-g-9
    Color(0xFFA855F7) to Color(0xFF6D28D9),   // src-g-10
    Color(0xFF2563EB) to Color(0xFF1E40AF),   // src-g-11
)

/** 44dp (mobile "sm") thumbnail frame with the gradient + initial fallback. */
@Composable
internal fun SourceThumb(extension: ProviderDto, baseUrl: String, thumbSize: Dp = 44.dp) {
    val shape = RoundedCornerShape(10.dp)
    val hasThumb = extension.thumbnailUrl.isNotEmpty()
    val (from, to) = srcGradients[hashPackage(extension.packageName)]

    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(thumbSize)
            .clip(shape)
            .background(Brush.linearGradient(listOf(from, to))),
    ) {
        if (hasThumb) {
            AsyncImage(
                model = absoluteUrl(baseUrl, extension.thumbnailUrl),
                contentDescription = "${extension.name} icon",
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Text(
                (extension.name.firstOrNull() ?: '?').uppercaseChar().toString(),
                fontSize = 17.sp,
                fontWeight = FontWeight.Bold,
                color = Color.White,
            )
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared atoms
// ─────────────────────────────────────────────────────────────────────────────

/** `.nsfw-pill` — the 18+ badge on adult sources. */
@Composable
internal fun NsfwPill() {
    val shape = RoundedCornerShape(4.dp)
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .height(16.dp)
            .clip(shape)
            .background(Color(0x2EEF4444))
            .border(1.dp, Color(0x4DEF4444), shape)
            .padding(horizontal = 5.dp),
    ) {
        Text(
            "18+",
            fontSize = 9.5.sp,
            fontWeight = FontWeight.Bold,
            letterSpacing = TextUnit(0.4f, TextUnitType.Sp),
            color = Color(0xFFFF8A8A),
        )
    }
}

/** `.dot-fail` — the pulsing red dot in front of a broken/dead source's name. */
@Composable
internal fun FailDot() {
    Box(
        modifier = Modifier
            .size(6.dp)
            .clip(RoundedCornerShape(50))
            .background(SourcesFailing),
    )
}

/** Muted count badge next to a section heading (`Installed 12`). */
@Composable
internal fun SectionCountBadge(count: Int) {
    val shape = RoundedCornerShape(50)
    Box(
        modifier = Modifier
            .clip(shape)
            .background(Color.White.copy(alpha = 0.05f))
            .border(1.dp, SourcesLine, shape)
            .padding(horizontal = 8.dp, vertical = 2.dp),
    ) {
        Text(
            count.toString(),
            fontSize = 12.sp,
            fontWeight = FontWeight.SemiBold,
            color = RenzoColors.MutedForeground,
        )
    }
}

/** `.src-chip` — the toolbar's rounded filter pills. */
@Composable
internal fun SrcChip(
    label: String,
    icon: ImageVector,
    isOn: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val shape = RoundedCornerShape(50)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .height(32.dp)
            .clip(shape)
            .background(if (isOn) SourcesPrimarySoft else SourcesChipBg)
            .border(1.dp, if (isOn) RenzoColors.Primary.copy(alpha = 0.4f) else SourcesLine, shape)
            .clickable { onClick() }
            .padding(horizontal = 10.dp),
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = if (isOn) RenzoColors.Foreground else RenzoColors.MutedForeground,
            modifier = Modifier.size(14.dp),
        )
        Text(
            label,
            fontSize = 12.5.sp,
            fontWeight = FontWeight.Medium,
            color = if (isOn) RenzoColors.Foreground else RenzoColors.MutedForeground,
            modifier = Modifier.padding(start = 6.dp),
        )
    }
}
