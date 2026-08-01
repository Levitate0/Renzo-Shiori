@file:OptIn(ExperimentalSerializationApi::class)

package app.renzoshiori.client.data.model

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable

/**
 * Scrobbler / tracker DTOs — RenzoBackend/Models/Dto/ScrobblerDto.cs.
 *
 * The provider enum is a plain int in every BODY and QUERY, but the `{provider}`
 * ROUTE segment is matched by NAME ("AniList", "MyAnimeList", "kitsu"…) — see
 * [scrobblerRouteName]. Getting that backwards is a 404 that looks like a
 * broken connect flow.
 */
object ScrobblerProvider {
    const val MY_ANIME_LIST = 0
    const val ANILIST = 1
    const val COMIC_VINE = 2
    const val KITSU = 3
    const val MANGADEX = 4

    val all = listOf(MY_ANIME_LIST, ANILIST, COMIC_VINE, KITSU, MANGADEX)
}

/** The C# enum member name, as the route segment expects it. */
fun scrobblerRouteName(provider: Int): String = when (provider) {
    ScrobblerProvider.MY_ANIME_LIST -> "MyAnimeList"
    ScrobblerProvider.ANILIST -> "AniList"
    ScrobblerProvider.COMIC_VINE -> "ComicVine"
    ScrobblerProvider.KITSU -> "Kitsu"
    ScrobblerProvider.MANGADEX -> "MangaDex"
    else -> "Unknown"
}

/** The two-letter tile the web renders in place of a provider logo. */
fun scrobblerShortIcon(provider: Int): String = when (provider) {
    ScrobblerProvider.MY_ANIME_LIST -> "MAL"
    ScrobblerProvider.ANILIST -> "AL"
    ScrobblerProvider.COMIC_VINE -> "CV"
    ScrobblerProvider.KITSU -> "KT"
    ScrobblerProvider.MANGADEX -> "MD"
    else -> "?"
}

@Serializable
data class ScrobblerConfigDto(
    val provider: Int = 0,
    val displayName: String = "",
    val icon: String = "",
    val link: String? = null,
    val linkDescription: String? = null,
    val isEnabled: Boolean = false,
    val isConnected: Boolean = false,
    val autoSync: Boolean = false,
    val lastSyncAt: String? = null,
    val lastUploadAt: String? = null,
    val lastDownloadAt: String? = null,
    val supportsDirectAuth: Boolean = false,
    val seriesUrlTemplate: String? = null,
    val imageTemplateUrl: String? = null,
)

/** PUT /api/scrobbler/config?provider={int} — omitted field = unchanged. */
@Serializable
data class ScrobblerConfigUpdateDto(
    val isEnabled: Boolean? = null,
    val autoSync: Boolean? = null,
)

@Serializable
data class OAuthAuthorizeResponseDto(
    val authUrl: String = "",
    val state: String = "",
)

@Serializable
data class ScrobblerConnectedDto(
    val connected: Boolean = false,
)

@Serializable
data class KitsuDirectAuthDto(
    @EncodeDefault val email: String = "",
    @EncodeDefault val password: String = "",
)

@Serializable
data class MangaDexDirectAuthDto(
    @EncodeDefault val username: String = "",
    @EncodeDefault val password: String = "",
    @EncodeDefault val clientId: String = "",
    @EncodeDefault val clientSecret: String = "",
)

@Serializable
data class ComicVineApiKeyDto(
    @EncodeDefault val apiKey: String = "",
)

/** mappingStatus: 0 not matched, 1 auto-matched, 2 confirmed, 3 disabled. */
@Serializable
data class SeriesMatchStatusDto(
    val seriesId: String = "",
    val seriesTitle: String = "",
    val seriesCoverUrl: String? = null,
    val provider: Int = 0,
    val mappingStatus: Int = 0,
    val externalSeriesId: String? = null,
    val externalSeriesTitle: String? = null,
    val externalCoverUrl: String? = null,
    val externalSeriesUrl: String? = null,
    val matchScore: Double? = null,
)

@Serializable
data class AutoMatchResultDto(
    val autoMatched: Int = 0,
    val leftUnmatched: Int = 0,
    val totalSeries: Int = 0,
)

@Serializable
data class SeriesMatchSearchDto(
    @EncodeDefault val provider: Int = 0,
    @EncodeDefault val query: String = "",
)

@Serializable
data class ScrobblerSearchResultDto(
    val externalId: String = "",
    val title: String = "",
    val alternateTitles: List<String> = emptyList(),
    val coverUrl: String? = null,
    val type: String? = null,
)

@Serializable
data class SeriesMatchSearchResultDto(
    val provider: Int = 0,
    val results: List<ScrobblerSearchResultDto> = emptyList(),
)

@Serializable
data class ConfirmMatchRequestDto(
    @EncodeDefault val seriesId: String = "",
    @EncodeDefault val provider: Int = 0,
    @EncodeDefault val externalSeriesId: String = "",
    val externalSeriesTitle: String? = null,
)

@Serializable
data class ScrobblerSyncStatusDto(
    val provider: Int = 0,
    val lastSyncAt: String? = null,
    val lastUploadAt: String? = null,
    val lastDownloadAt: String? = null,
    val seriesMatched: Int = 0,
    val seriesUnmatched: Int = 0,
    val seriesIgnored: Int = 0,
)
