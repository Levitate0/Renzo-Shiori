package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.ApplyDefaultPriorityResultDto
import app.renzoshiori.client.data.model.ExtensionInfoDto
import app.renzoshiori.client.data.model.NsfwSettingsDto
import app.renzoshiori.client.data.model.ProviderDto
import app.renzoshiori.client.data.model.UpdatePreferencesRequestDto
import app.renzoshiori.client.data.model.UserDto
import kotlinx.serialization.json.JsonObject
import okhttp3.MultipartBody
import okhttp3.ResponseBody
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.Multipart
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Part
import retrofit2.http.Path
import retrofit2.http.Query

/**
 * Sources page endpoints — the native twin of RenzoFrontend
 * src/lib/api/services/{providerService,extensionsService,userService}.ts plus
 * the two series/settings calls the "Default priority order" tab needs.
 *
 * Provider preferences are round-tripped as a raw [JsonObject] on purpose:
 * `defaultValue` / `currentValue` are polymorphic (string, bool, or string
 * array) and the DTO is POSTed back verbatim, so nothing may be dropped by a
 * typed re-serialization.
 */
interface SourcesApi {
    // ── Providers (api/provider) ────────────────────────────────────────
    @GET("api/provider/list")
    suspend fun providers(): List<ProviderDto>

    /** Real install: downloads/compiles the extension for the whole instance. */
    @POST("api/provider/install/{pkgName}")
    suspend fun installProvider(
        @Path("pkgName") pkgName: String,
        @Query("repoName") repoName: String? = null,
        @Query("force") force: Boolean? = null,
    ): ResponseBody

    /**
     * Sideload an .apk as a provider. The body is the installed package name —
     * read as a raw [ResponseBody] because ASP.NET's StringOutputFormatter can
     * answer this one as unquoted text/plain rather than a JSON string.
     */
    @Multipart
    @POST("api/provider/install/file")
    suspend fun installProviderFromFile(
        @Part file: MultipartBody.Part,
        @Query("force") force: Boolean? = null,
    ): ResponseBody

    /** "Install" when the package already exists instance-wide: enable it for me only. */
    @POST("api/provider/my-sources/package/{pkgName}")
    suspend fun enablePackageForMe(@Path("pkgName") pkgName: String): ResponseBody

    /** Removes the package from MY enabled set; the shared install is untouched. */
    @DELETE("api/provider/my-sources/package/{pkgName}")
    suspend fun disablePackageForMe(@Path("pkgName") pkgName: String): ResponseBody

    @GET("api/provider/preferences/{pkgName}")
    suspend fun providerPreferences(@Path("pkgName") pkgName: String): JsonObject

    @POST("api/provider/preferences")
    suspend fun setProviderPreferences(@Body body: JsonObject): ResponseBody

    // ── Extension versions (api/extensions, Manager+) ───────────────────
    @GET("api/extensions")
    suspend fun extensions(): List<ExtensionInfoDto>

    /** Switch active version; picking a non-latest version pins the extension. */
    @POST("api/extensions/active")
    suspend fun setActiveVersion(
        @Query("name") name: String,
        @Query("version") version: String,
    ): ExtensionInfoDto

    /** Pin/unpin; unpinning re-activates the newest installed version. */
    @POST("api/extensions/autoupdate")
    suspend fun setAutoUpdate(
        @Query("name") name: String,
        @Query("enabled") enabled: Boolean,
    ): ExtensionInfoDto

    /** Sideload an APK; compiled in a temp folder, swapped in only on success. */
    @Multipart
    @POST("api/extensions/sideload")
    suspend fun sideloadExtension(@Part file: MultipartBody.Part): ExtensionInfoDto

    // ── Account + library (Default priority order tab) ──────────────────
    @GET("api/auth/me")
    suspend fun me(): UserDto

    @PUT("api/auth/me")
    suspend fun updateMe(@Body body: UpdatePreferencesRequestDto): UserDto

    @POST("api/serie/apply-default-priority")
    suspend fun applyDefaultPriorityToAll(): ApplyDefaultPriorityResultDto

    @GET("api/settings")
    suspend fun settings(): NsfwSettingsDto
}
