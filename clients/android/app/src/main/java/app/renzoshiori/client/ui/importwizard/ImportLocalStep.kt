package app.renzoshiori.client.ui.importwizard

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.outlined.Circle
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.data.model.WizardJobStatus
import app.renzoshiori.client.data.model.WizardProgressStateDto
import app.renzoshiori.client.data.model.WizardProgressStatus
import app.renzoshiori.client.data.model.WizardSetupStatusDto
import app.renzoshiori.client.data.network.SetupWizardApi
import kotlinx.coroutines.delay

/** The three chained jobs, in order — JobType 0/1/2 (types.ts JobType). */
private val ACTION_TITLES = listOf(
    "Scan Local Files",
    "Install Additional Sources",
    "Search Series",
)

private val ACTION_LABELS = listOf("scan", "install", "search")

/** How often the wizard re-reads GET /api/setup/status (the web gets this live over SignalR). */
private const val POLL_INTERVAL_MS = 1500L

private val DoneBorder = Color(0x4D1FA97A)   // .iw-scan-card.is-done — hsla(160 70% 40% / .3)
private val FailBorder = Color(0x66EF4444)   // .iw-scan-card.is-failed — hsla(0 84% 60% / .4)
private val DoneIconBg = Color(0x331FA97A)   // .iw-scan-icon.is-done — hsla(160 70% 40% / .2)

private fun WizardSetupStatusDto.valueFor(index: Int): String? = when (index) {
    0 -> scanLocalFiles
    1 -> installAdditionalExtensions
    else -> searchProviders
}

private fun WizardSetupStatusDto.progressFor(index: Int): WizardProgressStateDto? = when (index) {
    0 -> scanLocalFilesProgress
    1 -> installAdditionalExtensionsProgress
    else -> searchProvidersProgress
}

/**
 * Terminal snapshots (Completed/Failed) are skipped — they can be leftovers
 * from a previous run and would pin a fresh run's percentage at 100.
 */
private fun WizardProgressStateDto?.isLive(): Boolean =
    this != null &&
        (progressStatus == WizardProgressStatus.STARTED || progressStatus == WizardProgressStatus.IN_PROGRESS)

/**
 * Step 01 — setup-wizard/steps/import-local-step.tsx in `forceRescan` mode
 * (which is how the Import Series wizard always uses it).
 *
 * Scan → install sources → search providers, chained automatically. A pipeline
 * that is genuinely in flight (another session, or this one before the wizard
 * was reopened) is attached to rather than restarted; stale "Completed"
 * statuses from an earlier import run are ignored and a fresh scan is started.
 */
@Composable
fun ImportLocalStep(
    api: SetupWizardApi?,
    titleOnly: Boolean,
    setError: (String?) -> Unit,
    setIsLoading: (Boolean) -> Unit,
    setCanProgress: (Boolean) -> Unit,
) {
    var currentActionIndex by remember { mutableIntStateOf(-1) }
    var allActionsCompleted by remember { mutableStateOf(false) }
    var failedIndex by remember { mutableIntStateOf(-1) }
    val serverCompleted = remember { mutableStateListOf<Int>() }
    val polled = remember { mutableStateMapOf<Int, WizardProgressStateDto>() }

    LaunchedEffect(api) {
        val service = api ?: run {
            setError("Could not reach the server to check import status. Close the wizard and try again.")
            return@LaunchedEffect
        }
        setError(null)

        // The status call can fail transiently (server redeploying, network blip).
        // Retry a few times before giving up — blindly starting a scan on failure
        // used to strand the wizard on "Failed to start scan process".
        suspend fun fetchStatusWithRetry(retries: Int = 4, delayMs: Long = 3000): WizardSetupStatusDto? {
            var attempt = 0
            while (true) {
                val result = runCatching { service.setupStatus() }
                result.getOrNull()?.let { return it }
                if (attempt >= retries) return null
                attempt += 1
                delay(delayMs)
            }
        }

        /** Refreshes the polled percentages/messages from a status snapshot. */
        fun absorbProgress(status: WizardSetupStatusDto) {
            for (index in 0..2) {
                val progress = status.progressFor(index)
                if (progress.isLive()) polled[index] = progress!! else polled.remove(index)
            }
        }

        /** POST the enqueue endpoint for one action; false when it truly failed to start. */
        suspend fun startAction(index: Int): Boolean {
            currentActionIndex = index
            val started = runCatching {
                when (index) {
                    0 -> service.scanLocalFiles(titleOnly)
                    1 -> service.installExtensions()
                    else -> service.searchProviders()
                }
            }
            if (started.isSuccess) return true

            // Before surfacing a failure, re-check the server: the start call can
            // fail because a pipeline is already live from another session, or
            // because the server was briefly restarting.
            val status = runCatching { service.setupStatus() }.getOrNull()
            if (status != null && WizardJobStatus.isInFlight(status.valueFor(index))) return true

            setError("Failed to start ${ACTION_LABELS[index]} process")
            currentActionIndex = -1
            return false
        }

        /** Polls until the given job reports Completed (true) or Failed (false). */
        suspend fun awaitAction(index: Int): Boolean {
            while (true) {
                delay(POLL_INTERVAL_MS)
                val status = runCatching { service.setupStatus() }.getOrNull() ?: continue
                absorbProgress(status)
                when (status.valueFor(index)) {
                    WizardJobStatus.COMPLETED -> return true
                    WizardJobStatus.FAILED -> return false
                    else -> Unit
                }
            }
        }

        val initial = fetchStatusWithRetry()
        if (initial == null) {
            setError("Could not reach the server to check import status. Close the wizard and try again.")
            return@LaunchedEffect
        }
        absorbProgress(initial)

        // A live pipeline (from this or any other session) takes priority: sync
        // to it rather than starting anything new.
        val inFlight = (0..2).firstOrNull { WizardJobStatus.isInFlight(initial.valueFor(it)) }
        var index: Int
        if (inFlight != null) {
            for (earlier in 0 until inFlight) {
                if (WizardJobStatus.isCompleted(initial.valueFor(earlier))) serverCompleted.add(earlier)
            }
            index = inFlight
            currentActionIndex = inFlight
        } else {
            // Import wizard: ignore stale "Completed" statuses left over from a
            // previous import run and kick off a fresh scan from the beginning.
            index = 0
            if (!startAction(0)) return@LaunchedEffect
        }

        while (true) {
            val ok = awaitAction(index)
            if (!ok) {
                failedIndex = index
                setError("Action failed: Job failed")
                currentActionIndex = -1
                return@LaunchedEffect
            }
            if (!serverCompleted.contains(index)) serverCompleted.add(index)
            polled.remove(index)
            if (index == 2) {
                allActionsCompleted = true
                currentActionIndex = -1
                break
            }
            index += 1
            if (!startAction(index)) return@LaunchedEffect
        }
    }

    LaunchedEffect(currentActionIndex, allActionsCompleted) {
        setIsLoading(currentActionIndex >= 0)
        setCanProgress(allActionsCompleted)
    }

    Column(modifier = Modifier.fillMaxWidth().verticalScroll(rememberScrollState())) {
        Text(
            "Scanning local files, installing sources, and searching for series matches. All actions " +
                "run automatically — this may take a few minutes depending on the number of series and sources.",
            style = MaterialTheme.typography.bodySmall,
            color = WizardColors.FgMuted,
        )

        Column(modifier = Modifier.fillMaxWidth().padding(top = 16.dp)) {
            ACTION_TITLES.forEachIndexed { index, title ->
                val isCompleted = serverCompleted.contains(index)
                val isActive = currentActionIndex == index && !isCompleted
                val isFailed = failedIndex == index
                val progressData = polled[index]
                ActionProgress(
                    title = title,
                    isActive = isActive,
                    isCompleted = isCompleted,
                    isFailed = isFailed,
                    progress = if (isCompleted) 100.0 else (progressData?.percentage ?: 0.0),
                    message = progressData?.message?.takeIf { it.isNotEmpty() },
                    errorMessage = progressData?.errorMessage,
                    modifier = Modifier.padding(bottom = if (index == ACTION_TITLES.lastIndex) 0.dp else 8.dp),
                )
            }

            if (allActionsCompleted) {
                WizardDoneBanner(
                    "Series process completed successfully",
                    modifier = Modifier.padding(top = 12.dp),
                )
            }
        }
    }
}

