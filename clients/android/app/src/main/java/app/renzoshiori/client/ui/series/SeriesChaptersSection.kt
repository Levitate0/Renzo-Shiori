package app.renzoshiori.client.ui.series

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DoneAll
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PlaylistAddCheck
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.outlined.Circle
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.ui.library.formatChapter
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable

/**
 * The chapters section header + toolbar from chapters-section.tsx: the
 * "N chapters" title with its "N downloaded · N missing" summary, the filter
 * field, the action chips (Download all / Delete downloads / Save series
 * offline / Mark all read / Select / Missing only), and the multi-select
 * toolbar. Rendered above the rows, which the screen's LazyColumn owns so the
 * list scrolls natively instead of nesting a second scroller.
 */
@Composable
fun ChaptersSectionHeader(state: SeriesDetailUiState, vm: SeriesDetailViewModel) {
    Column(Modifier.fillMaxWidth()) {

        // ── Header ──
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
        ) {
            Text(
                if (state.total > 0) "${state.total} chapter${if (state.total == 1) "" else "s"}"
                else "Chapters",
                style = MaterialTheme.typography.titleMedium,
                color = RenzoColors.Foreground,
                modifier = Modifier.weight(1f),
            )
            if (state.total > 0) {
                Row {
                    Text("${state.downloadedCount} downloaded", fontSize = 12.sp, color = Muted)
                    if (state.missingCount > 0) {
                        Text(" · ", fontSize = 12.sp, color = Muted)
                        Text(
                            "${state.missingCount} missing",
                            fontSize = 12.sp,
                            fontWeight = FontWeight.Medium,
                            color = Amber500,
                        )
                    }
                }
            }
        }
        HairLine()

        // ── Filter + action chips ──
        val focusManager = LocalFocusManager.current
        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedTextField(
                value = state.query,
                onValueChange = { vm.setQuery(it) },
                placeholder = { Text("Filter by number or title…", style = MaterialTheme.typography.bodySmall) },
                leadingIcon = {
                    Icon(
                        Icons.Filled.Search,
                        contentDescription = null,
                        tint = Muted,
                        modifier = Modifier.size(16.dp),
                    )
                },
                singleLine = true,
                // The leanback IME needs an explicit action to close on; without
                // one the remote's keyboard has no way to commit and dismiss.
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { focusManager.clearFocus() }),
                textStyle = MaterialTheme.typography.bodyMedium,
                shape = MaterialTheme.shapes.small,
                colors = OutlinedTextFieldDefaults.colors(
                    focusedBorderColor = MaterialTheme.colorScheme.primary,
                    unfocusedBorderColor = Border60,
                    focusedTextColor = RenzoColors.Foreground,
                    unfocusedTextColor = RenzoColors.Foreground,
                ),
                modifier = Modifier.fillMaxWidth(),
            )

            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (state.canManageDownloads && state.missingCount > 0) {
                    RenzoChip(
                        label = "Download all",
                        icon = Icons.Filled.Download,
                        count = state.missingCount,
                        accent = MaterialTheme.colorScheme.primary,
                        enabled = !state.downloadAllPending,
                        onClick = { vm.downloadAll() },
                    )
                }
                if (state.canManageDownloads && state.downloadedCount > 0) {
                    RenzoChip(
                        label = "Delete downloads",
                        icon = Icons.Filled.Delete,
                        count = state.downloadedCount,
                        accent = DestructiveText,
                        enabled = !state.deleteDownloadsPending,
                        onClick = { vm.askDeleteDownloads(DeleteDownloadsScope.ALL) },
                    )
                }
                if (state.allDownloadedNumbers.isNotEmpty()) {
                    RenzoChip(
                        label = "Save series offline",
                        icon = Icons.Filled.CloudDownload,
                        count = state.allDownloadedNumbers.size,
                        accent = Emerald400,
                        onClick = { vm.saveOffline(state.chapters.filter { it.downloaded }) },
                    )
                }
                if (state.readerEnabled && state.total > 0) {
                    RenzoChip(
                        label = "Mark all read",
                        icon = Icons.Filled.DoneAll,
                        enabled = !state.markingAll,
                        onClick = { vm.markAllRead() },
                    )
                }
                if (state.readerEnabled && state.total > 0) {
                    RenzoChip(
                        label = "Select",
                        icon = Icons.Filled.PlaylistAddCheck,
                        active = state.selecting,
                        onClick = { vm.toggleSelecting() },
                    )
                }
                RenzoChip(
                    label = "Missing only",
                    count = state.missingCount.takeIf { it > 0 },
                    accent = if (state.missingOnly) Amber500 else null,
                    active = state.missingOnly,
                    onClick = { vm.toggleMissingOnly() },
                )
            }
        }

        // ── Multi-select toolbar ──
        if (state.selecting) {
            HairLine()
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(RenzoColors.Foreground.copy(alpha = 0.02f))
                    .padding(horizontal = 16.dp, vertical = 10.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(
                    "${state.selected.size} selected",
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Medium,
                    color = Muted,
                )
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    RenzoChip(
                        label = if (state.allVisibleSelected) "Deselect all" else "Select all",
                        onClick = { vm.selectAllOrNone() },
                    )
                    RenzoChip(
                        label = "Select in-between",
                        enabled = state.selected.isNotEmpty(),
                        onClick = { vm.selectInBetween() },
                    )
                    RenzoChip(
                        label = "Swap",
                        icon = Icons.Filled.SwapHoriz,
                        onClick = { vm.invertSelection() },
                    )
                    if (state.readerEnabled) {
                        RenzoChip(
                            label = "Mark read",
                            icon = Icons.Filled.DoneAll,
                            enabled = state.selected.isNotEmpty() && !state.bulkPending,
                            onClick = { vm.bulkMark(true) },
                        )
                        RenzoChip(
                            label = "Mark unread",
                            icon = Icons.Outlined.Circle,
                            enabled = state.selected.isNotEmpty() && !state.bulkPending,
                            onClick = { vm.bulkMark(false) },
                        )
                    }
                    if (state.canManageDownloads) {
                        RenzoChip(
                            label = "Download",
                            icon = Icons.Filled.Download,
                            accent = MaterialTheme.colorScheme.primary,
                            enabled = state.selected.isNotEmpty() && !state.bulkPending,
                            onClick = { vm.bulkDownload() },
                        )
                    }
                    RenzoChip(
                        label = "Save offline",
                        icon = Icons.Filled.CloudDownload,
                        count = state.selectedDownloadedNumbers.size.takeIf { it > 0 },
                        accent = Emerald400,
                        enabled = state.selectedDownloadedNumbers.isNotEmpty(),
                        onClick = {
                            val wanted = state.selectedDownloadedNumbers.toSet()
                            vm.saveOffline(state.chapters.filter { it.number in wanted })
                        },
                    )
                    if (state.canManageDownloads) {
                        RenzoChip(
                            label = "Delete",
                            icon = Icons.Filled.Delete,
                            count = state.selectedDownloadedNumbers.size.takeIf { it > 0 },
                            accent = DestructiveText,
                            enabled = state.selectedDownloadedNumbers.isNotEmpty() && !state.bulkPending,
                            onClick = { vm.askDeleteDownloads(DeleteDownloadsScope.SELECTED) },
                        )
                    }
                    RenzoChip(
                        label = "Done",
                        icon = Icons.Filled.Close,
                        onClick = { vm.exitSelection() },
                    )
                }
            }
        }
        HairLine()
    }
}

