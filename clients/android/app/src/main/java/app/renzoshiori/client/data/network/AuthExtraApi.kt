package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.ApiErrorDto
import app.renzoshiori.client.data.model.ForgotPasswordRequestDto
import app.renzoshiori.client.data.model.GenericResultDto
import app.renzoshiori.client.data.model.LoginResponseDto
import app.renzoshiori.client.data.model.ResetPasswordRequestDto
import app.renzoshiori.client.data.model.SelectUserRequestDto
import app.renzoshiori.client.data.model.SetPasswordRequestDto
import app.renzoshiori.client.data.model.UserDto
import kotlinx.serialization.json.Json
import retrofit2.HttpException
import retrofit2.http.Body
import retrofit2.http.POST

/**
 * The public auth endpoints the web app's /auth/forgot-password,
 * /auth/reset-password and /auth/set-password pages call, which
 * [ApiService] doesn't cover.
 *
 * Also carries the CORRECT `select-user` signature: AuthController's
 * POST /api/auth/select-user returns a bare `UserDto` (auth-disabled profile
 * mode issues no JWT — the web client identifies itself with the
 * `X-Renzo-User` header from then on), not a `{ token, user }` LoginResponse.
 *
 * All of these are unauthenticated, and all except select-user are rate
 * limited 5/min/IP, so their error text is surfaced verbatim
 * (see [serverErrorMessage]).
 */
interface AuthExtraApi {
    @POST("api/auth/forgot-password")
    suspend fun forgotPassword(@Body body: ForgotPasswordRequestDto): GenericResultDto

    @POST("api/auth/reset-password")
    suspend fun resetPassword(@Body body: ResetPasswordRequestDto): GenericResultDto

    @POST("api/auth/set-password")
    suspend fun setPassword(@Body body: SetPasswordRequestDto): LoginResponseDto

    @POST("api/auth/select-user")
    suspend fun selectUserProfile(@Body body: SelectUserRequestDto): UserDto
}

private val authErrorJson = Json { ignoreUnknownKeys = true }

/**
 * Pulls the server's own message out of a failed call so the UI can show it
 * word-for-word, exactly like the web app does (apiClient rethrows
 * `body.error` as the Error message). Falls back to [fallback] for bodies that
 * aren't RenzoBackend's `{ "error": … }` shape.
 */
fun Throwable.serverErrorMessage(fallback: String): String {
    if (this is HttpException) {
        val raw = runCatching { response()?.errorBody()?.string() }.getOrNull()
        if (!raw.isNullOrBlank()) {
            val parsed = runCatching {
                authErrorJson.decodeFromString(ApiErrorDto.serializer(), raw).error
            }.getOrNull()
            if (!parsed.isNullOrBlank()) return parsed
        }
        // The login rate limiter answers 429 with no body at all.
        if (code() == 429) return "Too many attempts. Try again in about a minute."
    }
    if (this is java.io.IOException) return "Can't reach the server. Check your connection and try again."
    return fallback
}