/** .iw-scan-card — icon, label + progress bar + status line, percentage. */
@Composable
private fun ActionProgress(
    title: String,
    isActive: Boolean,
    isCompleted: Boolean,
    isFailed: Boolean,
    progress: Double,
    message: String?,
    errorMessage: String?,
    modifier: Modifier = Modifier,
) {
    val borderColor = when {
        isFailed -> FailBorder
        isCompleted -> DoneBorder
        isActive -> WizardColors.Primary.copy(alpha = 0.45f)
        else -> WizardColors.Border
    }

    // One decimal while running: long jobs (e.g. searching hundreds of titles)
    // advance in sub-1% steps that integer rounding would hide entirely.
    val pctLabel = when {
        isCompleted -> "100%"
        isActive || isFailed -> String.format("%.1f%%", (Math.round(progress * 10) / 10.0))
        else -> "—"
    }
    val statusText = message ?: when {
        isCompleted -> "Complete"
        isActive -> "Running…"
        else -> "Queued"
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(WizardColors.Panel)
            .border(1.dp, borderColor, RoundedCornerShape(8.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier.size(22.dp).clip(CircleShape),
        ) {
            when {
                isFailed -> Icon(
                    Icons.Filled.ErrorOutline,
                    contentDescription = null,
                    tint = WizardColors.Destructive,
                    modifier = Modifier.size(14.dp),
                )
                isCompleted -> Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .size(22.dp)
                        .clip(CircleShape)
                        .background(DoneIconBg),
                ) {
                    Icon(
                        Icons.Filled.CheckCircle,
                        contentDescription = null,
                        tint = WizardColors.Green,
                        modifier = Modifier.size(12.dp),
                    )
                }
                isActive -> WizardSpinner(22)
                else -> Icon(
                    Icons.Outlined.Circle,
                    contentDescription = null,
                    tint = WizardColors.FgMuted.copy(alpha = 0.4f),
                    modifier = Modifier.size(14.dp),
                )
            }
        }

        Column(modifier = Modifier.padding(horizontal = 12.dp).weight(1f)) {
            Text(
                title,
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.SemiBold,
                color = WizardColors.Fg,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            WizardProgressBar(
                progress = (if (isCompleted) 100.0 else progress).toFloat() / 100f,
                modifier = Modifier.padding(top = 4.dp),
            )
            Text(
                statusText,
                style = wizardMono(9.5f, FontWeight.Normal, 0.1f, WizardColors.FgMuted),
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 4.dp),
            )
            if (!errorMessage.isNullOrEmpty()) {
                Text(
                    errorMessage,
                    style = MaterialTheme.typography.labelSmall,
                    color = WizardColors.Destructive,
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
        }

        Text(
            pctLabel,
            style = wizardMono(
                10f,
                FontWeight.Bold,
                0.0f,
                if (!isActive && !isCompleted && !isFailed) WizardColors.FgMuted else WizardColors.Fg,
            ),
            maxLines = 1,
        )
    }
}
