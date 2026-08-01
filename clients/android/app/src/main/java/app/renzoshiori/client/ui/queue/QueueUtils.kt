package app.renzoshiori.client.ui.queue

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.TextStyle
import java.util.Locale

/**
 * 1:1 port of RenzoFrontend src/components/comp/queue/utils.ts — shared by the
 * Queue and Updates screens exactly as the web shares it.
 */

/**
 * Appends 'Z' to a UTC date string that lacks an explicit timezone suffix so
 * that parsing treats it as UTC rather than local time.
 */
fun normalizeUtcString(dateString: String): String =
    if (dateString.contains('Z') || dateString.contains('+') || dateString.indexOf('-', 10) >= 0) {
        dateString
    } else {
        dateString + "Z"
    }

/** Epoch millis for a backend UTC timestamp, or null when unparseable. */
fun parseUtcMillis(dateString: String): Long? {
    if (dateString.isBlank()) return null
    val normalized = normalizeUtcString(dateString)
    return runCatching { Instant.parse(normalized).toEpochMilli() }.getOrNull()
        ?: runCatching {
            LocalDate.parse(dateString.take(10)).atStartOfDay(ZoneId.systemDefault()).toInstant().toEpochMilli()
        }.getOrNull()
}

/**
 * Concise relative time, verbatim from the web:
 *   < 60 s → "just now"; < 60 m → "Xm ago"; same day < 24 h → "Xh ago";
 *   yesterday → "yesterday"; last 7 days → short weekday ("Tue");
 *   older → short date ("May 12").
 */
fun formatRelativeTime(millis: Long): String {
    val now = System.currentTimeMillis()
    val diff = now - millis

    if (diff < 60_000L) return "just now"
    if (diff < 60L * 60_000L) return "${diff / 60_000L}m ago"

    val zone = ZoneId.systemDefault()
    val date = Instant.ofEpochMilli(millis).atZone(zone)
    val todayMidnight = LocalDate.now(zone).atStartOfDay(zone).toInstant().toEpochMilli()

    if (millis >= todayMidnight && diff < 24L * 60L * 60_000L) {
        return "${diff / (60L * 60_000L)}h ago"
    }

    val yesterdayMidnight = LocalDate.now(zone).minusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()
    if (millis >= yesterdayMidnight && millis < todayMidnight) return "yesterday"

    val weekStart = LocalDate.now(zone).minusDays(6).atStartOfDay(zone).toInstant().toEpochMilli()
    if (millis >= weekStart) {
        return date.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault())
    }

    return "${date.month.getDisplayName(TextStyle.SHORT, Locale.getDefault())} ${date.dayOfMonth}"
}

/** The four display buckets, in the web's BUCKET_ORDER, with BUCKET_LABELS. */
enum class DateBucket(val label: String) {
    TODAY("Today"),
    YESTERDAY("Yesterday"),
    THIS_WEEK("Earlier this week"),
    EARLIER("Earlier"),
}

/** Active / queued items (sortTime == 0) are always bucketed as 'today'. */
fun getDateBucket(sortTime: Long): DateBucket {
    if (sortTime == 0L) return DateBucket.TODAY
    val zone = ZoneId.systemDefault()
    val today = LocalDate.now(zone)
    val todayMidnight = today.atStartOfDay(zone).toInstant().toEpochMilli()
    val yesterdayMidnight = today.minusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()
    val weekStart = today.minusDays(6).atStartOfDay(zone).toInstant().toEpochMilli()
    return when {
        sortTime >= todayMidnight -> DateBucket.TODAY
        sortTime >= yesterdayMidnight -> DateBucket.YESTERDAY
        sortTime >= weekStart -> DateBucket.THIS_WEEK
        else -> DateBucket.EARLIER
    }
}
