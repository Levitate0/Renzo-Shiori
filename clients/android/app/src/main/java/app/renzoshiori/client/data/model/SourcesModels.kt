package app.renzoshiori.client.data.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * DTOs for the Sources page — RenzoBackend ExtensionDto (api/provider/list) and
 * the extension version manager (api/extensions). Mirrors RenzoFrontend
 * src/lib/api/types.ts `Provider` / `ExtensionRepository` / `ExtensionEntry`
 * and services/extensionsService.ts.
 *
 * NOTE: `isInstaled` is misspelled in the API. Do NOT "fix" it — the typo is
 * the wire format (the web's sources/lib.ts carries the same warning).
 */
@Serializable
data class ExtensionSourceDto(
    val name: String = "",
    val lang: String = "",
)

@Serializable
data class ExtensionEntryDto(
    val id: String = "",
    val onlineRepositoryName: String = "",
    val onlineRepositoryId: String = "",
    val isLocal: Boolean = false,
    val name: String = "",
    val downloadUTC: String? = null,
    @SerialName("package") val packageName: String = "",
    val version: String = "",
    val nsfw: Boolean = false,
    val sources: List<ExtensionSourceDto> = emptyList(),
)

@Serializable
data class ExtensionRepositoryDto(
    val name: String = "",
    val id: String = "",
    val entries: List<ExtensionEntryDto> = emptyList(),
)

@Serializable
data class ProviderDto(
    @SerialName("package") val packageName: String = "",
    val name: String = "",
    val thumbnailUrl: String = "",
    val isStorage: Boolean = false,
    val isEnabled: Boolean = false,
    val isBroken: Boolean = false,
    val isDead: Boolean = false,
    /** sic — the API misspells "isInstalled". */
    val isInstaled: Boolean = false,
    /** True when the CURRENT user has this source enabled for their own Search/Browse/Add-series. */
    val isEnabledForMe: Boolean = false,
    val activeEntry: Int = 0,
    val autoUpdate: Boolean = false,
    val onlineRepositories: List<ExtensionRepositoryDto> = emptyList(),
)

@Serializable
data class ExtensionVersionInfoDto(
    val version: String = "",
    val isLocal: Boolean = false,
    val repositoryId: String = "",
)

@Serializable
data class ExtensionInfoDto(
    val name: String = "",
    val autoUpdate: Boolean = false,
    val activeVersion: String = "",
    val versions: List<ExtensionVersionInfoDto> = emptyList(),
)

/** POST /api/serie/apply-default-priority response. */
@Serializable
data class ApplyDefaultPriorityResultDto(
    val success: Boolean = false,
    val error: String? = null,
    val seriesConsidered: Int = 0,
    val seriesReordered: Int = 0,
    val seriesAdopted: Int = 0,
    val chaptersQueued: Int = 0,
)

/**
 * PUT /api/auth/me body used by the Default priority order tab. Only the
 * `preferences` blob is sent; every other field on UpdateUserDto is nullable
 * server-side and left untouched.
 */
@Serializable
data class UpdatePreferencesRequestDto(
    val preferences: String = "",
)

/** Slice of GET /api/settings — only the NSFW visibility the toolbar reads. */
@Serializable
data class NsfwSettingsDto(
    /** "AlwaysHide" | "HideByDefault" | "Show" (JsonStringEnumConverter server-side). */
    val nsfwVisibility: String = "HideByDefault",
)

/** Mirrors RenzoBackend Models/Enums/EntryType.cs (serialized as a plain int). */
object ProviderPreferenceEntryType {
    const val COMBO_BOX = 0
    const val COMBO_CHECK_BOX = 1
    const val TEXT_BOX = 2
    const val SWITCH = 3
}
