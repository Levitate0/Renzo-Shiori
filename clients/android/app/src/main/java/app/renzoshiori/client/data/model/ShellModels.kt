package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable

/**
 * The slice of SettingsDto the app chrome reads (user-menu.tsx uses exactly
 * these two): `externalDomain` to build the full OPDS URL it copies, and
 * `importFolder` to decide whether title-only import is offered at all.
 */
@Serializable
data class ShellSettingsDto(
    val externalDomain: String = "",
    val importFolder: String = "",
)
