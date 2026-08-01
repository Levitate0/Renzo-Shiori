package app.renzoshiori.client.ui.importwizard

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.LibraryBooks
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Power
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.CircularProgressIndicator
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.data.model.WizardImportTotalsDto
import app.renzoshiori.client.data.network.SetupWizardApi
import app.renzoshiori.client.ui.theme.RenzoColors

/**
 * Step 03 — setup-wizard/steps/schedule-updates-step.tsx. The desktop
 * three-column Import Summary becomes a vertical stack (which is exactly what
 * the web itself does below md).
 */
@Composable
fun ScheduleUpdatesStep(
    api: SetupWizardApi?,
    setError: (String?) -> Unit,
    setIsLoading: (Boolean) -> Unit,
    setCanProgress: (Boolean) -> Unit,
    onDownloadOptionChange: (Boolean) -> Unit,
) {
    var totals by remember { mutableStateOf<WizardImportTotalsDto?>(null) }
    var loading by remember { mutableStateOf(true) }
    var downloadOption by remember { mutableStateOf("proceed") }

    LaunchedEffect(api) {
        val service = api ?: run {
            loading = false
            return@LaunchedEffect
        }
        loading = true
        setIsLoading(true)
        runCatching { service.importTotals() }
            .onSuccess {
                totals = it
                setError(null)
            }
            .onFailure { setError("Failed to load import totals: ${it.message}") }
        loading = false
        setIsLoading(false)
    }

    // Always allow progress — the user can choose either option.
    LaunchedEffect(Unit) { setCanProgress(true) }
    LaunchedEffect(downloadOption) { onDownloadOptionChange(downloadOption == "disable") }

    if (loading) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.Center,
            modifier = Modifier.fillMaxSize(),
        ) {
            CircularProgressIndicator(color = WizardColors.Fg, strokeWidth = 2.dp, modifier = Modifier.size(24.dp))
            Text(
                "Loading import totals...",
                style = MaterialTheme.typography.bodyMedium,
                color = WizardColors.Fg,
                modifier = Modifier.padding(start = 8.dp),
            )
        }
        return
    }

    val data = totals
    if (data == null) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("No import data available", color = WizardColors.FgMuted, style = MaterialTheme.typography.bodyMedium)
        }
        return
    }

    Column(modifier = Modifier.fillMaxWidth().verticalScroll(rememberScrollState())) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Text(
                "Schedule Updates",
                style = MaterialTheme.typography.titleLarge,
                color = WizardColors.Fg,
            )
            Text(
                "Review the incoming schedule, and choose between hell let loose now, or do in steps.",
                style = MaterialTheme.typography.bodySmall,
                color = WizardColors.FgMuted,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 8.dp),
            )
        }

        // ── Import Summary ───────────────────────────────────────────────
        WizardCard(modifier = Modifier.padding(top = 24.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.CalendarMonth,
                    contentDescription = null,
                    tint = WizardColors.Fg,
                    modifier = Modifier.size(20.dp),
                )
                Text(
                    "Import Summary",
                    style = MaterialTheme.typography.titleMedium,
                    color = WizardColors.Fg,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }
            Column(modifier = Modifier.fillMaxWidth().padding(top = 16.dp)) {
                StatTile(
                    icon = Icons.AutoMirrored.Filled.LibraryBooks,
                    iconTint = RenzoColors.Primary,
                    background = RenzoColors.Primary.copy(alpha = 0.1f),
                    value = data.totalSeries.toString(),
                    label = "Series",
                )
                StatTile(
                    icon = Icons.Filled.Power,
                    iconTint = WizardColors.Fg,
                    background = RenzoColors.Secondary.copy(alpha = 0.1f),
                    value = data.totalProviders.toString(),
                    label = "Sources",
                    modifier = Modifier.padding(top = 8.dp),
                )
                StatTile(
                    icon = Icons.Filled.Download,
                    iconTint = Color(0xFF60A5FA),
                    background = RenzoColors.Primary.copy(alpha = 0.1f),
                    value = data.totalDownloads.toString(),
                    label = "Scheduled Downloads",
                    modifier = Modifier.padding(top = 8.dp),
                )
            }
        }

        // ── Download Schedule Options ────────────────────────────────────
        WizardCard(modifier = Modifier.padding(top = 24.dp)) {
            Text(
                "Download Schedule Options",
                style = MaterialTheme.typography.titleMedium,
                color = WizardColors.Fg,
            )
            Column(modifier = Modifier.fillMaxWidth().padding(top = 16.dp)) {
                RadioCard(
                    selected = downloadOption == "proceed",
                    title = "Proceed with scheduled downloads",
                    body = "All ${data.totalDownloads} pending downloads will be automatically scheduled " +
                        "and start downloading according to your configured settings.",
                    onClick = { downloadOption = "proceed" },
                )
                RadioCard(
                    selected = downloadOption == "disable",
                    title = "Start with downloads disabled",
                    body = "All series sources will be imported but disabled. You can manually enable them " +
                        "one by one from the series management page when you're ready.",
                    onClick = { downloadOption = "disable" },
                    modifier = Modifier.padding(top = 16.dp),
                )
            }
        }

        if (downloadOption == "disable") {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 24.dp, bottom = 8.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(Color(0x1FEAB308))
                    .border(1.dp, Color(0x66A16207), RoundedCornerShape(8.dp))
                    .padding(16.dp),
            ) {
                Icon(
                    Icons.Filled.Warning,
                    contentDescription = null,
                    tint = Color(0xFFFACC15),
                    modifier = Modifier.size(20.dp),
                )
                Column(modifier = Modifier.padding(start = 8.dp)) {
                    Text(
                        "Note: Downloads will be disabled",
                        style = MaterialTheme.typography.bodySmall,
                        fontWeight = FontWeight.Medium,
                        color = Color(0xFFFDE68A),
                    )
                    Text(
                        "You'll need to manually enable sources for each series in the series management " +
                            "page to start downloading new chapters.",
                        style = MaterialTheme.typography.labelSmall,
                        color = Color(0xFFFCD34D),
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }
        }
    }
}

