package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.FavoriteListDto
import app.renzoshiori.client.data.model.LibraryRowDto
import app.renzoshiori.client.data.model.ScrobblerConfigLiteDto
import app.renzoshiori.client.data.model.SettingsLiteDto
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query

/**
 * Everything the Library ribbon needs that ApiService.library() doesn't cover:
 * the viewAll (Owner) flag, the per-series provider list, the user's favourite
 * lists, the categorized-folder settings and the Track-all scrobbler action.
 */
interface LibraryExtrasApi {
    /** Owner-level accounts can flip viewAll to see every user's library. */
    @GET("api/serie/library")
    suspend fun library(@Query("viewAll") viewAll: Boolean = false): List<LibraryRowDto>

    @GET("api/favorites")
    suspend fun favorites(): List<FavoriteListDto>

    @GET("api/settings")
    suspend fun settings(): SettingsLiteDto

    @GET("api/scrobbler/config")
    suspend fun scrobblerConfigs(): List<ScrobblerConfigLiteDto>

    /** "Track all" — auto-match the whole library on one connected tracker. */
    @POST("api/scrobbler/matches/auto")
    suspend fun autoMatchAll(@Query("provider") provider: Int): Response<ResponseBody>
}
