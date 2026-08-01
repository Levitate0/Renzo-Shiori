package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.SaveSiteCookieDto
import app.renzoshiori.client.data.model.SaveSiteLoginDto
import app.renzoshiori.client.data.model.SiteAuthSaveResponseDto
import app.renzoshiori.client.data.model.SiteCredentialDto
import app.renzoshiori.client.data.model.SiteInfoDto
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Path

/** Per-user logins for coin/paid scanlation sites (`api/site-auth`). */
interface SiteAuthApi {
    /** Sites Renzo Shiori knows how to log in to (or that you own series from). */
    @GET("api/site-auth/sites")
    suspend fun sites(): List<SiteInfoDto>

    @GET("api/site-auth")
    suspend fun credentials(): List<SiteCredentialDto>

    @POST("api/site-auth")
    suspend fun save(@Body body: SaveSiteLoginDto): SiteAuthSaveResponseDto

    @POST("api/site-auth/cookie")
    suspend fun saveCookie(@Body body: SaveSiteCookieDto): SiteAuthSaveResponseDto

    /** Test / re-login now. */
    @POST("api/site-auth/{id}/login")
    suspend fun relogin(@Path("id") id: String): SiteAuthSaveResponseDto

    @DELETE("api/site-auth/{id}")
    suspend fun remove(@Path("id") id: String): Response<ResponseBody>
}
