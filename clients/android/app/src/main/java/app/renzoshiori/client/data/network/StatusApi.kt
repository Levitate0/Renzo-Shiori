package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.ClearAlertRequestDto
import app.renzoshiori.client.data.model.ProviderHealthDto
import app.renzoshiori.client.data.model.SeriesHealthDto
import app.renzoshiori.client.data.model.SetCadenceRequestDto
import app.renzoshiori.client.data.model.SetCadenceResponseDto
import app.renzoshiori.client.data.model.StatusSummaryDto
import app.renzoshiori.client.data.model.UserDto
import okhttp3.ResponseBody
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.PATCH
import retrofit2.http.POST
import retrofit2.http.Path
import retrofit2.http.Query

/**
 * Status page endpoints — the native twin of RenzoFrontend
 * src/lib/api/services/statusService.ts. `viewAll` is honoured for Owner-level
 * accounts only; passing null omits the query parameter entirely, exactly like
 * the web service's `${viewAll ? '?viewAll=true' : ''}`.
 */
interface StatusApi {
    @GET("api/status/summary")
    suspend fun summary(@Query("viewAll") viewAll: Boolean? = null): StatusSummaryDto

    @GET("api/status/series")
    suspend fun seriesStatus(@Query("viewAll") viewAll: Boolean? = null): List<SeriesHealthDto>

    @GET("api/status/providers")
    suspend fun providerStatus(@Query("viewAll") viewAll: Boolean? = null): List<ProviderHealthDto>

    /** Admin+ — dismisses a series (targetType 0) or provider (1) alert. */
    @POST("api/status/clear")
    suspend fun clearAlert(@Body body: ClearAlertRequestDto): ResponseBody

    /** Manager+ — the per-series "Cadence:" editor on the Series panel. */
    @PATCH("api/serie/{id}/cadence")
    suspend fun setCadence(
        @Path("id") seriesId: String,
        @Body body: SetCadenceRequestDto,
    ): SetCadenceResponseDto

    /** Drives the canAdmin / canOwner gates the web reads from useAuth(). */
    @GET("api/auth/me")
    suspend fun me(): UserDto
}
