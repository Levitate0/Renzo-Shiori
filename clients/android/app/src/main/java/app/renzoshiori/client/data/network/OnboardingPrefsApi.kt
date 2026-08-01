package app.renzoshiori.client.data.network

import app.renzoshiori.client.data.model.UserDto
import kotlinx.serialization.json.JsonObject
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.PUT

/**
 * The per-user preferences blob, as used by the first-run walkthrough.
 *
 * `preferences` is a JSON *string* on the User entity that also carries the
 * theme preset, accent and source-priority keys — see RenzoFrontend
 * src/lib/utils/theme-prefs.ts. Anything written back MUST be the parsed blob
 * with one key merged in, never a freshly-built object, or another feature's
 * settings are wiped. The body here is a raw [JsonObject] so no unset field of
 * UpdateUserDto (level, email, avatar…) is ever sent along.
 */
interface OnboardingPrefsApi {

    @GET("api/auth/me")
    suspend fun me(): UserDto

    @PUT("api/auth/me")
    suspend fun updateMe(@Body body: JsonObject): UserDto
}
