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
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
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
import app.renzoshiori.client.data.model.WizardImportStatus
import app.renzoshiori.client.data.model.toWizardImport
import app.renzoshiori.client.data.network.SetupWizardApi
import kotlinx.coroutines.delay

private const val IMPORT_POLL_INTERVAL_MS = 1500L

private val DoneCardBorder = Color(0x4D1FA97A)
private val FailCardBorder = Color(0x66EF4444)
private val DoneIconTint = Color(0xFF4ADE80)

/**
 * Step 04 — setup-wizard/steps/finish-step.tsx.
 *
 * Asks the backend first: an import already running is reattached to (never
 * restarted), one that already finished shows the completed state, and only a
 * genuinely idle server gets a fresh POST /api/setup/import.
 *
 * The web's live percentage comes from the SignalR hub; the REST surface only
 * reports this job's state (Running/Completed/Failed), so the bar runs
 * indeterminate until the job reports Completed.
 */
@Composable
fun FinishStep(
    api: SetupWizardApi?,
    disableDownloads: Boolean,
    setError: (String?) -> Unit,
    setIsLoading: (Boolean) -> Unit,
    setCanProgress: (Boolean) -> Unit,
) {
    var importCompleted by remember { mutableStateOf(false) }
    var isFailed by remember { mutableStateOf(false) }
    var hasTriggered by remember { mutableStateOf(false) }

    LaunchedEffect(api) {
        val service = api ?: run {
            setError("Failed to start import process")
            return@LaunchedEffect
        }

        // On mount, ask the backend whether an import is already running/queued/
        // completed. This survives reopening the wizard: reconnect to the running
        // job instead of restarting it from scratch.
        val status = runCatching { service.importStatus() }.getOrNull()
        var monitoring = false
        when {
            status?.isActive == true -> {
                hasTriggered = true
                monitoring = true
            }
            status?.hasCompleted == true -> {
                hasTriggered = true
                importCompleted = true
                return@LaunchedEffect
            }
        }

        if (!monitoring) {
            setError(null)
            hasTriggered = true

            // Determine what to import.
            val imports = runCatching { service.imports() }.getOrNull()
                ?.map { it.toWizardImport() }
            val toProcess = imports?.filter {
                it.status == WizardImportStatus.IMPORT || it.status == WizardImportStatus.DO_NOT_CHANGE
            }

            if (imports != null && imports.isNotEmpty() && toProcess!!.isEmpty()) {
                importCompleted = true
                return@LaunchedEffect
            }

            val started = runCatching { service.importSeries(disableDownloads) }
            if (started.isFailure) {
                setError("Failed to start import process")
                hasTriggered = false
                return@LaunchedEffect
            }
        }

        // Monitor until the job finishes.
        while (true) {
            delay(IMPORT_POLL_INTERVAL_MS)
            val current = runCatching { service.importStatus() }.getOrNull() ?: continue
            if (current.hasCompleted) {
                importCompleted = true
                return@LaunchedEffect
            }
            if (current.hasFailed) {
                isFailed = true
                setError("Import failed: Job failed")
                return@LaunchedEffect
            }
        }
    }

    val isDone = importCompleted
    val isActive = hasTriggered && !isDone && !isFailed

    LaunchedEffect(isDone, isActive) {
        setIsLoading(isActive)
        setCanProgress(isDone)
    }

    val statusText = when {
        isFailed -> "Import process failed"
        isDone -> "Import process completed successfully"
        isActive -> "Importing series…"
        else -> "Preparing to import series…"
    }

    Column(modifier = Modifier.fillMaxWidth().verticalScroll(rememberScrollState())) {
        Text(
            "Final step: importing your selected series into the library. Please wait while the " +
                "process completes.",
            style = MaterialTheme.typography.bodySmall,
            color = WizardColors.FgMuted,
        )

        Column(modifier = Modifier.fillMaxWidth().padding(top = 16.dp)) {
            // ── Hero progress card ───────────────────────────────────────
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(10.dp))
                    .background(WizardColors.Panel)
                    .border(
                        1.dp,
                        when {
                            isFailed -> FailCardBorder
                            isDone -> DoneCardBorder
                            isActive -> WizardColors.Primary.copy(alpha = 0.4f)
                            else -> WizardColors.Border
                        },
                        RoundedCornerShape(10.dp),
                    )
                    .padding(16.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier.size(26.dp).clip(CircleShape),
                    ) {
                        when {
                            isFailed -> Icon(
                                Icons.Filled.ErrorOutline,
                                contentDescription = null,
                                tint = WizardColors.Destructive,
                                modifier = Modifier.size(20.dp),
                            )
                            isDone -> Icon(
                                Icons.Filled.CheckCircle,
                                contentDescription = null,
                                tint = DoneIconTint,
                                modifier = Modifier.size(20.dp),
                            )
                            isActive -> WizardSpinner(26)
                            else -> Icon(
                                Icons.Filled.Flag,
                                contentDescription = null,
                                tint = WizardColors.FgMuted,
                                modifier = Modifier.size(20.dp),
                            )
                        }
                    }
                    Column(modifier = Modifier.padding(horizontal = 12.dp).weight(1f)) {
                        Text(
                            "Series Import",
                            style = MaterialTheme.typography.titleMedium,
                            color = WizardColors.Fg,
                        )
                        Text(
                            statusText,
                            style = wizardMono(10.5f, FontWeight.Normal, 0.06f, WizardColors.FgMuted),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(top = 2.dp),
                        )
                    }
                    Text(
                        if (isDone) "100%" else "—",
                        style = wizardMono(18f, FontWeight.Bold, 0.0f, WizardColors.Primary),
                    )
                }

                if (isDone) {
                    WizardProgressBar(progress = 1f, height = 5, modifier = Modifier.padding(top = 12.dp))
                } else if (isActive) {
                    WizardIndeterminateBar(modifier = Modifier.padding(top = 12.dp))
                } else {
                    WizardProgressBar(progress = 0f, height = 5, modifier = Modifier.padding(top = 12.dp))
                }
            }

            if (isDone) {
                WizardDoneBanner(
                    "Import process completed successfully",
                    modifier = Modifier.padding(top = 12.dp),
                )
            }

            if (isFailed) {
                WizardFailBanner(
                    title = "Import Failed",
                    body = "The import process encountered an error. You can try again or skip this " +
                        "step and manually import series later.",
                    modifier = Modifier.padding(top = 12.dp),
                )
            }
        }
    }
}
