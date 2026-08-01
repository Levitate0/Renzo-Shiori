package app.renzoshiori.client.data.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.longOrNull

/**
 * DTOs for the Import Series wizard (RenzoBackend SetupWizardController,
 * `api/setup`) — the native side of RenzoFrontend's setupWizardService.ts.
 *
 * Every field carries a default (kotlinx.serialization requirement for
 * tolerant decoding) and every name matches the backend's camelCase
 * [JsonPropertyName] exactly. Enums cross the wire as INTEGERS, so they are
 * modelled as Int constants rather than Kotlin enums.
 */

/** RenzoBackend Models/Enums/ImportStatus.cs */
object WizardImportStatus {
    const val IMPORT = 0
    const val SKIP = 1
    const val DO_NOT_CHANGE = 2
    const val COMPLETED = 3
}

/** RenzoBackend Models/Enums/Action.cs */
object WizardAction {
    const val ADD = 0
    const val SKIP = 1
}

/** RenzoBackend Models/Enums/ProgressStatus.cs (mirrors types.ts ProgressStatus). */
object WizardProgressStatus {
    const val STARTED = 0
    const val IN_PROGRESS = 1
    const val COMPLETED = 2
    const val FAILED = 3
}

/**
 * The string values GET /api/setup/status reports per job (the queue row's
 * QueueStatus.ToString()), or null when that job has never run.
 */
object WizardJobStatus {
    const val RUNNING = "Running"
    const val WAITING = "Waiting"
    const val COMPLETED = "Completed"
    const val FAILED = "Failed"

    fun isCompleted(value: String?): Boolean = value == COMPLETED
    fun isInFlight(value: String?): Boolean = value == RUNNING || value == WAITING
}

/** `{ success, message, alreadyRunning? }` — every enqueue endpoint's reply. */
@Serializable
data class WizardOperationResponseDto(
    val success: Boolean = false,
    val message: String = "",
    val alreadyRunning: Boolean = false,
)

/** RenzoBackend Models/ProgressState.cs (the `download` payload is unused here). */
@Serializable
data class WizardProgressStateDto(
    val id: String = "",
    val jobType: Int = 0,
    val progressStatus: Int = 0,
    val percentage: Double = 0.0,
    val message: String = "",
    val errorMessage: String? = null,
)

/** GET /api/setup/status */
@Serializable
data class WizardSetupStatusDto(
    val scanLocalFiles: String? = null,
    val installAdditionalExtensions: String? = null,
    val searchProviders: String? = null,
    val importSeries: String? = null,
    val scanLocalFilesProgress: WizardProgressStateDto? = null,
    val installAdditionalExtensionsProgress: WizardProgressStateDto? = null,
    val searchProvidersProgress: WizardProgressStateDto? = null,
)

/** GET /api/setup/import/status */
@Serializable
data class WizardImportJobStatusDto(
    val isRunning: Boolean = false,
    val isQueued: Boolean = false,
    val isActive: Boolean = false,
    val hasCompleted: Boolean = false,
    val hasFailed: Boolean = false,
)

/** GET /api/setup/imports/totals — RenzoBackend Models/Dto/ImportTotalsDto.cs */
@Serializable
data class WizardImportTotalsDto(
    val totalSeries: Int = 0,
    val totalProviders: Int = 0,
    val totalDownloads: Int = 0,
)

/** GET /api/setup/import/users */
@Serializable
data class WizardImportUsersDto(
    val hasReadStates: Boolean = false,
    val autoCreatedUsers: List<String> = emptyList(),
    val userCount: Int = 0,
)

/**
 * The two fields of GET /api/settings the wizard needs: `importFolder` gates
 * the "Titles Only" scan, `categories`/`categorizedFolders` back the per-card
 * Category picker on title-only stubs.
 */
@Serializable
data class WizardSettingsDto(
    val importFolder: String = "",
    val categorizedFolders: Boolean = false,
    val categories: List<String> = emptyList(),
)

/** GET /api/search/sources — RenzoBackend Models/Dto/SearchSourceDto.cs */
@Serializable
data class WizardSearchSourceDto(
    val mihonProviderId: String = "",
    val provider: String = "",
    val scanlator: String = "",
    val language: String = "",
    val isStorage: Boolean = false,
    val thumbnailUrl: String? = null,
)

// ─────────────────────────────────────────────────────────────────────────────
// ImportSeriesEntry / LinkedSeriesDto — kept as raw JSON
//
// POST /api/setup/update round-trips the WHOLE entry the server handed us, and
// POST /api/setup/augment round-trips whole search results. The web does this
// by spreading the object it received ({...importInfo, status}), so no field it
// doesn't know about is ever dropped. A typed DTO here would silently strip
// `providers`, `genre`, `Version`, … and (because the shared Retrofit Json is
// configured with encodeDefaults = false) would omit C#-`required` members like
// `title` whenever they happened to equal the Kotlin default. So the entries are
// carried as JsonObject and only *projected* into the read-only view models
// below for rendering.
// ─────────────────────────────────────────────────────────────────────────────

