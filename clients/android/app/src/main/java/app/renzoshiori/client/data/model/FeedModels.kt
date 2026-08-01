package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable

/** Mirrors UpdateFeedItemDto — one row of the Updates feed. */
@Serializable
data class UpdateFeedItemDto(
    val seriesId: String,
    val seriesTitle: String = "",
    val thumbnailUrl: String? = null,
    /** "seriesAdded" or "newChapter". */
    val kind: String = "newChapter",
    val chapterNumber: Double? = null,
    val chapterName: String? = null,
    val provider: String? = null,
    val timestamp: String = "",
    val read: Boolean = false,
)

/** Mirrors LatestSeriesDto — a Browse/discover catalogue row. */
@Serializable
data class LatestSeriesDto(
    val mihonId: String,
    val provider: String = "",
    val language: String = "",
    val title: String = "",
    val isNsfw: Boolean = false,
    val thumbnailUrl: String? = null,
    val author: String? = null,
    val description: String? = null,
    val genre: List<String> = emptyList(),
    val latestChapter: Double? = null,
)

/** QueueStatus enum (int): Waiting=0 Running=1 Completed=2 Failed=3. */
object QueueStatus {
    const val WAITING = 0
    const val RUNNING = 1
    const val COMPLETED = 2
    const val FAILED = 3
}

/** Mirrors DownloadInfoDto (extends DownloadSummaryBase). */
@Serializable
data class DownloadInfoDto(
    val id: String,
    val title: String = "",
    val provider: String = "",
    val language: String = "",
    val scanlator: String? = null,
    val thumbnailUrl: String? = null,
    val url: String? = null,
    val chapter: Double? = null,
    val chapterTitle: String? = null,
    val downloadDateUTC: String? = null,
    val status: Int = QueueStatus.WAITING,
    val scheduledDateUTC: String = "",
    val retries: Int = 0,
)

@Serializable
data class DownloadInfoListDto(
    val totalCount: Int = 0,
    val downloads: List<DownloadInfoDto> = emptyList(),
)

@Serializable
data class DownloadsMetricsDto(
    val downloads: Int = 0,
    val queued: Int = 0,
    val failed: Int = 0,
)
