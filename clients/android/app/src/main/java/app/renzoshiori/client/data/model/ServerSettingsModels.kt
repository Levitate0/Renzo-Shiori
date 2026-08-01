@file:OptIn(ExperimentalSerializationApi::class)

package app.renzoshiori.client.data.model

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable

/**
 * Instance-wide settings — RenzoBackend/Models/Dto/SettingsDto.cs (which
 * extends EditableSettingsDto). PUT /api/settings binds onto a FRESH DTO, so a
 * field left out of the body silently reverts to the C# property initializer
 * (e.g. `categorizedFolders` would snap back to true). The shared Retrofit Json
 * has `encodeDefaults = false`, which drops any property equal to its Kotlin
 * default — so every field here is nullable with a null default: after a GET
 * they are all non-null, therefore all re-encoded, and the round-trip is
 * lossless. Do NOT "simplify" these to non-null primitives.
 *
 * TimeSpans arrive/leave as "hh:mm:ss" strings; nsfwVisibility is the one
 * string enum ("AlwaysHide" | "HideByDefault" | "Show").
 */
@Serializable
data class ServerSettingsDto(
    val preferredLanguages: List<String>? = null,
    val mihonRepositories: List<String>? = null,
    val numberOfSimultaneousDownloads: Int? = null,
    val numberOfSimultaneousSearches: Int? = null,
    val chapterDownloadFailRetryTime: String? = null,
    val chapterDownloadFailRetries: Int? = null,
    val perTitleUpdateSchedule: String? = null,
    val perSourceUpdateSchedule: String? = null,
    val extensionsCheckForUpdateSchedule: String? = null,
    val categorizedFolders: Boolean? = null,
    val categories: List<String>? = null,
    val flareSolverrEnabled: Boolean? = null,
    val flareSolverrUrl: String? = null,
    val flareSolverrTimeout: String? = null,
    val flareSolverrSessionTtl: String? = null,
    val flareSolverrAsResponseFallback: Boolean? = null,
    val isWizardSetupComplete: Boolean? = null,
    val wizardSetupStepCompleted: Int? = null,
    val numberOfSimultaneousDownloadsPerProvider: Int? = null,
    val pagesInParallelPerChapter: Int? = null,
    val downloadMemoryBudgetMB: Int? = null,
    val maxRequestsPerHost: Int? = null,
    val socksProxyEnabled: Boolean? = null,
    val socksProxyVersion: Int? = null,
    val socksProxyHost: String? = null,
    val socksProxyPort: Int? = null,
    val socksProxyUsername: String? = null,
    val socksProxyPassword: String? = null,
    val nsfwVisibility: String? = null,
    val releaseCadenceMultiplierYellow: Double? = null,
    val releaseCadenceMultiplierRed: Double? = null,
    val releaseCadenceDefaultDays: Int? = null,
    val providerErrorYellowHours: Int? = null,
    val providerErrorRedHours: Int? = null,
    val authenticationEnabled: Boolean? = null,
    val externalDomain: String? = null,
    val allowedOrigins: List<String>? = null,
    val sessionExpirationHours: Int? = null,
    val rememberMeExpirationDays: Int? = null,
    val smtpHost: String? = null,
    val smtpPort: Int? = null,
    val smtpUsername: String? = null,
    val smtpPassword: String? = null,
    val smtpUseSsl: Boolean? = null,
    val smtpFromAddress: String? = null,
    val readerEnabled: Boolean? = null,
    val downloadAllChapters: Boolean? = null,
    val libraryScanIntervalHours: Int? = null,
    val storageFolder: String? = null,
    val importFolder: String? = null,
)

@Serializable
data class SettingsUpdateResponseDto(
    val message: String? = null,
    val setPasswordUrl: String? = null,
)

@Serializable
data class TestEmailRequestDto(
    @EncodeDefault val to: String = "",
)

@Serializable
data class TestEmailResponseDto(
    val success: Boolean = false,
    val message: String = "",
)

/** nsfwVisibility is serialized by name, not by ordinal. */
object NsfwVisibility {
    const val ALWAYS_HIDE = "AlwaysHide"
    const val HIDE_BY_DEFAULT = "HideByDefault"
    const val SHOW = "Show"
}