/** One provider match row of an import entry (`series[]`, ProviderSeriesOption). */
data class WizardMatch(
    val index: Int,
    val id: String? = null,
    val provider: String = "",
    val scanlator: String = "",
    val lang: String = "",
    val thumbnailUrl: String? = null,
    val title: String = "",
    val chapterCount: Long = 0,
    val url: String? = null,
    val useCover: Boolean = false,
    val isStorage: Boolean = false,
    val useTitle: Boolean = false,
    val lastChapter: Double? = null,
    val preferred: Boolean = false,
)

/** One pending import (ImportSeriesEntry), plus the raw JSON it came from. */
data class WizardImport(
    val raw: JsonObject,
    val path: String = "",
    val title: String = "",
    val status: Int = WizardImportStatus.IMPORT,
    val action: Int = WizardAction.ADD,
    val continueAfterChapter: Double? = null,
    val type: String = "",
    val isTitleOnly: Boolean = false,
    val series: List<WizardMatch> = emptyList(),
)

private fun JsonObject.str(key: String): String? =
    (this[key] as? JsonPrimitive)?.takeIf { !it.isString || it.content.isNotEmpty() }
        ?.let { if (it.content == "null") null else it.content }

private fun JsonObject.bool(key: String): Boolean =
    (this[key] as? JsonPrimitive)?.booleanOrNull ?: false

private fun JsonObject.num(key: String): Double? = (this[key] as? JsonPrimitive)?.doubleOrNull

private fun JsonObject.whole(key: String): Long = (this[key] as? JsonPrimitive)?.longOrNull ?: 0L

private fun JsonObject.int(key: String, fallback: Int): Int =
    (this[key] as? JsonPrimitive)?.intOrNull ?: fallback

/** Projects one `series[]` element into a [WizardMatch]. */
fun JsonObject.toWizardMatch(index: Int): WizardMatch = WizardMatch(
    index = index,
    id = str("id"),
    provider = str("provider") ?: "",
    scanlator = str("scanlator") ?: "",
    lang = str("lang") ?: "",
    thumbnailUrl = str("thumbnailUrl"),
    title = str("title") ?: "",
    chapterCount = whole("chapterCount"),
    url = str("url"),
    useCover = bool("useCover"),
    isStorage = bool("isStorage"),
    useTitle = bool("useTitle"),
    lastChapter = num("lastChapter"),
    preferred = bool("preferred"),
)

/** Projects a raw ImportSeriesEntry into the read-only view model. */
fun JsonObject.toWizardImport(): WizardImport = WizardImport(
    raw = this,
    path = str("path") ?: "",
    title = str("title") ?: "",
    status = int("status", WizardImportStatus.IMPORT),
    action = int("action", WizardAction.ADD),
    continueAfterChapter = num("continueAfterChapter"),
    type = str("type") ?: "",
    isTitleOnly = bool("isTitleOnly"),
    series = (this["series"] as? JsonArray)
        ?.mapIndexedNotNull { i, element -> (element as? JsonObject)?.toWizardMatch(i) }
        ?: emptyList(),
)

/** Copy of the entry with one top-level field replaced (mirrors `{...item, [field]: value}`). */
fun WizardImport.withField(field: String, value: JsonElement): WizardImport =
    JsonObject(raw.toMutableMap().apply { put(field, value) }).toWizardImport()

/** Copy of the entry with one `series[index]` field replaced. */
fun WizardImport.withSeriesField(index: Int, field: String, value: JsonElement): WizardImport {
    val list = (raw["series"] as? JsonArray)?.toMutableList() ?: return this
    val target = list.getOrNull(index) as? JsonObject ?: return this
    list[index] = JsonObject(target.toMutableMap().apply { put(field, value) })
    return JsonObject(raw.toMutableMap().apply { put("series", JsonArray(list)) }).toWizardImport()
}

/** The whole entry replaced by the server's answer (augment result). */
fun JsonObject.replacingWizardImport(): WizardImport = toWizardImport()

/** `{ "preferences": "<json string>" }` — the only field PUT /api/auth/me needs here. */
fun preferencesUpdateBody(serialized: String): JsonObject = buildJsonObject {
    put("preferences", JsonPrimitive(serialized))
}

// Re-exported so callers don't need the kotlinx json imports for simple puts.
fun wizardJsonOf(value: String): JsonElement = JsonPrimitive(value)
fun wizardJsonOf(value: Int): JsonElement = JsonPrimitive(value)
fun wizardJsonOf(value: Double): JsonElement = JsonPrimitive(value)
fun wizardJsonOf(value: Boolean): JsonElement = JsonPrimitive(value)