/** The web's empty/loading/error placeholders for the chapter list. */
@Composable
fun ChaptersEmptyState(state: SeriesDetailUiState) {
    val message = when {
        state.chaptersError -> "Couldn't load chapters. Please try again."
        state.total == 0 -> "No chapters tracked for this series yet."
        state.missingOnly -> "No missing chapters — everything is downloaded."
        else -> "No chapters match your filter."
    }
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp)
            .clip(MaterialTheme.shapes.large)
            .background(RenzoColors.Card.copy(alpha = 0.5f))
            .border(1.dp, Border60, MaterialTheme.shapes.large)
            .padding(32.dp),
    ) {
        Text(message, style = MaterialTheme.typography.bodyMedium, color = Muted)
    }
}

/**
 * chapter-row.tsx — the bordered, card-tinted row: selection box (in
 * multi-select), status icon (downloaded ✓ emerald / locked 🔒 violet /
 * missing ⚠ amber), number + name + bookmark + progress chip, the
 * "from <source>" / "Locked · purchase on source" / "Missing" subtitle with
 * its upload date, then the save-offline, read-toggle and (re-)download
 * split-button actions.
 */
@Composable
fun ChapterRow(
    chapter: ChapterRowUi,
    state: SeriesDetailUiState,
    isOffline: Boolean,
    vm: SeriesDetailViewModel,
    onOpenChapter: (Double) -> Unit,
) {
    var sourceMenuOpen by remember { mutableStateOf(false) }
    // One focus state per target in the row: the row itself, the split
    // re-download button and its source dropdown are separately reachable.
    val rowFocus = rememberFocusState()
    val redownloadFocus = rememberFocusState()
    val sourceMenuFocus = rememberFocusState()
    val selected = chapter.number in state.selected
    val selecting = state.selecting
    val isPending = chapter.number in state.pending
    val readPending = chapter.number in state.readPending
    val label = if (chapter.downloaded) "Re-download" else "Download"

    val disabledReason = when {
        state.pausedDownloads -> "Unpause the series to re-download"
        chapter.availableProviders.isEmpty() -> "No source available to download this chapter"
        else -> null
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp)
            .clip(MaterialTheme.shapes.small)
            .background(
                if (selected) MaterialTheme.colorScheme.primary.copy(alpha = 0.10f)
                else RenzoColors.Card.copy(alpha = 0.5f),
            )
            .border(
                1.dp,
                if (selected) MaterialTheme.colorScheme.primary.copy(alpha = 0.5f) else Border40,
                MaterialTheme.shapes.small,
            )
            .focusRing(rowFocus.focused, 8.dp)
            .tvClickable(onFocused = rowFocus::set) {
                if (selecting) vm.toggleSelected(chapter.number) else onOpenChapter(chapter.number)
            }
            .padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        if (selecting) {
            Icon(
                if (selected) Icons.Filled.CheckBox else Icons.Filled.CheckBoxOutlineBlank,
                contentDescription = null,
                tint = if (selected) MaterialTheme.colorScheme.primary else Muted.copy(alpha = 0.5f),
                modifier = Modifier.size(16.dp),
            )
        }

        // Status icon
        when {
            chapter.downloaded -> Icon(
                Icons.Filled.CheckCircle, contentDescription = "Downloaded",
                tint = Emerald500, modifier = Modifier.size(16.dp),
            )
            chapter.locked -> Icon(
                Icons.Filled.Lock, contentDescription = "Locked",
                tint = Violet400, modifier = Modifier.size(16.dp),
            )
            else -> Icon(
                Icons.Filled.Warning, contentDescription = "Missing",
                tint = Amber500, modifier = Modifier.size(16.dp),
            )
        }

        Column(
            modifier = Modifier
                .weight(1f)
                .then(if (chapter.isCompleted) Modifier.alpha(0.5f) else Modifier),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "Ch. ${formatChapter(chapter.number)}",
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.Medium,
                    color = RenzoColors.Foreground,
                )
                if (chapter.name.isNotEmpty()) {
                    Text(
                        chapter.name,
                        style = MaterialTheme.typography.bodyMedium,
                        color = Muted,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 8.dp).weight(1f, fill = false),
                    )
                }
                if (chapter.bookmarked) {
                    Icon(
                        Icons.Filled.Bookmark, contentDescription = "Bookmarked",
                        tint = Pink500, modifier = Modifier.padding(start = 6.dp).size(12.dp),
                    )
                }
                if (!chapter.isCompleted && chapter.progress > 0f) {
                    Text(
                        "${(chapter.progress * 100).toInt()}%",
                        fontSize = 10.sp,
                        fontWeight = FontWeight.Medium,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier
                            .padding(start = 6.dp)
                            .clip(RoundedCornerShape(4.dp))
                            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.15f))
                            .padding(horizontal = 4.dp, vertical = 1.dp),
                    )
                }
            }
            Row(modifier = Modifier.padding(top = 2.dp)) {
                when {
                    chapter.downloaded -> Text(
                        buildAnnotatedString {
                            append("from ")
                            withStyle(SpanStyle(color = RenzoColors.Foreground.copy(alpha = 0.8f))) {
                                append(chapter.sourceProviderName ?: "unknown source")
                            }
                        },
                        fontSize = 11.sp, color = Muted, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                    chapter.locked -> Text(
                        "Locked · purchase on source",
                        fontSize = 11.sp, fontWeight = FontWeight.Medium, color = Violet400,
                    )
                    else -> Text(
                        "Missing",
                        fontSize = 11.sp, fontWeight = FontWeight.Medium, color = Amber500,
                    )
                }
                formatUpload(chapter.uploadDate)?.let {
                    Text(" · $it", fontSize = 11.sp, color = Muted.copy(alpha = 0.7f), maxLines = 1)
                }
            }
        }

        if (!selecting) {
            // Save offline (native-only) — phone icon once saved.
            if (isOffline) {
                Icon(
                    Icons.Filled.PhoneAndroid, contentDescription = "Saved on device",
                    tint = Emerald500, modifier = Modifier.size(16.dp),
                )
            } else if (chapter.downloaded) {
                SquareIconButton(
                    icon = Icons.Filled.CloudDownload,
                    contentDescription = "Save offline",
                    borderColor = Color.Transparent,
                    background = Color.Transparent,
                    enabled = chapter.number !in state.offlineSaving,
                    onClick = { vm.saveOffline(listOf(chapter)) },
                )
            }

            // Read / unread toggle.
            if (state.readerEnabled) {
                SquareIconButton(
                    icon = if (chapter.isCompleted) Icons.Filled.CheckCircle else Icons.Outlined.Circle,
                    contentDescription = if (chapter.isCompleted) "Mark as unread" else "Mark as read",
                    tint = if (chapter.isCompleted) Emerald500 else Muted.copy(alpha = 0.5f),
                    borderColor = Color.Transparent,
                    background = Color.Transparent,
                    enabled = !readPending,
                    onClick = { vm.toggleRead(chapter.number, !chapter.isCompleted) },
                )
            }

            // (Re-)download split button.
            if (state.canManageDownloads && !chapter.locked) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .height(32.dp)
                            .width(36.dp)
                            .clip(RoundedCornerShape(topStart = 8.dp, bottomStart = 8.dp))
                            .background(RenzoColors.Foreground.copy(alpha = 0.03f))
                            .border(
                                1.dp,
                                Border60,
                                RoundedCornerShape(topStart = 8.dp, bottomStart = 8.dp),
                            )
                            .focusRing(redownloadFocus.focused, 8.dp)
                            .tvClickable(
                                onFocused = redownloadFocus::set,
                                enabled = disabledReason == null && !isPending,
                            ) { vm.redownload(chapter.number) }
                            .then(if (disabledReason == null && !isPending) Modifier else Modifier.alphaHalf()),
                    ) {
                        Icon(
                            if (chapter.downloaded) Icons.Filled.Refresh else Icons.Filled.Download,
                            contentDescription = disabledReason ?: label,
                            tint = RenzoColors.Foreground,
                            modifier = Modifier.size(14.dp),
                        )
                    }
                    Box {
                        Box(
                            contentAlignment = Alignment.Center,
                            modifier = Modifier
                                .height(32.dp)
                                .width(26.dp)
                                .clip(RoundedCornerShape(topEnd = 8.dp, bottomEnd = 8.dp))
                                .background(RenzoColors.Foreground.copy(alpha = 0.03f))
                                .border(
                                    1.dp,
                                    Border60,
                                    RoundedCornerShape(topEnd = 8.dp, bottomEnd = 8.dp),
                                )
                                .focusRing(sourceMenuFocus.focused, 8.dp)
                                .tvClickable(onFocused = sourceMenuFocus::set, enabled = !isPending) {
                                    sourceMenuOpen = true
                                }
                                .then(if (isPending) Modifier.alphaHalf() else Modifier),
                        ) {
                            Icon(
                                Icons.Filled.ExpandMore,
                                contentDescription = "Choose source",
                                tint = RenzoColors.Foreground,
                                modifier = Modifier.size(14.dp),
                            )
                        }
                        DropdownMenu(
                            expanded = sourceMenuOpen,
                            onDismissRequest = { sourceMenuOpen = false },
                        ) {
                            Text(
                                if (chapter.downloaded) "Re-download from" else "Download from",
                                fontSize = 12.sp,
                                color = Muted,
                                modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                            )
                            HorizontalDivider(color = Border40)
                            chapter.availableProviders.forEach { src ->
                                DropdownMenuItem(
                                    text = { Text(src.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                                    trailingIcon = {
                                        if (src.id == chapter.sourceProviderId) {
                                            Icon(
                                                Icons.Filled.Check,
                                                contentDescription = null,
                                                tint = Muted,
                                                modifier = Modifier.size(14.dp),
                                            )
                                        }
                                    },
                                    onClick = {
                                        sourceMenuOpen = false
                                        vm.redownload(chapter.number, src.id)
                                    },
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

/** The delete-downloads confirmation from chapters-section.tsx. */
@Composable
fun DeleteDownloadsDialog(state: SeriesDetailUiState, vm: SeriesDetailViewModel) {
    val scope = state.confirmDelete ?: return
    val count =
        if (scope == DeleteDownloadsScope.ALL) state.downloadedCount
        else state.selectedDownloadedNumbers.size
    RenzoDialog(
        title = if (scope == DeleteDownloadsScope.ALL) "Delete all downloads?" else "Delete selected downloads?",
        onDismiss = { vm.dismissDeleteDownloads() },
        description = "This removes the downloaded files for " +
            (if (scope == DeleteDownloadsScope.ALL) "all " else "") +
            "$count ${if (scope == DeleteDownloadsScope.ALL) "downloaded " else "selected "}" +
            "chapter${if (count == 1) "" else "s"} from disk. Chapter history is kept — you " +
            "can re-download them later.",
        footer = {
            OutlineDialogButton(text = "Cancel", enabled = !state.deleteDownloadsPending) {
                vm.dismissDeleteDownloads()
            }
            DestructiveDialogButton(
                text = if (state.deleteDownloadsPending) "Deleting…" else "Delete",
                enabled = !state.deleteDownloadsPending,
            ) { vm.confirmDeleteDownloads() }
        },
    )
}

/** Small spacer helper so the section reads like the web's `space-y-2`. */
@Composable
fun RowGap(height: Int = 8) {
    Spacer(Modifier.height(height.dp))
}
