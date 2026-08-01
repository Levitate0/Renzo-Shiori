package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable

/** Mirrors RenzoBackend/Models/Enums/UserLevel.cs (serialized as a plain int, no string converter). */
object UserLevel {
    const val USER = 0
    const val MANAGER = 1
    const val ADMIN = 2
    const val OWNER = 3
}

fun Int.atLeast(level: Int): Boolean = this >= level

@Serializable
data class UserDto(
    val id: String,
    val username: String,
    val avatarBase64: String? = null,
    val avatarContentType: String? = null,
    val level: Int,
    val opdsPath: String,
    val createdAt: String,
    val lastLoginAt: String? = null,
    val isActive: Boolean,
    val hasPassword: Boolean,
    val email: String? = null,
    val preferences: String? = null,
)

@Serializable
data class AuthStatusDto(
    val authenticationEnabled: Boolean,
    val hasUsers: Boolean,
    val users: List<UserDto>? = null,
)

@Serializable
data class LoginRequestDto(
    val username: String,
    val password: String,
    val rememberMe: Boolean,
)

@Serializable
data class LoginResponseDto(
    val token: String,
    val user: UserDto,
)

@Serializable
data class SelectUserRequestDto(
    val username: String,
)

@Serializable
data class SystemInfoPublicDto(
    val product: String,
)
