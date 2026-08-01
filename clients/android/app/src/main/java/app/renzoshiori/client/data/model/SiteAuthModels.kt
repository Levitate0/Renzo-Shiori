@file:OptIn(ExperimentalSerializationApi::class)

package app.renzoshiori.client.data.model

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable

/**
 * Per-user coin/paid-site logins — RenzoBackend `api/site-auth`. The password is
 * encrypted server-side and never returned; sites that can't be automated
 * (CAPTCHA / Google sign-in) take a pasted session cookie instead.
 */

@Serializable
data class SiteInfoDto(
    val provider: String = "",
    val domain: String = "",
    val supportsAutoLogin: Boolean = false,
    /** True = the source advertises a coin/paid gate. */
    val coin: Boolean = false,
)

/** status: ok | needs_login | failed | manual_cookie */
@Serializable
data class SiteCredentialDto(
    val id: String = "",
    val provider: String = "",
    val username: String = "",
    val status: String = "",
    val statusDetail: String? = null,
    val lastLoginAt: String? = null,
    val supportsAutoLogin: Boolean = false,
)

@Serializable
data class SiteLoginResultDto(
    val success: Boolean = false,
    val status: String = "",
    val detail: String? = null,
    val cookiesInjected: Int = 0,
)

@Serializable
data class SiteAuthSaveResponseDto(
    val credential: SiteCredentialDto? = null,
    val result: SiteLoginResultDto = SiteLoginResultDto(),
)

@Serializable
data class SaveSiteLoginDto(
    @EncodeDefault val provider: String = "",
    @EncodeDefault val username: String = "",
    @EncodeDefault val password: String = "",
)

@Serializable
data class SaveSiteCookieDto(
    @EncodeDefault val provider: String = "",
    @EncodeDefault val username: String = "",
    @EncodeDefault val cookie: String = "",
)
