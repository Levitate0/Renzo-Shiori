package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable

/**
 * DTOs the Library ribbon needs beyond [SeriesInfoDto]: the per-series provider
 * list (drives the "All Sources" filter and the card's source badges), the
 * user's favourite lists (the Favourites dropdown), the categorized-folders
 * settings (the Categories dropdown) and the scrobbler configs (Track all).
 */

/** Mirrors SmallProviderInfo / ProviderSummaryBase. */
@Serializable
data class SmallProviderRowDto(
    val id: String? = null,
    val provider: String = "",
    val scanlator: String = "",
    val language: String = "",
    val isStorage: Boolean = false,
    val thumbnailUrl: String? = null,
    val status: Int = SeriesStatus.UNKNOWN,
    val url: String? = null,
)

/** Mirrors SeriesInfo (BaseSeriesInfo + providers) — GET /api/serie/library. */
@Serializable
data class LibraryRowDto(
    val id: String = "",
    val title: String = "",
    val thumbnailUrl: String = "",
    val author: String? = null,
    val artist: String? = null,
    val description: String? = null,
    val genre: List<String> = emptyList(),
    val status: Int = SeriesStatus.UNKNOWN,
    val chapterCount: Int = 0,
    val lastChapter: Double? = null,
    val lastChangeUTC: String? = null,
    val isActive: Boolean = true,
    val pausedDownloads: Boolean = false,
    val hasUnknown: Boolean = false,
    val category: String? = null,
    val isNsfw: Boolean = false,
    val lastChangeProvider: SmallProviderRowDto? = null,
    val providers: List<SmallProviderRowDto> = emptyList(),
)

/** Mirrors FavoriteList — GET /api/favorites. */
@Serializable
data class FavoriteListDto(
    val id: String = "",
    val name: String = "",
    val parentId: String? = null,
    val sortOrder: Int = 0,
    val seriesIds: List<String> = emptyList(),
)

/** The handful of Settings fields the library ribbon / browse sheet read. */
@Serializable
data class SettingsLiteDto(
    val categorizedFolders: Boolean = false,
    val categories: List<String> = emptyList(),
    val readerEnabled: Boolean = true,
)

/** Mirrors ScrobblerConfig — only what the Track-all button needs. */
@Serializable
data class ScrobblerConfigLiteDto(
    val provider: Int = 0,
    val displayName: String = "",
    val isEnabled: Boolean = false,
    val isConnected: Boolean = false,
)

/** GET /api/serie/scan-status — the Updates page's live scan progress. */
@Serializable
data class ScanStatusDto(
    val waiting: Int = 0,
    val running: Int = 0,
)
