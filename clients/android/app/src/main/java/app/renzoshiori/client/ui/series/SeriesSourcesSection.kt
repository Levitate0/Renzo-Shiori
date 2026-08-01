package app.renzoshiori.client.ui.series

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Restore
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.data.network.ProviderExtendedDto
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage

/**
 * Transliteration of sources-section.tsx + provider-card.tsx. Same header
 * ("Sources" + count + the order explainer), same per-source anatomy (cover,
 * name + scanlator + language flag + status pill + Permanent chip, the
 * Ch./updated line, the Perm/Cover/Title/Status pill toggles, "After Ch.",
 * the green power button and the trash button, and the ▲▼ priority arrows),
 * and the same Apply / Revert to Default bar pinned under the list.
 */
@Composable
fun SeriesSourcesSection(
    state: SeriesDetailUiState,
    baseUrl: String,
    vm: SeriesDetailViewModel,
) {
    var infoOpen by remember { mutableStateOf(false) }
    var confirmDeleteProvider by remember { mutableStateOf<ProviderExtendedDto?>(null) }

    val orderIndex = state.providerOrder.withIndex().associate { (i, id) -> id to i }
    val ordered = state.providers.sortedBy { orderIndex[it.id] ?: 0 }

    Column(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        // ── Header ──
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(
                "Sources",
                style = MaterialTheme.typography.titleMedium,
                color = RenzoColors.Foreground,
            )
            Text(
                "${state.providers.size}",
                fontSize = 11.sp,
                fontWeight = FontWeight.Medium,
                color = Muted,
                modifier = Modifier
                    .clip(PillShape)
                    .background(ForegroundFaint10)
                    .padding(horizontal = 6.dp, vertical = 2.dp),
            )
            Icon(
                Icons.Filled.Info,
                contentDescription = "How source order works",
                tint = Muted.copy(alpha = 0.7f),
                modifier = Modifier
                    .size(20.dp)
                    .clip(PillShape)
                    .clickable { infoOpen = true }
                    .padding(2.dp),
            )
        }

        // ── Source rows ──
        Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            ordered.forEachIndexed { index, provider ->
                ProviderCard(
                    provider = provider,
                    baseUrl = baseUrl,
                    switches = state.providerSwitches[provider.id] ?: ProviderSwitchState(),
                    isDisabled = state.providerDisabled[provider.id] ?: provider.isDisabled,
                    fromChapter = state.providerFromChapters[provider.id] ?: "",
                    canEdit = state.canEdit,
                    canMoveUp = index > 0,
                    canMoveDown = index < ordered.size - 1,
                    vm = vm,
                    onRequestDelete = { confirmDeleteProvider = provider },
                )
            }
        }

        if (state.providers.isEmpty()) {
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(MaterialTheme.shapes.large)
                    .background(RenzoColors.Card.copy(alpha = 0.5f))
                    .border(1.dp, Border60, MaterialTheme.shapes.large)
                    .padding(32.dp),
            ) {
                Text(
                    "No sources configured yet.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = Muted,
                )
            }
        }

        // ── Apply bar (2+ sources, like the web's sticky bar) ──
        if (state.canEdit && state.providers.size > 1) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(MaterialTheme.shapes.medium)
                    .background(RenzoColors.Background.copy(alpha = 0.95f))
                    .border(1.dp, Border60, MaterialTheme.shapes.medium)
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    if (state.orderDirty) "Unsaved order — press Apply to save."
                    else "Order matches saved priority.",
                    fontSize = 12.sp,
                    color = Muted,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    androidx.compose.material3.OutlinedButton(
                        onClick = { vm.revertOrderToDefault() },
                        shape = MaterialTheme.shapes.small,
                        border = androidx.compose.foundation.BorderStroke(1.dp, Border60),
                        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 6.dp),
                    ) {
                        Icon(Icons.Filled.Restore, contentDescription = null, modifier = Modifier.size(14.dp))
                        Text(
                            "  Revert to Default",
                            style = MaterialTheme.typography.labelLarge,
                            color = RenzoColors.Foreground,
                        )
                    }
                    androidx.compose.material3.Button(
                        onClick = { vm.applyOrder() },
                        enabled = state.orderDirty && !state.applyingOrder,
                        shape = MaterialTheme.shapes.small,
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 6.dp),
                    ) {
                        Text("Apply", style = MaterialTheme.typography.labelLarge)
                    }
                }
            }
        }
    }

    if (infoOpen) {
        RenzoDialog(
            title = "Source order",
            onDismiss = { infoOpen = false },
            description = "Order sets source priority — the topmost source is preferred for " +
                "reading, previews, and downloads. Use the ▲▼ arrows to reorder, then press " +
                "Apply to save.",
            footer = { OutlineDialogButton(text = "Got it") { infoOpen = false } },
        )
    }

    confirmDeleteProvider?.let { provider ->
        RenzoDialog(
            title = "Remove this source?",
            onDismiss = { confirmDeleteProvider = null },
            description = (
                if (provider.scanlator.isNotEmpty())
                    "Remove ${provider.provider} (${provider.scanlator}) from this series"
                else "Remove ${provider.provider} from this series"
                ) + ". This won't delete downloaded files.",
            footer = {
                OutlineDialogButton(text = "Cancel") { confirmDeleteProvider = null }
                DestructiveDialogButton(text = "Remove Source") {
                    vm.deleteProvider(provider.id)
                    confirmDeleteProvider = null
                }
            },
        )
    }
}

