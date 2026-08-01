package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.WizardImportJobStatusDto
import app.renzoshiori.client.data.model.WizardImportTotalsDto
import app.renzoshiori.client.data.model.WizardImportUsersDto
import app.renzoshiori.client.data.model.WizardOperationResponseDto
import app.renzoshiori.client.data.model.WizardSearchSourceDto
import app.renzoshiori.client.data.model.WizardSettingsDto
import app.renzoshiori.client.data.model.WizardSetupStatusDto
import kotlinx.serialization.json.JsonObject
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query

/**
 * The Import Series wizard's REST surface — a 1:1 port of RenzoFrontend
 * src/lib/api/services/setupWizardService.ts (plus the two searchService calls
 * the re-match dialog uses).
 *
 * Import entries and search results travel as raw [JsonObject]: POST
 * /api/setup/update and POST /api/setup/augment echo back whole server objects,
 * and the web sends them back untouched apart from the field it edited. See the
 * note in SetupWizardModels.kt.
 *
 * Live progress is a SignalR hub (`/progress`) on the web; the native client
 * polls GET /api/setup/status + GET /api/setup/import/status instead.
 */
interface SetupWizardApi {

    /**
     * Scan local files. `titleOnly` scans the configured ImportFolder (e.g. a
     * Suwayomi migration) instead of StorageFolder, registering bare titles for
     * archive-less folders. 400 when titleOnly is asked for and no ImportFolder
     * is configured.
     */
    @POST("api/setup/scan")
    suspend fun scanLocalFiles(@Query("titleOnly") titleOnly: Boolean): WizardOperationResponseDto

    /** Install the additional extensions the scanned series need. */
    @POST("api/setup/install-extensions")
    suspend fun installExtensions(): WizardOperationResponseDto

    /** Search providers for matches against the scanned series. */
    @POST("api/setup/search")
    suspend fun searchProviders(): WizardOperationResponseDto

    /** Latest status (+ last broadcast progress) of every setup job. */
    @GET("api/setup/status")
    suspend fun setupStatus(): WizardSetupStatusDto

    /** Pending imports (List&lt;ImportSeriesEntry&gt;). */
    @GET("api/setup/imports")
    suspend fun imports(): List<JsonObject>

    /** Totals for the Schedule Updates step. */
    @GET("api/setup/imports/totals")
    suspend fun importTotals(): WizardImportTotalsDto

    /** Persist one edited entry. Body is the whole ImportSeriesEntry. */
    @POST("api/setup/update")
    suspend fun updateImport(@Body body: JsonObject): Response<ResponseBody>

    /** Attach hand-picked provider matches to an entry; returns the updated entry. */
    @POST("api/setup/augment")
    suspend fun augment(
        @Query("path") path: String,
        @Body linkedSeries: List<JsonObject>,
    ): JsonObject

    /** Kick off the import itself. */
    @POST("api/setup/import")
    suspend fun importSeries(@Query("disableDownloads") disableDownloads: Boolean): WizardOperationResponseDto

    /** State of the import job — lets a reopened wizard reattach instead of restarting. */
    @GET("api/setup/import/status")
    suspend fun importStatus(): WizardImportJobStatusDto

    /**
     * Users auto-created from an imported renzo.json's UserReadStates. Only the
     * first-run setup wizard acts on this (its identify-user step); the Import
     * Series wizard has no such step, so it is exposed but not called here.
     */
    @GET("api/setup/import/users")
    suspend fun importUsers(): WizardImportUsersDto

    /** Server settings — `importFolder` gates Titles Only, `categories` backs the picker. */
    @GET("api/settings")
    suspend fun settings(): WizardSettingsDto

    /** Keyword search across sources (SearchController) — the re-match dialog. */
    @GET("api/search")
    suspend fun search(
        @Query("keyword") keyword: String,
        @Query("searchSources") searchSources: List<String>?,
        @Query("languages") languages: String? = null,
    ): List<JsonObject>

    /** Sources available to search. */
    @GET("api/search/sources")
    suspend fun searchSources(): List<WizardSearchSourceDto>
}
