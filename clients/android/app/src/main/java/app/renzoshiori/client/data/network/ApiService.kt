package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.AuthStatusDto
import app.renzoshiori.client.data.model.ChapterDetailDto
import app.renzoshiori.client.data.model.LoginRequestDto
import app.renzoshiori.client.data.model.LoginResponseDto
import app.renzoshiori.client.data.model.PreviewPagesDto
import app.renzoshiori.client.data.model.ReaderChapterInfoDto
import app.renzoshiori.client.data.model.ReaderChaptersDto
import app.renzoshiori.client.data.model.ReaderMarkRequestDto
import app.renzoshiori.client.data.model.ReaderProgressRequestDto
import app.renzoshiori.client.data.model.SelectUserRequestDto
import app.renzoshiori.client.data.model.SeriesInfoDto
import app.renzoshiori.client.data.model.SystemInfoPublicDto
import app.renzoshiori.client.data.model.UserDto
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query

interface ApiService {
    // ── Discovery + auth ────────────────────────────────────────────────
    @GET("api/system/info/public")
    suspend fun systemInfo(): SystemInfoPublicDto

    @GET("api/auth/status")
    suspend fun authStatus(): AuthStatusDto

    @POST("api/auth/login")
    suspend fun login(@Body body: LoginRequestDto): LoginResponseDto

    // NOTE: POST /api/auth/select-user returns a BARE UserDto (no token
    // wrapper) — decoding it as LoginResponseDto throws. It lives in
    // AuthExtraApi.selectUserProfile with the correct return type.

    @POST("api/auth/refresh")
    suspend fun refresh(): LoginResponseDto

    @GET("api/auth/me")
    suspend fun me(): UserDto

    @POST("api/auth/logout")
    suspend fun logout(): retrofit2.Response<okhttp3.ResponseBody>

    /** Only the two settings the app chrome itself needs (full settings screen
     * has its own service): the OPDS base domain and whether title-only import
     * is available. */
    @GET("api/settings")
    suspend fun shellSettings(): app.renzoshiori.client.data.model.ShellSettingsDto

    // ── Library / series ────────────────────────────────────────────────
    @GET("api/serie/library")
    suspend fun library(): List<SeriesInfoDto>

    /** Backend returns SeriesExtendedDto (a BaseSeriesDto superset) — extra
     * fields are ignored, SeriesInfoDto covers everything the hero shows. */
    @GET("api/serie")
    suspend fun series(@Query("id") id: String): SeriesInfoDto

    @GET("api/serie/chapters")
    suspend fun seriesChapters(@Query("seriesId") seriesId: String): List<ChapterDetailDto>

    @GET("api/serie/updates")
    suspend fun updates(
        @Query("start") start: Int = 0,
        @Query("count") count: Int = 60,
    ): List<app.renzoshiori.client.data.model.UpdateFeedItemDto>

    @GET("api/serie/latest")
    suspend fun latest(
        @Query("start") start: Int = 0,
        @Query("count") count: Int = 40,
        @Query("sourceId") sourceId: String? = null,
        @Query("keyword") keyword: String? = null,
    ): List<app.renzoshiori.client.data.model.LatestSeriesDto>

    /** "Update now" — queue a library-wide new-chapter scan. */
    @POST("api/serie/scan-all")
    suspend fun scanAll(): retrofit2.Response<okhttp3.ResponseBody>

    // ── Download queue ──────────────────────────────────────────────────
    @GET("api/downloads")
    suspend fun downloads(
        @Query("status") status: Int,
        @Query("limit") limit: Int = 100,
    ): app.renzoshiori.client.data.model.DownloadInfoListDto

    @GET("api/downloads/metrics")
    suspend fun downloadMetrics(): app.renzoshiori.client.data.model.DownloadsMetricsDto

    // ── Reader ──────────────────────────────────────────────────────────
    @GET("api/reader/chapters")
    suspend fun readerChapters(@Query("seriesId") seriesId: String): ReaderChaptersDto

    /** `filename` must already be base64url-encoded (see [encodeFilename]). */
    @GET("api/reader/chapter-info")
    suspend fun chapterInfo(
        @Query("seriesId") seriesId: String,
        @Query("filename", encoded = true) filename: String,
    ): ReaderChapterInfoDto

    @GET("api/reader/stream/pages")
    suspend fun streamPages(
        @Query("seriesId") seriesId: String,
        @Query("chapter") chapter: Double,
        @Query("refresh") refresh: Boolean = false,
    ): PreviewPagesDto

    @POST("api/reader/progress")
    suspend fun setProgress(@Body body: ReaderProgressRequestDto)

    @POST("api/reader/mark")
    suspend fun markChapters(@Body body: ReaderMarkRequestDto)
}

/** Base64url — chapter filenames can contain characters that break query
 * strings. Mirrors RenzoFrontend readerService.encodeFilename exactly. */
fun encodeFilename(filename: String): String =
    android.util.Base64.encodeToString(
        filename.toByteArray(Charsets.UTF_8),
        android.util.Base64.URL_SAFE or android.util.Base64.NO_PADDING or android.util.Base64.NO_WRAP,
    )

/** Absolute page-image URL for a DOWNLOADED chapter (Coil fetches it with the Bearer interceptor). */
fun pageUrl(baseUrl: String, seriesId: String, filename: String, page: Int): String =
    "$baseUrl/api/reader/page?seriesId=$seriesId&filename=${encodeFilename(filename)}&page=$page"

/** Absolute page-image URL for a not-yet-downloaded chapter, streamed live from the source. */
fun streamPageUrl(baseUrl: String, seriesId: String, chapter: Double, page: Int): String =
    "$baseUrl/api/reader/stream/page?seriesId=$seriesId&chapter=$chapter&page=$page"

/** thumbnailUrl fields arrive server-relative ("/api/image/{key}") — make them absolute. */
fun absoluteUrl(baseUrl: String, pathOrUrl: String): String =
    if (pathOrUrl.startsWith("http")) pathOrUrl else baseUrl.trimEnd('/') + pathOrUrl
