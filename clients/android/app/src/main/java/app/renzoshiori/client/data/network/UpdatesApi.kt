package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.ScanStatusDto
import app.renzoshiori.client.data.model.UpdateFeedItemDto
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query

/**
 * Updates feed. Separate from ApiService.updates() because the page needs the
 * viewAll (Owner) flag and the live scan-progress endpoint.
 */
interface UpdatesApi {
    @GET("api/serie/updates")
    suspend fun updates(
        @Query("start") start: Int = 0,
        @Query("count") count: Int = 1000,
        @Query("viewAll") viewAll: Boolean = false,
    ): List<UpdateFeedItemDto>

    /** "Update now" — queue a library-wide new-chapter scan. */
    @POST("api/serie/scan-all")
    suspend fun scanAll(): Response<ResponseBody>

    /** Remaining per-provider chapter checks — drives the scan progress bar. */
    @GET("api/serie/scan-status")
    suspend fun scanStatus(): ScanStatusDto
}
