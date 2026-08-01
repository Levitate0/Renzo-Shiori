package app.renzoshiori.client.ui.importwizard

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.network.SetupWizardApi

/** One step of the wizard — RenzoFrontend comp/import-wizard/index.tsx WIZARD_STEPS. */
private data class WizardStepDef(
    val id: String,
    val label: String,
    val eyebrow: String,
    val title: String,
    val description: String,
)

private val WIZARD_STEPS = listOf(
    WizardStepDef(
        id = "import",
        label = "Import",
        eyebrow = "Step 01 · Scan",
        title = "Import Local Files",
        description = "Scan your archives and select series to import.",
    ),
    WizardStepDef(
        id = "confirm",
        label = "Review",
        eyebrow = "Step 02 · Review",
        title = "Confirm Imports",
        description = "Review and match each detected series before importing.",
    ),
    WizardStepDef(
        id = "schedule",
        label = "Schedule",
        eyebrow = "Step 03 · Configure",
        title = "Schedule Updates",
        description = "Configure automatic update schedules for your series.",
    ),
    WizardStepDef(
        id = "finish",
        label = "Finish",
        eyebrow = "Step 04 · Complete",
        title = "Finish",
        description = "Your series are being imported into the library.",
    ),
)

/**
 * Import Series wizard — the native transliteration of RenzoFrontend
 * comp/import-wizard (WizardShell + the four setup-wizard steps it reuses:
 * import-local, confirm-imports, schedule-updates, finish).
 *
 * The phone layout is the web's own ≤640px layout: a full-screen shell, the
 * compact "01 · IMPORT ›" stepper pill with its hairline instead of the desktop
 * four-segment bar, hero without the description line, and a two-row footer
 * (centred step meta above, Back | Continue side by side below).
 *
 * @param titleOnly scan the configured ImportFolder (e.g. a Suwayomi migration)
 *   instead of StorageFolder, registering bare titles for archive-less folders.
 */
