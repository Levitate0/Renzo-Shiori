package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable

/**
 * DTOs for the Browse (web /cloud-latest) screen and its add-to-library flow.
 * Field-for-field mirrors of RenzoFrontend src/lib/api/types.ts, so the native
 * screen renders exactly the same data the web page does.
 */

/** Mirrors InLibraryStatus (serialized as int). */
object InLibraryStatus {
    const val NOT_IN_LIBRARY = 0
    const val IN_LIBRARY = 1
    const val IN_LIBRARY_BUT_DISABLED = 2
}

/** Mirrors SearchSource — one row of GET /api/search/sources. */
@Serializable
data class SearchSourceDto(
    val mihonProviderId: String = "",
    val provider: String = "",
    val scanlator: String = "",
    val language: String = "",
    val isStorage: Boolean = false,
    val thumbnailUrl: String? = null,
    val status: Int = SeriesStatus.UNKNOWN,
    val url: String? = null,
)

/** Mirrors LatestGenre — one tag of GET /api/serie/latest/genres. */
@Serializable
data class LatestGenreDto(
    val name: String = "",
    val count: Int = 0,
)

/**
 * Mirrors LatestSeriesInfo — the FULL catalogue row (the older
 * [LatestSeriesDto] in FeedModels.kt only covers a subset and can't drive the
 * spotlight hero, the in-library heart or the details sheet).
 */
@Serializable
data class LatestSeriesRowDto(
    val mihonId: String = "",
    val mihonProviderId: String? = null,
    val provider: String = "",
    val language: String = "",
    val url: String? = null,
    val title: String = "",
    val thumbnailUrl: String? = null,
    val artist: String? = null,
    val author: String? = null,
    val description: String? = null,
    val genre: List<String> = emptyList(),
    val fetchDate: String? = null,
    val chapterCount: Int? = null,
    val latestChapter: Double? = null,
    val latestChapterTitle: String = "",
    val status: Int = SeriesStatus.UNKNOWN,
    val inLibrary: Int = InLibraryStatus.NOT_IN_LIBRARY,
    val seriesId: String? = null,
    val isNsfw: Boolean = false,
)

/**
 * Light typed view over one GET /api/search result. The add-series flow POSTs
 * the *raw* JSON of the selected rows back to /api/search/augment, so this type
 * is display-only — see BrowseApi.searchRaw.
 */
@Serializable
data class LinkedSeriesRowDto(
    val mihonId: String? = null,
    val mihonProviderId: String? = null,
    val providerId: String = "",
    val provider: String = "",
    val lang: String = "",
    val thumbnailUrl: String? = null,
    val title: String = "",
    val linkedIds: List<String> = emptyList(),
    val isStorage: Boolean = false,
    val isLocal: Boolean = false,
    val useCover: Boolean = false,
) {
    /** The id the web uses as a row key: `series.mihonId ?? series.providerId`. */
    val rowId: String get() = mihonId ?: providerId
}
