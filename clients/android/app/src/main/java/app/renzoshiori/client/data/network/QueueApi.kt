package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.DownloadInfoListDto
import app.renzoshiori.client.data.model.DownloadsMetricsDto
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.GET
import retrofit2.http.PATCH
import retrofit2.http.POST
import retrofit2.http.Query

/** ErrorDownloadAction (Models/Enums/ErrorDownloadAction.cs), serialized as int. */
object ErrorDownloadAction {
    const val RETRY = 0
    const val DELETE = 1
}

/**
 * The Queue page's server endpoints. Separate from ApiService.downloads()
 * because the page needs the keyword + viewAll parameters the shared helper
 * doesn't expose, plus the retry/remove mutation and the Jobs action.
 */
interface QueueApi {
    @GET("api/downloads")
    suspend fun downloads(
        @Query("status") status: Int,
        @Query("limit") limit: Int = 100,
        @Query("keyword") keyword: String? = null,
        @Query("viewAll") viewAll: Boolean = false,
    ): DownloadInfoListDto

    @GET("api/downloads/metrics")
    suspend fun metrics(@Query("viewAll") viewAll: Boolean = false): DownloadsMetricsDto

    /** Retry (0) or delete (1) a completed/failed queue entry. */
    @PATCH("api/downloads")
    suspend fun manageDownload(
        @Query("id") id: String,
        @Query("action") action: Int,
    ): Response<ResponseBody>

    /** Jobs dialog → "Update All Series". */
    @POST("api/serie/update-all")
    suspend fun updateAllSeries(): Response<ResponseBody>
}
