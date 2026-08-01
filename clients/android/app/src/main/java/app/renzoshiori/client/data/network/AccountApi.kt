package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.BackupImportResultDto
import app.renzoshiori.client.data.model.ChangePasswordRequestDto
import app.renzoshiori.client.data.model.ChangePasswordResponseDto
import app.renzoshiori.client.data.model.CreateUserRequestDto
import app.renzoshiori.client.data.model.InviteMessageDto
import app.renzoshiori.client.data.model.OpdsPathDto
import app.renzoshiori.client.data.model.UpdateUserDto
import app.renzoshiori.client.data.model.UserDto
import okhttp3.MultipartBody
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.Multipart
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Part
import retrofit2.http.Path

/**
 * Account + user administration. Bearer auth is attached by the shared
 * NetworkModule interceptor, so nothing here passes a token explicitly.
 *
 * Endpoints that answer 204/empty return `Response<ResponseBody>` rather than
 * `Unit` — the kotlinx converter would otherwise try to *decode* an empty body.
 */
interface AccountApi {
    // ── Signed-in user ──────────────────────────────────────────────────
    @GET("api/auth/me")
    suspend fun me(): UserDto

    /** Every field of [UpdateUserDto] is optional; omitted = unchanged. */
    @PUT("api/auth/me")
    suspend fun updateMe(@Body body: UpdateUserDto): UserDto

    @POST("api/auth/change-password")
    suspend fun changePassword(@Body body: ChangePasswordRequestDto): ChangePasswordResponseDto

    // ── User management (Admin+) ────────────────────────────────────────
    @GET("api/users")
    suspend fun listUsers(): List<UserDto>

    @GET("api/users/{id}")
    suspend fun getUser(@Path("id") id: String): UserDto

    @POST("api/users")
    suspend fun createUser(@Body body: CreateUserRequestDto): UserDto

    /** `level` / `isActive` ARE honoured here (unlike on /auth/me). */
    @PUT("api/users/{id}")
    suspend fun updateUser(@Path("id") id: String, @Body body: UpdateUserDto): UserDto

    @DELETE("api/users/{id}")
    suspend fun deleteUser(@Path("id") id: String): Response<ResponseBody>

    @POST("api/users/{id}/generate-invite")
    suspend fun generateInvite(@Path("id") id: String): InviteMessageDto

    @POST("api/users/{id}/regenerate-opds")
    suspend fun regenerateOpds(@Path("id") id: String): OpdsPathDto

    // ── Suwayomi/Tachiyomi backup import ────────────────────────────────
    /** multipart/form-data, field name `file`, ≤200MB, .tachibk/.proto.gz. */
    @Multipart
    @POST("api/reader/import-backup")
    suspend fun importBackup(@Part file: MultipartBody.Part): BackupImportResultDto
}
