@file:OptIn(ExperimentalSerializationApi::class)

package app.renzoshiori.client.data.model

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable

/**
 * Account / user-management DTOs (RenzoBackend AuthController + UsersController).
 *
 * NOTE on nullability: the shared Retrofit Json is configured with
 * `encodeDefaults = false`, so any property whose value equals its declared
 * default is DROPPED from the request body. That is exactly the semantics
 * UpdateUserDto wants ("omitted = leave unchanged"), which is why every field
 * here is nullable-with-null-default rather than a non-null default — sending
 * `email = ""` (clear it) or `isActive = false` must not be mistaken for
 * "unset". Fields that must always be transmitted carry @EncodeDefault.
 */

/** PUT /api/auth/me and PUT /api/users/{id}. Every field optional. */
@Serializable
data class UpdateUserDto(
    /** Raw base64, no `data:` prefix; ≤2MB decoded. */
    val avatarBase64: String? = null,
    /** image/png | image/jpeg | image/gif | image/webp */
    val avatarContentType: String? = null,
    val removeAvatar: Boolean? = null,
    /** "" clears the address. */
    val email: String? = null,
    /** A JSON *string* — the whole per-user preferences blob. */
    val preferences: String? = null,
    /** Honoured only on PUT /api/users/{id} (admin route), never on /auth/me. */
    val level: Int? = null,
    val isActive: Boolean? = null,
)

@Serializable
data class ChangePasswordRequestDto(
    @EncodeDefault val currentPassword: String = "",
    @EncodeDefault val newPassword: String = "",
)

@Serializable
data class ChangePasswordResponseDto(
    val success: Boolean = false,
    val error: String? = null,
)

@Serializable
data class CreateUserRequestDto(
    @EncodeDefault val username: String = "",
    @EncodeDefault val level: Int = 0,
)

/** POST /api/users/{id}/generate-invite */
@Serializable
data class InviteMessageDto(
    val message: String = "",
    val token: String? = null,
    val opdsPath: String? = null,
)

/** POST /api/users/{id}/regenerate-opds */
@Serializable
data class OpdsPathDto(
    val opdsPath: String = "",
)

/** POST /api/reader/import-backup */
@Serializable
data class BackupImportResultDto(
    val backupSeries: Int = 0,
    val matchedSeries: Int = 0,
    val updatedChapters: Int = 0,
    val bookmarks: Int = 0,
    val unmatched: List<String> = emptyList(),
)
