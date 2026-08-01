package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.LatestGenreDto
import app.renzoshiori.client.data.model.LatestSeriesRowDto
import app.renzoshiori.client.data.model.SearchSourceDto
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query

/**
 * Browse (web /cloud-latest) + the add-to-library flow.
 *
 * The add flow round-trips the augment payload as raw [JsonObject]/[JsonArray]
 * on purpose: /api/search/augment returns a FullSeries[] with far more fields
 * than the UI touches (chapters[], chapterList, suggestedFilename…), and
 * /api/serie expects all of them back verbatim. Typing it would silently drop
 * whatever we didn't model.
 */
interface BrowseApi {
    /** Sources available for cross-source search / the Browse source picker. */
    @GET("api/search/sources")
    suspend fun searchSources(): List<SearchSourceDto>

    /** The cached cross-source "latest" catalogue. `genre` repeats per tag. */
    @GET("api/serie/latest")
    suspend fun latest(
        @Query("start") start: Int,
        @Query("count") count: Int,
        @Query("sourceId") sourceId: String? = null,
        @Query("keyword") keyword: String? = null,
        @Query("genre") genre: List<String>? = null,
    ): List<LatestSeriesRowDto>

    /** Distinct tags in the cached catalogue, with counts (tag filter popover). */
    @GET("api/serie/latest/genres")
    suspend fun latestGenres(): List<LatestGenreDto>

    /** Cross-source search — raw rows, kept verbatim for [augment]. */
    @GET("api/search")
    suspend fun searchRaw(
        @Query("keyword") keyword: String,
        @Query("searchSources") searchSources: List<String>? = null,
    ): JsonArray

    /** Expands the selected search rows into full series with chapter lists. */
    @POST("api/search/augment")
    suspend fun augment(@Body linkedSeries: JsonArray): JsonObject

    /** Commits the (edited) augmented response — adds the series to the library. */
    @POST("api/serie")
    suspend fun addSeries(@Body augmentedResponse: JsonObject): JsonObject
}
