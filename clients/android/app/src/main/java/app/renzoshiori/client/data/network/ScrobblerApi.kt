package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.AutoMatchResultDto
import app.renzoshiori.client.data.model.ComicVineApiKeyDto
import app.renzoshiori.client.data.model.ConfirmMatchRequestDto
import app.renzoshiori.client.data.model.KitsuDirectAuthDto
import app.renzoshiori.client.data.model.MangaDexDirectAuthDto
import app.renzoshiori.client.data.model.OAuthAuthorizeResponseDto
import app.renzoshiori.client.data.model.ScrobblerConfigDto
import app.renzoshiori.client.data.model.ScrobblerConfigUpdateDto
import app.renzoshiori.client.data.model.ScrobblerConnectedDto
import app.renzoshiori.client.data.model.ScrobblerSyncStatusDto
import app.renzoshiori.client.data.model.SeriesMatchSearchDto
import app.renzoshiori.client.data.model.SeriesMatchSearchResultDto
import app.renzoshiori.client.data.model.SeriesMatchStatusDto
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Path
import retrofit2.http.Query

/**
 * Trackers (AniList / MyAnimeList / Kitsu / MangaDex / ComicVine).
 *
 * Native OAuth flow, end to end: [authorize] → open `authUrl` in the system
 * browser → poll [pollCallback] every 2s until `connected` is true. The proxy
 * holds the tokens keyed by `state`, so there is no redirect to capture and no
 * custom URL scheme to register.
 */
interface ScrobblerApi {
    @GET("api/scrobbler/config")
    suspend fun configs(): List<ScrobblerConfigDto>

    /** `provider` is the INT here (query string), not the enum name. */
    @PUT("api/scrobbler/config")
    suspend fun updateConfig(
        @Query("provider") provider: Int,
        @Body body: ScrobblerConfigUpdateDto,
    ): Response<ResponseBody>

    /** `provider` is the enum NAME here (route segment): "AniList", "MyAnimeList". */
    @POST("api/scrobbler/config/{provider}/authorize")
    suspend fun authorize(@Path("provider") provider: String): OAuthAuthorizeResponseDto

    /** Errors mean "not ready yet" — keep polling until it 200s. */
    @GET("api/scrobbler/callback/{provider}")
    suspend fun pollCallback(
        @Path("provider") provider: String,
        @Query("state") state: String,
    ): ScrobblerConnectedDto

    @DELETE("api/scrobbler/config/{provider}")
    suspend fun disconnect(@Path("provider") provider: String): Response<ResponseBody>

    // ── Direct auth (no OAuth round-trip) ───────────────────────────────
    @POST("api/scrobbler/config/kitsu/direct")
    suspend fun kitsuDirect(@Body body: KitsuDirectAuthDto): ScrobblerConnectedDto

    @POST("api/scrobbler/config/mangadex/direct")
    suspend fun mangaDexDirect(@Body body: MangaDexDirectAuthDto): ScrobblerConnectedDto

    @POST("api/scrobbler/config/comicvine/apikey")
    suspend fun comicVineApiKey(@Body body: ComicVineApiKeyDto): Response<ResponseBody>

    // ── Series matching ─────────────────────────────────────────────────
    @GET("api/scrobbler/matches/unmatched")
    suspend fun unmatched(): List<SeriesMatchStatusDto>

    @POST("api/scrobbler/matches/auto")
    suspend fun autoMatchAll(@Query("provider") provider: Int): AutoMatchResultDto

    @POST("api/scrobbler/matches/search")
    suspend fun searchExternal(@Body body: SeriesMatchSearchDto): SeriesMatchSearchResultDto

    @POST("api/scrobbler/matches/confirm")
    suspend fun confirmMatch(@Body body: ConfirmMatchRequestDto): Response<ResponseBody>

    // ── Sync ────────────────────────────────────────────────────────────
    @POST("api/scrobbler/sync")
    suspend fun triggerSync(): Response<ResponseBody>

    @GET("api/scrobbler/sync/status")
    suspend fun syncStatus(): List<ScrobblerSyncStatusDto>
}
