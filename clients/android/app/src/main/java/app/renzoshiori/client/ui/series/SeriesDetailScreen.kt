package app.renzoshiori.client.ui.series

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.VerifiedUser
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.renzoshiori.client.ui.theme.RenzoColors

/**
 * Native port of RenzoFrontend/src/app/library/series/page.tsx.
 *
 * The web page is a full-width hero over a two-column grid (sources +
 * downloads on the left, chapters on the right). On a phone that grid
 * collapses to the same vertical order the web itself uses at mobile widths —
 * hero, chapters, sources, latest downloads — with every control, label and
 * dialog carried over unchanged.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SeriesDetailScreen(
    seriesId: String,
    onBack: () -> Unit,
    onReadChapter: (seriesId: String, chapterNumber: Double) -> Unit,
    vm: SeriesDetailViewModel = viewModel(
        key = "series-$seriesId",
        factory = SeriesDetailViewModel.factory(
            androidx.compose.ui.platform.LocalContext.current.applicationContext as android.app.Application,
            seriesId,
        ),
    ),
) {
    val state by vm.state.collectAsState()
    var showDeleteDialog by remember { mutableStateOf(false) }
    var deletePhysicalFiles by remember { mutableStateOf(false) }

    // Navigating away is the web's router.push('/library') after a delete.
    LaunchedEffect(state.deleted) { if (state.deleted) onBack() }

    // Auto-dismiss the toast the way the web's toaster does.
    LaunchedEffect(state.toast) {
        if (state.toast != null) {
            kotlinx.coroutines.delay(4000)
            vm.dismissToast()
        }
    }

    Scaffold(
        containerColor = RenzoColors.Background,
        topBar = {
            TopAppBar(
                title = {
                    Text(
                        state.title.ifEmpty { "Series" },
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.titleMedium,
                    )
                },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = RenzoColors.Background,
                    titleContentColor = RenzoColors.Foreground,
                    navigationIconContentColor = RenzoColors.Foreground,
                ),
            )
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when {
                state.loading && state.series == null -> Box(
                    Modifier.fillMaxSize(),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        "Loading series details...",
                        style = MaterialTheme.typography.bodyLarge,
                        color = RenzoColors.Foreground,
                    )
                }

                state.error != null && state.series == null -> Column(
                    modifier = Modifier.fillMaxSize(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Text(
                        state.error ?: "Error loading series",
                        style = MaterialTheme.typography.bodyLarge,
                        color = Red500,
                    )
                    Spacer(Modifier.height(16.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlineDialogButton(text = "Retry") { vm.refresh() }
                        OutlineDialogButton(text = "Back to Library") { onBack() }
                    }
                }

                else -> LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(bottom = 32.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    // ── Cinematic hero ──
                    item(key = "hero") {
                        SeriesHeroSection(
                            state = state,
                            baseUrl = vm.baseUrl,
                            vm = vm,
                            onOpenChapter = { number -> onReadChapter(seriesId, number) },
                            onRequestDeleteSeries = { showDeleteDialog = true },
                        )
                    }

                    // ── Chapters (leads on mobile, like the web's order-1) ──
                    item(key = "chapters-header") {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 16.dp)
                                .clip(MaterialTheme.shapes.large)
                                .background(RenzoColors.Card.copy(alpha = 0.4f))
                                .border(1.dp, Border60, MaterialTheme.shapes.large),
                        ) {
                            ChaptersSectionHeader(state, vm)
                        }
                    }

                    if (state.chaptersLoading && state.chapters.isEmpty()) {
                        item(key = "chapters-loading") {
                            Row(
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.fillMaxWidth().padding(vertical = 40.dp),
                            ) {
                                Spacer(Modifier.weight(1f))
                                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                                Text(
                                    "Loading chapters…",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = Muted,
                                )
                                Spacer(Modifier.weight(1f))
                            }
                        }
                    } else if (state.filteredChapters.isEmpty()) {
                        item(key = "chapters-empty") { ChaptersEmptyState(state) }
                    } else {
                        items(state.filteredChapters, key = { "ch-${it.number}" }) { chapter ->
                            ChapterRow(
                                chapter = chapter,
                                state = state,
                                isOffline = chapterKey(seriesId, chapter.number) in state.offlineKeys,
                                vm = vm,
                                onOpenChapter = { number -> onReadChapter(seriesId, number) },
                            )
                        }
                    }

                    // ── Sources ──
                    item(key = "sources") {
                        Spacer(Modifier.height(16.dp))
                        SeriesSourcesSection(state = state, baseUrl = vm.baseUrl, vm = vm)
                    }

                    // ── Latest downloads ──
                    item(key = "downloads") {
                        Spacer(Modifier.height(16.dp))
                        SeriesDownloadsPanel(state = state, baseUrl = vm.baseUrl)
                    }
                }
            }

            // ── Toast ──
            state.toast?.let { toast ->
                Column(
                    modifier = Modifier
                        .align(Alignment.BottomCenter)
                        .padding(16.dp)
                        .fillMaxWidth()
                        .clip(MaterialTheme.shapes.large)
                        .background(RenzoColors.Popover)
                        .border(
                            1.dp,
                            if (toast.destructive) DestructiveText.copy(alpha = 0.6f) else Border60,
                            MaterialTheme.shapes.large,
                        )
                        .padding(16.dp),
                ) {
                    Text(
                        toast.title,
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.SemiBold,
                        color = if (toast.destructive) DestructiveText else RenzoColors.Foreground,
                    )
                    toast.description?.let {
                        Text(
                            it,
                            fontSize = 12.sp,
                            color = Muted,
                            modifier = Modifier.padding(top = 4.dp),
                        )
                    }
                }
            }
        }
    }

    // ── Delete Series Confirmation ──
    if (showDeleteDialog) {
        RenzoDialog(
            title = "Delete Series",
            onDismiss = { showDeleteDialog = false; deletePhysicalFiles = false },
            description = "Are you sure you want to delete \"${state.title}\"? This action cannot be undone.",
            body = {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    RenzoSwitch(
                        checked = deletePhysicalFiles,
                        onCheckedChange = { deletePhysicalFiles = it },
                    )
                    Text(
                        "Also delete Physical Files",
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.Medium,
                        color = RenzoColors.Foreground,
                    )
                }
            },
            footer = {
                OutlineDialogButton(text = "Cancel", enabled = !state.deletePending) {
                    showDeleteDialog = false
                    deletePhysicalFiles = false
                }
                DestructiveDialogButton(
                    text = if (state.deletePending) "Deleting..." else "Delete",
                    icon = Icons.Filled.Delete,
                    enabled = !state.deletePending,
                ) {
                    vm.deleteSeries(deletePhysicalFiles)
                    showDeleteDialog = false
                }
            },
        )
    }

    // ── Verify Integrity Success ──
    if (state.showVerifyDialog && state.verifyResult?.success == true) {
        RenzoDialog(
            title = "Integrity Verification Complete",
            onDismiss = { vm.dismissVerifyDialog() },
            titleLeading = {
                Icon(
                    Icons.Filled.VerifiedUser,
                    contentDescription = null,
                    tint = Green500,
                    modifier = Modifier.size(20.dp),
                )
            },
            description = "The integrity verification completed successfully. All files are in good condition.",
            footer = { PrimaryDialogButton(text = "OK") { vm.dismissVerifyDialog() } },
        )
    }

    // ── Cleanup Confirmation (verification found issues) ──
    val verify = state.verifyResult
    if (state.showCleanupDialog && verify != null && !verify.success) {
        RenzoDialog(
            title = "File Issues Found",
            onDismiss = { vm.dismissCleanupDialog() },
            titleLeading = {
                Icon(
                    Icons.Filled.Delete,
                    contentDescription = null,
                    tint = Red500,
                    modifier = Modifier.size(20.dp),
                )
            },
            description = "The following issues were found with the series files. You can delete " +
                "these problematic files to clean up the series.",
            body = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    verify.badFiles.forEach { bad ->
                        val (text, color) = archiveResultDisplay(bad.result)
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(MaterialTheme.shapes.extraSmall)
                                .background(RenzoColors.Secondary)
                                .padding(8.dp),
                        ) {
                            Box(
                                contentAlignment = Alignment.Center,
                                modifier = Modifier
                                    .size(24.dp)
                                    .clip(PillShape)
                                    .background(Red500.copy(alpha = 0.15f)),
                            ) { StatusDot(Red500, size = 10) }
                            Column(Modifier.weight(1f)) {
                                Text(
                                    bad.filename,
                                    style = MaterialTheme.typography.bodySmall,
                                    fontWeight = FontWeight.Medium,
                                    color = RenzoColors.Foreground,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                Text(text, fontSize = 11.sp, color = color)
                            }
                        }
                    }
                }
            },
            footer = {
                OutlineDialogButton(text = "Cancel") { vm.dismissCleanupDialog() }
                DestructiveDialogButton(
                    text = if (state.cleanupPending) "Cleaning..." else "Delete Files",
                    icon = Icons.Filled.Delete,
                    enabled = !state.cleanupPending,
                ) { vm.confirmCleanup() }
            },
        )
    }

    // ── Delete-downloads confirmation (chapters toolbar) ──
    DeleteDownloadsDialog(state, vm)
}
