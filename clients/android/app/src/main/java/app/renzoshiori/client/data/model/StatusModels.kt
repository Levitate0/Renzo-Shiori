package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable

/**
 * DTOs for the Status page (RenzoBackend/Controllers/StatusController.cs).
 * Mirrors RenzoFrontend src/lib/api/types.ts SeriesHealth / ProviderHealth /
 * SmallProviderHealth / StatusSummary — the C# enums serialize as plain ints.
 */
object HealthStatusLevel {
    const val GREEN = 0
    const val YELLOW = 1
    const val RED = 2
}

object HealthStatusTargetType {
    const val SERIES = 0
    const val PROVIDER = 1
}

@Serializable
data class SmallProviderHealthDto(
    val providerId: String = "",
    val providerName: String = "",
    val language: String = "",
    val level: Int = 0,
)

@Serializable
data class SeriesHealthDto(
    val id: String = "",
    val title: String = "",
    val thumbnailUrl: String? = null,
    val level: Int = 0,
    val message: String = "",
    val lastChapterDate: String? = null,
    val daysWithoutRelease: Int? = null,
    /** Release cadence in days (absolute value, always positive). */
    val releaseCadenceDays: Int? = null,
    val providers: List<SmallProviderHealthDto> = emptyList(),
)

@Serializable
data class ProviderHealthDto(
    val providerId: String = "",
    val providerName: String = "",
    val scanlator: String = "",
    val language: String = "",
    val level: Int = 0,
    val message: String = "",
    val lastErrorDate: String? = null,
    val consecutiveErrors: Int = 0,
    val isMihonInstalled: Boolean = false,
    val affectedSeries: List<SeriesHealthDto> = emptyList(),
)

@Serializable
data class StatusSummaryDto(
    val totalYellowSeries: Int = 0,
    val totalRedSeries: Int = 0,
    val totalYellowProviders: Int = 0,
    val totalRedProviders: Int = 0,
)

/**
 * POST /api/status/clear body. The default targetType is deliberately -1 (an
 * impossible value): the shared Retrofit Json is configured with
 * `encodeDefaults = false`, so a 0 ("Series") would otherwise be dropped from
 * the payload instead of being sent explicitly.
 */
@Serializable
data class ClearAlertRequestDto(
    val targetType: Int = -1,
    val targetId: String = "",
)

/** PATCH /api/serie/{id}/cadence body — null clears the user override. */
@Serializable
data class SetCadenceRequestDto(
    val cadenceDays: Int? = null,
)

@Serializable
data class SetCadenceResponseDto(
    val releaseCadenceDays: Int? = null,
    val isUserSet: Boolean = false,
)