/** provider-card.tsx — one source row. */
@Composable
private fun ProviderCard(
    provider: ProviderExtendedDto,
    baseUrl: String,
    switches: ProviderSwitchState,
    isDisabled: Boolean,
    fromChapter: String,
    canEdit: Boolean,
    canMoveUp: Boolean,
    canMoveDown: Boolean,
    vm: SeriesDetailViewModel,
    onRequestDelete: () -> Unit,
) {
    val isUnknown = provider.isUnknown
    val hasUnknownThumbnail = provider.thumbnailUrl?.lowercase()?.contains("unknown") == true
    val (statusText, statusColor) = providerStatusDisplay(provider.status)
    val relative = provider.lastChangeUTC?.let { formatRelative(it) }?.takeIf { it != "—" }

    var localFromChapter by remember(provider.id) { mutableStateOf(fromChapter) }
    LaunchedEffect(fromChapter) { localFromChapter = fromChapter }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(MaterialTheme.shapes.large)
            .background(if (isUnknown) Amber500.copy(alpha = 0.04f) else RenzoColors.Card)
            .border(
                1.dp,
                if (isUnknown) Amber500.copy(alpha = 0.4f) else Border60,
                MaterialTheme.shapes.large,
            )
            .padding(16.dp)
            // The web card is `opacity-60` while the source is disabled.
            .then(if (isDisabled) Modifier.alpha(0.6f) else Modifier),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {

            // ── Priority arrows (0 = highest) ──
            if (canEdit) {
                Column(
                    verticalArrangement = Arrangement.spacedBy(2.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Icon(
                        Icons.Filled.KeyboardArrowUp,
                        contentDescription = "Raise source priority",
                        tint = Muted,
                        modifier = Modifier
                            .size(24.dp)
                            .clip(MaterialTheme.shapes.extraSmall)
                            .clickable(enabled = canMoveUp) { vm.moveProvider(provider.id, up = true) }
                            .then(if (canMoveUp) Modifier else Modifier.alphaHalf()),
                    )
                    Icon(
                        Icons.Filled.KeyboardArrowDown,
                        contentDescription = "Lower source priority",
                        tint = Muted,
                        modifier = Modifier
                            .size(24.dp)
                            .clip(MaterialTheme.shapes.extraSmall)
                            .clickable(enabled = canMoveDown) { vm.moveProvider(provider.id, up = false) }
                            .then(if (canMoveDown) Modifier else Modifier.alphaHalf()),
                    )
                }
            }

            // ── Thumbnail ──
            Box(
                modifier = Modifier
                    .width(56.dp)
                    .height(84.dp)
                    .clip(MaterialTheme.shapes.extraSmall)
                    .background(RenzoColors.Muted)
                    .border(1.dp, Color.White.copy(alpha = 0.06f), MaterialTheme.shapes.extraSmall),
            ) {
                provider.thumbnailUrl?.takeIf { it.isNotEmpty() }?.let {
                    AsyncImage(
                        model = absoluteUrl(baseUrl, it),
                        contentDescription = provider.title,
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                }
            }

            // ── Centre column ──
            Column(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(
                    provider.title,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.Medium,
                    color = RenzoColors.Foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )

                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    Text(
                        provider.provider,
                        style = MaterialTheme.typography.bodySmall,
                        fontWeight = FontWeight.Medium,
                        color = RenzoColors.Foreground,
                    )
                    if (provider.scanlator.isNotEmpty() && provider.scanlator != provider.provider) {
                        Text(
                            provider.scanlator.uppercase(),
                            fontSize = 10.sp,
                            color = Muted,
                            modifier = Modifier
                                .clip(PillShape)
                                .background(ForegroundFaint10)
                                .padding(horizontal = 8.dp, vertical = 2.dp),
                        )
                    }
                    Text(flagForLanguage(provider.lang), fontSize = 12.sp)
                    MetaBadge(
                        label = statusText,
                        leading = { StatusDot(statusColor) },
                    )
                    if (provider.isUninstalled) {
                        MetaBadge(
                            label = "Uninstalled",
                            color = DestructiveText,
                            borderColor = DestructiveText.copy(alpha = 0.4f),
                            background = DestructiveText.copy(alpha = 0.1f),
                            leading = {
                                Icon(
                                    Icons.Filled.Warning,
                                    contentDescription = null,
                                    tint = DestructiveText,
                                    modifier = Modifier.size(12.dp),
                                )
                            },
                        )
                    }
                    if (switches.useStorage && !isUnknown) {
                        MetaBadge(
                            label = "Permanent",
                            color = MaterialTheme.colorScheme.primary,
                            borderColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.4f),
                            background = MaterialTheme.colorScheme.primary.copy(alpha = 0.1f),
                        )
                    }
                }

                Text(
                    buildString {
                        append("Ch. ${provider.chapterList}")
                        if (relative != null) append("  ·  updated $relative")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = Muted,
                )
            }
        }

        // ── Controls ──
        if (canEdit) {
            HairLine(Border40)

            if (!isUnknown) {
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    PillToggle(
                        label = "Perm",
                        checked = switches.useStorage,
                        enabled = !isDisabled,
                        onChange = { vm.setUseStorage(provider.id, it) },
                    )
                    PillToggle(
                        label = "Cover",
                        checked = switches.useCover,
                        enabled = !isDisabled && !hasUnknownThumbnail,
                        onChange = { vm.setUseCover(provider.id, it) },
                    )
                    PillToggle(
                        label = "Title",
                        checked = switches.useTitle,
                        enabled = !isDisabled,
                        onChange = { vm.setUseTitle(provider.id, it) },
                    )
                    PillToggle(
                        label = "Status",
                        checked = switches.useStatus,
                        enabled = !isDisabled,
                        onChange = { vm.setUseStatus(provider.id, it) },
                    )
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text("AFTER CH.", fontSize = 11.sp, color = Muted)
                    OutlinedTextField(
                        value = localFromChapter,
                        onValueChange = { localFromChapter = it },
                        enabled = !isDisabled,
                        singleLine = true,
                        placeholder = { Text("0", fontSize = 12.sp, color = Muted) },
                        textStyle = MaterialTheme.typography.bodySmall,
                        shape = MaterialTheme.shapes.extraSmall,
                        keyboardOptions = KeyboardOptions(
                            keyboardType = KeyboardType.Number,
                            imeAction = ImeAction.Done,
                        ),
                        keyboardActions = KeyboardActions(
                            onDone = { vm.commitFromChapter(provider.id, localFromChapter) },
                        ),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = MaterialTheme.colorScheme.primary,
                            unfocusedBorderColor = Border60,
                            focusedTextColor = RenzoColors.Foreground,
                            unfocusedTextColor = RenzoColors.Foreground,
                        ),
                        modifier = Modifier
                            .width(96.dp)
                            .onFocusChanged { focus ->
                                if (!focus.isFocused && localFromChapter != fromChapter) {
                                    vm.commitFromChapter(provider.id, localFromChapter)
                                }
                            },
                    )

                    Box(Modifier.weight(1f))

                    if (!provider.isUninstalled) {
                        val enabled = !isDisabled
                        SquareIconButton(
                            icon = Icons.Filled.PowerSettingsNew,
                            contentDescription = if (enabled) "Disable source" else "Enable source",
                            tint = if (enabled) Green500 else Muted,
                            borderColor = if (enabled) Green500.copy(alpha = 0.4f) else Border60,
                            background = if (enabled) Green500.copy(alpha = 0.06f) else RenzoColors.Muted.copy(alpha = 0.4f),
                            onClick = { vm.setProviderDisabled(provider.id, enabled) },
                        )
                    }

                    SquareIconButton(
                        icon = Icons.Filled.Delete,
                        contentDescription = "Delete source",
                        tint = DestructiveText.copy(alpha = 0.7f),
                        onClick = onRequestDelete,
                    )
                }
            } else {
                // Unknown source: the web only offers delete (matching lives in
                // the web's provider-match dialog, which needs a source search).
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Box(Modifier.weight(1f))
                    SquareIconButton(
                        icon = Icons.Filled.Delete,
                        contentDescription = "Delete source",
                        tint = DestructiveText.copy(alpha = 0.7f),
                        onClick = onRequestDelete,
                    )
                }
            }
        }
    }
}