/** shadcn <Card> — bg-card, 1px border, rounded-xl. */
@Composable
private fun WizardCard(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(RenzoColors.Card)
            .border(1.dp, WizardColors.Border, RoundedCornerShape(12.dp))
            .padding(16.dp),
    ) {
        content()
    }
}

@Composable
private fun StatTile(
    icon: ImageVector,
    iconTint: Color,
    background: Color,
    value: String,
    label: String,
    modifier: Modifier = Modifier,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(background)
            .padding(16.dp),
    ) {
        Icon(icon, contentDescription = null, tint = iconTint, modifier = Modifier.size(32.dp))
        Column(modifier = Modifier.padding(start = 12.dp)) {
            Text(
                value,
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
                color = WizardColors.Fg,
            )
            Text(
                label,
                style = MaterialTheme.typography.bodySmall,
                color = WizardColors.FgMuted,
            )
        }
    }
}

@Composable
private fun RadioCard(
    selected: Boolean,
    title: String,
    body: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, WizardColors.Border, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(16.dp),
    ) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .padding(top = 2.dp)
                .size(16.dp)
                .clip(CircleShape)
                .border(
                    if (selected) 1.5.dp else 1.dp,
                    if (selected) RenzoColors.Primary else WizardColors.BorderStrong,
                    CircleShape,
                ),
        ) {
            if (selected) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .clip(CircleShape)
                        .background(RenzoColors.Primary),
                )
            }
        }
        Column(modifier = Modifier.padding(start = 12.dp).weight(1f)) {
            Text(
                title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.Medium,
                color = WizardColors.Fg,
            )
            Text(
                body,
                style = MaterialTheme.typography.bodySmall,
                color = WizardColors.FgMuted,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
    }
}
