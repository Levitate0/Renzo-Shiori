package app.renzoshiori.client.ui.library

import androidx.compose.ui.graphics.Color
import app.renzoshiori.client.data.model.SeriesStatus
import app.renzoshiori.client.ui.queue.parseUtcMillis

/**
 * 1:1 ports of the small web helpers the Library / Browse cards rely on:
 * getStatusDisplay (lib/utils/series-status.ts), the 31-step Last-Change ring
 * palette (list-series/index.tsx) and the adult-tag classifier
 * (lib/utils/adult-filter.ts).
 */

/** SeriesStatus.DISABLED — present in the web enum, absent from SeriesStatus.kt. */
const val SERIES_STATUS_DISABLED = 7

data class StatusDisplay(val text: String, val color: Color)

/** Verbatim getStatusDisplay(): same wording, same Tailwind color per status. */
fun getStatusDisplay(status: Int): StatusDisplay = when (status) {
    SeriesStatus.ONGOING -> StatusDisplay("Ongoing", Color(0xFF22C55E))            // bg-green-500
    SeriesStatus.COMPLETED -> StatusDisplay("Completed", Color(0xFF3B82F6))        // bg-blue-500
    SeriesStatus.LICENSED -> StatusDisplay("Licensed", Color(0xFFA855F7))          // bg-purple-500
    SeriesStatus.PUBLISHING_FINISHED -> StatusDisplay("Publishing Finished", Color(0xFF2563EB)) // bg-blue-600
    SeriesStatus.CANCELLED -> StatusDisplay("Cancelled", Color(0xFFEF4444))        // bg-red-500
    SeriesStatus.ON_HIATUS -> StatusDisplay("On Hiatus", Color(0xFFEAB308))        // bg-yellow-500
    SERIES_STATUS_DISABLED -> StatusDisplay("Disabled", Color(0xFF4B5563))         // bg-gray-600
    else -> StatusDisplay("Unknown", Color(0xFF6B7280))                            // bg-gray-500
}

/** The spotlight hero's cover top-strip color (spotlight-hero.tsx statusBarColor). */
fun spotlightStripColor(status: Int): Color = when (status) {
    SeriesStatus.ONGOING -> Color(0xFF22C55E)
    SeriesStatus.COMPLETED, SeriesStatus.PUBLISHING_FINISHED -> Color(0xFF3B82F6)
    SeriesStatus.LICENSED -> Color(0xFFA855F7)
    SeriesStatus.CANCELLED -> Color(0xFFEF4444)
    SeriesStatus.ON_HIATUS -> Color(0xFFEAB308)
    SERIES_STATUS_DISABLED -> Color(0xFF4B5563)
    else -> Color(0xFF6B7280)
}

/** LAST_CHANGE_COLORS — 31 steps, green → blue, indexed by days since change. */
private val LAST_CHANGE_COLORS = listOf(
    0x00FF00, 0x22FF00, 0x44FF00, 0x66FF00, 0x88FF00, 0xAAFF00, 0xCCFF00, 0xFFFF00,
    0xFFCC00, 0xFFAA00, 0xFF8800, 0xFF6600, 0xFF4400, 0xFF2200, 0xFF0000, 0xFF0022,
    0xFF0044, 0xFF0066, 0xFF0088, 0xFF00AA, 0xFF00CC, 0xFF00FF, 0xCC00FF, 0xAA00FF,
    0x8800FF, 0x6600FF, 0x4400FF, 0x2200FF, 0x0000FF, 0x2200FF, 0x4400FF,
)

/**
 * The age-graded card border shown while sorting by Last Change. Returns null
 * past 31 days (web: no ring at all), matching getLastChangeRingColor().
 */
fun lastChangeRingColor(lastChangeUtc: String?): Color? {
    if (lastChangeUtc.isNullOrBlank()) return null
    val millis = parseUtcMillis(lastChangeUtc) ?: return null
    val diffDays = ((System.currentTimeMillis() - millis) / (1000L * 60L * 60L * 24L)).toInt()
    if (diffDays < 0 || diffDays > 31) return null
    val rgb = LAST_CHANGE_COLORS[minOf(diffDays, 30)]
    return Color(0xFF000000.toInt() or rgb)
}

// The adult-content filter lives in ui/util/AdultFilter.kt (AdultFilter.isHidden /
// AdultFilter.isAdultItem) — the Library and Browse cards call that helper
// directly rather than keeping a second copy of the classifier here.