@Composable
fun ImportWizardScreen(titleOnly: Boolean, onClose: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val api = remember(app.network.serverUrl) { app.network.currentServiceOf<SetupWizardApi>() }
    val scope = rememberCoroutineScope()

    var currentStep by remember { mutableIntStateOf(0) }
    var isLoading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var canProgress by remember { mutableStateOf(false) }
    var disableDownloads by remember { mutableStateOf(false) }

    val total = WIZARD_STEPS.size
    val step = WIZARD_STEPS[currentStep]
    val isLastStep = currentStep == total - 1
    val canPrevious = currentStep > 0

    BackHandler { onClose() }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(WizardColors.Shell)
            .statusBarsPadding(),
    ) {
        // ── Header: compact stepper pill + hairline + close ──────────────
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 16.dp, end = 12.dp, top = 10.dp),
        ) {
            MobileStepper(
                stepNumber = currentStep + 1,
                label = step.label,
                percent = ((currentStep + 1).toFloat() / total),
                modifier = Modifier.weight(1f),
            )
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .padding(start = 8.dp)
                    .size(36.dp)
                    .clip(CircleShape)
                    .border(1.dp, WizardColors.BorderStrong, CircleShape)
                    .clickable(onClick = onClose),
            ) {
                Icon(
                    Icons.Filled.Close,
                    contentDescription = "Close import wizard",
                    tint = WizardColors.FgMuted,
                    modifier = Modifier.size(16.dp),
                )
            }
        }

        // ── Hero band: eyebrow + title (the description is hidden ≤640px) ─
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 16.dp, end = 16.dp, top = 20.dp, bottom = 12.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .width(24.dp)
                        .height(1.dp)
                        .background(
                            Brush.horizontalGradient(
                                listOf(Color.Transparent, WizardColors.Primary.copy(alpha = 0.6f)),
                            ),
                        ),
                )
                Text(
                    step.eyebrow.uppercase(),
                    style = wizardMono(10.5f, FontWeight.SemiBold, 0.32f, WizardColors.Primary),
                    modifier = Modifier.padding(horizontal = 10.dp),
                )
                Box(
                    modifier = Modifier
                        .width(24.dp)
                        .height(1.dp)
                        .background(
                            Brush.horizontalGradient(
                                listOf(WizardColors.Primary.copy(alpha = 0.6f), Color.Transparent),
                            ),
                        ),
                )
            }
            Text(
                step.title,
                style = MaterialTheme.typography.headlineSmall,
                color = WizardColors.Fg,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 14.dp),
            )
        }

        // ── Step content ─────────────────────────────────────────────────
        Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
            Column(modifier = Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
                error?.let { message ->
                    Text(
                        message,
                        style = MaterialTheme.typography.labelMedium,
                        color = Color(0xFFF87171),
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(bottom = 16.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .background(WizardColors.Destructive.copy(alpha = 0.1f))
                            .border(1.dp, WizardColors.Border, RoundedCornerShape(8.dp))
                            .padding(8.dp),
                    )
                }

                when (currentStep) {
                    0 -> ImportLocalStep(
                        api = api,
                        titleOnly = titleOnly,
                        setError = { error = it },
                        setIsLoading = { isLoading = it },
                        setCanProgress = { canProgress = it },
                    )
                    1 -> ConfirmImportsStep(
                        api = api,
                        outerScope = scope,
                        setError = { error = it },
                        setIsLoading = { isLoading = it },
                        setCanProgress = { canProgress = it },
                    )
                    2 -> ScheduleUpdatesStep(
                        api = api,
                        setError = { error = it },
                        setIsLoading = { isLoading = it },
                        setCanProgress = { canProgress = it },
                        onDownloadOptionChange = { disableDownloads = it },
                    )
                    else -> FinishStep(
                        api = api,
                        disableDownloads = disableDownloads,
                        setError = { error = it },
                        setIsLoading = { isLoading = it },
                        setCanProgress = { canProgress = it },
                    )
                }
            }
        }

        // ── Footer ───────────────────────────────────────────────────────
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    Brush.verticalGradient(
                        listOf(Color(0x66101014), Color(0x99090909)),
                    ),
                )
                .padding(start = 16.dp, end = 16.dp, top = 12.dp, bottom = 20.dp)
                .navigationBarsPadding(),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(1.dp)
                    .background(
                        Brush.horizontalGradient(
                            listOf(
                                Color.Transparent,
                                WizardColors.Primary.copy(alpha = 0.35f),
                                Color.Transparent,
                            ),
                        ),
                    ),
            )
            Text(
                "STEP ${currentStep + 1} OF $total",
                style = wizardMono(10.5f, FontWeight.SemiBold, 0.22f, WizardColors.FgDim),
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
            )
            Row(
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
            ) {
                WizardBackButton(
                    label = if (canPrevious) "← Back" else "Cancel",
                    enabled = !isLoading,
                    onClick = { if (canPrevious) currentStep -= 1 else onClose() },
                    modifier = Modifier.weight(1f),
                )
                WizardPrimaryButton(
                    label = if (isLastStep) "Finish" else "Continue →",
                    enabled = canProgress && !isLoading,
                    onClick = {
                        if (isLastStep) onClose() else if (currentStep < total - 1) currentStep += 1
                    },
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

/** .iw-mobile-stepper — "01 · IMPORT ›" pill plus the 3px progress hairline. */
@Composable
private fun MobileStepper(
    stepNumber: Int,
    label: String,
    percent: Float,
    modifier: Modifier = Modifier,
) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = modifier) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .height(40.dp)
                .clip(RoundedCornerShape(8.dp))
                .background(
                    Brush.verticalGradient(
                        listOf(
                            WizardColors.Primary.copy(alpha = 0.18f),
                            WizardColors.Primary.copy(alpha = 0.06f),
                        ),
                    ),
                )
                .border(1.dp, WizardColors.Primary.copy(alpha = 0.4f), RoundedCornerShape(8.dp))
                .padding(horizontal = 12.dp),
        ) {
            Text(
                stepNumber.toString().padStart(2, '0'),
                style = wizardMono(10.5f, FontWeight.Bold, 0.2f, WizardColors.Primary),
            )
            Text(
                "·",
                style = wizardMono(10.5f, FontWeight.Bold, 0.2f, WizardColors.Fg),
                modifier = Modifier.padding(horizontal = 6.dp),
            )
            Text(
                label.uppercase(),
                style = wizardMono(10.5f, FontWeight.Bold, 0.2f, WizardColors.Fg),
            )
            Icon(
                Icons.AutoMirrored.Filled.KeyboardArrowRight,
                contentDescription = null,
                tint = WizardColors.FgMuted,
                modifier = Modifier.padding(start = 6.dp).size(10.dp),
            )
        }
        Spacer(Modifier.width(10.dp))
        WizardProgressBar(progress = percent, modifier = Modifier.weight(1f))
    }
}

/** .iw-back-btn */
@Composable
private fun WizardBackButton(
    label: String,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = modifier
            .heightIn(min = 48.dp)
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, WizardColors.BorderStrong, RoundedCornerShape(8.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .alpha(if (enabled) 1f else 0.45f)
            .padding(horizontal = 16.dp, vertical = 10.dp),
    ) {
        Text(
            label.uppercase(),
            style = wizardMono(11f, FontWeight.SemiBold, 0.16f, WizardColors.FgMuted),
            maxLines = 1,
        )
    }
}

/** .iw-primary-btn */
@Composable
private fun WizardPrimaryButton(
    label: String,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = modifier
            .heightIn(min = 48.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(
                Brush.verticalGradient(
                    listOf(Color(0xFFEA2B56), Color(0xFFC7143C)),
                ),
            )
            .border(1.dp, Color(0xCCEA2B56), RoundedCornerShape(12.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .alpha(if (enabled) 1f else 0.45f)
            .padding(horizontal = 18.dp, vertical = 11.dp),
    ) {
        Text(
            label.uppercase(),
            style = wizardMono(11f, FontWeight.Bold, 0.18f, Color.White),
            maxLines = 1,
        )
    }
}
