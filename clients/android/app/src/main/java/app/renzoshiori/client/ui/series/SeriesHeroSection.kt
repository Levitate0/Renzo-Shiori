package app.renzoshiori.client.ui.series

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.MenuBook
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Numbers
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.BlurredEdgeTreatment
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import app.renzoshiori.client.R
import app.renzoshiori.client.data.network.absoluteUrl
import app.renzoshiori.client.ui.theme.RenzoColors
import coil3.compose.AsyncImage

/**
 * Transliteration of series-hero.tsx. The web hero is cover-beside-info on a
 * desktop viewport and stacks on mobile — the phone gets the stacked form:
 * blurred backdrop, centred cover, status pill, title, author, meta row,
 * genre pills, description with Read more, the paused-downloads callout, the
 * icon action row, and the mono storage-path line with its copy button.
 */
@Composable
fun SeriesHeroSection(
    state: SeriesDetailUiState,
    baseUrl: String,
    vm: SeriesDetailViewModel,
    onOpenChapter: (Double) -> Unit,
    onRequestDeleteSeries: () -> Unit,
) {
    val series = state.series ?: return
    var descriptionExpanded by remember { mutableStateOf(false) }
    var coverExpanded by remember { mutableStateOf(false) }
    var copied by remember { mutableStateOf(false) }
    var moreOpen by remember { mutableStateOf(false) }
    var categoryDialogOpen by remember { mutableStateOf(false) }
    var favoritesOpen by remember { mutableStateOf(false) }
    var trackerOpen by remember { mutableStateOf(false) }
    val context = LocalContext.current

    val coverUrl = state.displayThumbnail.takeIf { it.isNotEmpty() }?.let { absoluteUrl(baseUrl, it) }
    val statusColors = heroStatusColors(state.effectiveStatus)
    val statusLabel = heroStatusLabel(state.effectiveStatus)

    // The category the backend derived, falling back to parsing the storage
    // path ({owner}/{Category}/{leaf}), matched case-insensitively.
    val currentCategory = remember(series.category, series.path, state.categories) {
        val fromServer = series.category
        val direct = state.categories.firstOrNull { it.equals(fromServer, ignoreCase = true) }
        if (direct != null) direct else {
            val parts = (series.path.ifEmpty { series.storagePath }).split("/").filter { it.isNotEmpty() }
            val seg = if (parts.size >= 3) parts[parts.size - 2] else null
            seg?.let { s -> state.categories.firstOrNull { it.equals(s, ignoreCase = true) } }
        }
    }

    Box(Modifier.fillMaxWidth().clipToBounds()) {

        // ── Blurred banner + gradient + pink tint layers ──
        if (coverUrl != null) {
            AsyncImage(
                model = coverUrl,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier
                    .matchParentSize()
                    .blur(28.dp, BlurredEdgeTreatment.Unbounded)
                    .alpha(0.4f),
            )
        }
        Box(
            Modifier
                .matchParentSize()
                .background(
                    Brush.verticalGradient(
                        listOf(
                            RenzoColors.Background.copy(alpha = 0.30f),
                            RenzoColors.Background.copy(alpha = 0.70f),
                            RenzoColors.Background,
                        ),
                    ),
                ),
        )
        Box(
            Modifier
                .matchParentSize()
                .background(
                    Brush.verticalGradient(
                        listOf(
                            MaterialTheme.colorScheme.primary.copy(alpha = 0.10f),
                            Color.Transparent,
                        ),
                    ),
                ),
        )

        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 32.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            // ── Cover (click to expand) ──
            Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .width(150.dp)
                        .height(225.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .background(RenzoColors.Card)
                        .border(1.dp, Color.White.copy(alpha = 0.06f), RoundedCornerShape(12.dp))
                        .clickable { coverExpanded = true },
                ) {
                    // Renzō logo shows through whenever the cover is missing or
                    // fails to load — the web's /renzo.png onError fallback.
                    Image(
                        painter = painterResource(R.drawable.splash_icon),
                        contentDescription = null,
                        modifier = Modifier.size(72.dp).alpha(0.5f),
                    )
                    if (coverUrl != null) {
                        AsyncImage(
                            model = coverUrl,
                            contentDescription = state.title,
                            contentScale = ContentScale.Crop,
                            modifier = Modifier.fillMaxSize(),
                        )
                    }
                }
            }

            // ── Status pill ──
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier
                    .clip(PillShape)
                    .background(statusColors.dot.copy(alpha = 0.12f))
                    .border(1.dp, statusColors.dot.copy(alpha = 0.25f), PillShape)
                    .padding(horizontal = 10.dp, vertical = 3.dp),
            ) {
                if (statusColors.pulse) {
                    val transition = rememberInfiniteTransition(label = "status-pulse")
                    val pulse by transition.animateFloat(
                        initialValue = 1f,
                        targetValue = 0.35f,
                        animationSpec = infiniteRepeatable(tween(1000), RepeatMode.Reverse),
                        label = "status-pulse-alpha",
                    )
                    StatusDot(statusColors.dot.copy(alpha = pulse))
                } else {
                    StatusDot(statusColors.dot)
                }
                Text(
                    statusLabel.uppercase(),
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Medium,
                    color = statusColors.text,
                )
            }

            // ── Title ──
            Text(
                state.title,
                fontSize = 26.sp,
                lineHeight = 30.sp,
                fontWeight = FontWeight.Bold,
                color = RenzoColors.Foreground,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )

            // ── Author / artist ──
            if (series.author.isNotEmpty() || series.artist.isNotEmpty()) {
                val suffix =
                    if (series.artist.isNotEmpty() && series.artist != series.author)
                        " · illust. ${series.artist}" else ""
                Text(
                    "by ${series.author}$suffix",
                    style = MaterialTheme.typography.bodySmall,
                    color = Muted,
                )
            }

            // ── Inline meta row ──
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                Text(
                    "Ch. ${series.chapterList.ifEmpty { "—" }}",
                    style = MaterialTheme.typography.bodySmall,
                    color = Muted,
                )
                Text("·", style = MaterialTheme.typography.bodySmall, color = Muted.copy(alpha = 0.4f))
                Text(
                    "${series.chapterCount} chapter${if (series.chapterCount == 1) "" else "s"}",
                    style = MaterialTheme.typography.bodySmall,
                    color = Muted,
                )
                if (!series.lastChangeUTC.isNullOrEmpty()) {
                    Text("·", style = MaterialTheme.typography.bodySmall, color = Muted.copy(alpha = 0.4f))
                    Text(
                        "Updated ${formatRelative(series.lastChangeUTC)}",
                        style = MaterialTheme.typography.bodySmall,
                        color = Muted,
                    )
                }
            }

            // ── Genre pills ──
            if (series.genre.isNotEmpty()) {
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    series.genre.forEach { g ->
                        Text(
                            g,
                            fontSize = 11.sp,
                            color = RenzoColors.Foreground.copy(alpha = 0.8f),
                            modifier = Modifier
                                .clip(PillShape)
                                .background(ForegroundFaint06)
                                .border(1.dp, Border40, PillShape)
                                .padding(horizontal = 8.dp, vertical = 2.dp),
                        )
                    }
                }
            }

            // ── Description + Read more (expanded scrolls in its own box) ──
            if (series.description.isNotBlank()) {
                Column {
                    Text(
                        series.description,
                        style = MaterialTheme.typography.bodySmall,
                        color = Muted,
                        maxLines = if (descriptionExpanded) Int.MAX_VALUE else 3,
                        overflow = TextOverflow.Ellipsis,
                        modifier = if (descriptionExpanded) {
                            Modifier.heightIn(max = 256.dp).verticalScroll(rememberScrollState())
                        } else Modifier,
                    )
                    if (series.description.length > 240) {
                        Text(
                            if (descriptionExpanded) "Show less" else "Read more",
                            fontSize = 12.sp,
                            fontWeight = FontWeight.Medium,
                            color = MaterialTheme.colorScheme.primary,
                            modifier = Modifier
                                .padding(top = 4.dp)
                                .clickable { descriptionExpanded = !descriptionExpanded },
                        )
                    }
                }
            }

            // ── Paused-downloads callout ──
            if (state.canManageDownloads && state.pausedDownloads) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(Yellow500.copy(alpha = 0.10f))
                        .border(1.dp, Yellow500.copy(alpha = 0.40f), RoundedCornerShape(12.dp))
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Icon(
                            Icons.Filled.Pause,
                            contentDescription = null,
                            tint = Yellow300,
                            modifier = Modifier.size(16.dp),
                        )
                        Text(
                            "Downloads are paused for this series.",
                            style = MaterialTheme.typography.bodyMedium,
                            fontWeight = FontWeight.Medium,
                            color = Yellow300,
                        )
                    }
                    androidx.compose.material3.Button(
                        onClick = { vm.togglePausedDownloads() },
                        shape = MaterialTheme.shapes.small,
                        colors = androidx.compose.material3.ButtonDefaults.buttonColors(
                            containerColor = Yellow500,
                            contentColor = Color.Black,
                        ),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 10.dp),
                    ) {
                        Icon(Icons.Filled.PlayArrow, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(8.dp))
                        Text("Resume Downloads", fontWeight = FontWeight.SemiBold)
                    }
                }
            }

            // ── Action toolbar ──
            val readTarget = remember(state.chapters) { computeReadTarget(state.chapters) }
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.padding(top = 4.dp),
            ) {
                if (state.readerEnabled && readTarget != null) {
                    HeroActionButton(
                        icon = Icons.Filled.MenuBook,
                        contentDescription = "${readTarget.label} — chapter ${formatNumber(readTarget.number)}",
                        primary = true,
                        onClick = { onOpenChapter(readTarget.number) },
                    )
                }

                HeroActionButton(
                    icon = if (state.isFavorited) Icons.Filled.Favorite else Icons.Filled.FavoriteBorder,
                    contentDescription = if (state.isFavorited) "In your favourites — manage lists" else "Add to favourites",
                    tint = if (state.isFavorited) Pink500 else RenzoColors.Foreground,
                    borderColor = if (state.isFavorited) Pink500.copy(alpha = 0.6f) else Border60,
                    background = if (state.isFavorited) Pink500.copy(alpha = 0.15f) else Color.Transparent,
                    onClick = { favoritesOpen = true },
                )

                if (state.hasConnectedTracker) {
                    HeroActionButton(
                        icon = Icons.Filled.Podcasts,
                        contentDescription = "Track this series",
                        tint = if (state.tracked) MaterialTheme.colorScheme.primary else RenzoColors.Foreground,
                        borderColor = if (state.tracked) MaterialTheme.colorScheme.primary.copy(alpha = 0.5f) else Border60,
                        onClick = { trackerOpen = true },
                    )
                }

                if (state.canManageDownloads && !state.pausedDownloads) {
                    HeroActionButton(
                        icon = Icons.Filled.Pause,
                        contentDescription = "Pause Downloads",
                        primary = true,
                        onClick = { vm.togglePausedDownloads() },
                    )
                }

                if (state.canEdit || state.canDelete) {
                    Box {
                        HeroActionButton(
                            icon = Icons.Filled.MoreHoriz,
                            contentDescription = "More actions",
                            onClick = { moreOpen = true },
                        )
                        DropdownMenu(
                            expanded = moreOpen,
                            onDismissRequest = { moreOpen = false },
                        ) {
                            if (state.canEdit) {
                                DropdownMenuItem(
                                    text = { Text("Refresh metadata & chapters") },
                                    leadingIcon = { Icon(Icons.Filled.Refresh, contentDescription = null) },
                                    onClick = { moreOpen = false; vm.refreshMetadata() },
                                )
                                DropdownMenuItem(
                                    text = { Text("Scan for new chapters") },
                                    leadingIcon = { Icon(Icons.Filled.Search, contentDescription = null) },
                                    onClick = { moreOpen = false; vm.scanForNewChapters() },
                                )
                                DropdownMenuItem(
                                    text = { Text("Verify integrity") },
                                    leadingIcon = { Icon(Icons.Filled.CheckCircle, contentDescription = null) },
                                    onClick = { moreOpen = false; vm.verifyIntegrity() },
                                )
                                DropdownMenuItem(
                                    text = { Text(if (series.nsfw) "Unmark as 18+" else "Mark as 18+") },
                                    leadingIcon = {
                                        Text(
                                            "18+",
                                            fontSize = 12.sp,
                                            fontWeight = FontWeight.SemiBold,
                                            color = if (series.nsfw) Red500 else RenzoColors.Foreground,
                                        )
                                    },
                                    onClick = { moreOpen = false; vm.toggleNsfw() },
                                )
                                DropdownMenuItem(
                                    text = {
                                        Text(
                                            if (series.hideDecimalChapters) "Show decimal chapters (.5)"
                                            else "Hide decimal chapters (.5)",
                                        )
                                    },
                                    leadingIcon = {
                                        Icon(
                                            Icons.Filled.Numbers,
                                            contentDescription = null,
                                            tint = if (series.hideDecimalChapters) MaterialTheme.colorScheme.primary
                                            else RenzoColors.Foreground,
                                        )
                                    },
                                    onClick = { moreOpen = false; vm.toggleHideDecimalChapters() },
                                )
                                if (state.categories.isNotEmpty()) {
                                    DropdownMenuItem(
                                        text = { Text("Category" + (currentCategory?.let { ": $it" } ?: "")) },
                                        leadingIcon = { Icon(Icons.Filled.FolderOpen, contentDescription = null) },
                                        onClick = { moreOpen = false; categoryDialogOpen = true },
                                    )
                                }
                            }
                            if (state.canDelete) {
                                if (state.canEdit) HorizontalDivider(color = Border40)
                                DropdownMenuItem(
                                    text = { Text("Delete series", color = DestructiveText) },
                                    leadingIcon = {
                                        Icon(
                                            Icons.Filled.Delete,
                                            contentDescription = null,
                                            tint = DestructiveText,
                                        )
                                    },
                                    onClick = { moreOpen = false; onRequestDeleteSeries() },
                                )
                            }
                        }
                    }
                }
            }

            // ── Storage path ──
            if (series.path.isNotEmpty()) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Icon(
                        Icons.Filled.FolderOpen,
                        contentDescription = null,
                        tint = Muted.copy(alpha = 0.7f),
                        modifier = Modifier.size(12.dp),
                    )
                    Text(
                        series.path,
                        fontSize = 11.sp,
                        fontFamily = FontFamily.Monospace,
                        color = Muted.copy(alpha = 0.7f),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .size(20.dp)
                            .clip(MaterialTheme.shapes.extraSmall)
                            .clickable {
                                val cm = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
                                    as? android.content.ClipboardManager
                                cm?.setPrimaryClip(
                                    android.content.ClipData.newPlainText("Storage path", series.path),
                                )
                                copied = true
                            },
                    ) {
                        Icon(
                            if (copied) Icons.Filled.Check else Icons.Filled.ContentCopy,
                            contentDescription = "Copy storage path",
                            tint = if (copied) Green500 else Muted,
                            modifier = Modifier.size(12.dp),
                        )
                    }
                }
            }
        }
    }

    // ── Cover lightbox ──
    if (coverExpanded && coverUrl != null) {
        Dialog(onDismissRequest = { coverExpanded = false }) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .clickable { coverExpanded = false },
                contentAlignment = Alignment.Center,
            ) {
                AsyncImage(
                    model = coverUrl,
                    contentDescription = state.title,
                    contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)),
                )
            }
        }
    }

    // ── Category picker ──
    if (categoryDialogOpen) {
        RenzoDialog(
            title = "Category",
            description = "Move this series into a category folder.",
            onDismiss = { categoryDialogOpen = false },
            body = {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    state.categories.forEach { cat ->
                        val selected = cat == currentCategory
                        MenuRow(
                            label = cat,
                            icon = Icons.Filled.Check,
                            iconTint = if (selected) RenzoColors.Foreground else Color.Transparent,
                            onClick = {
                                categoryDialogOpen = false
                                if (!selected) vm.setCategory(cat)
                            },
                        )
                    }
                    if (currentCategory != null) {
                        MenuRow(
                            label = "Uncategorized",
                            labelColor = Muted,
                            onClick = {
                                categoryDialogOpen = false
                                vm.setCategory(null)
                            },
                        )
                    }
                }
            },
            footer = { OutlineDialogButton(text = "Close") { categoryDialogOpen = false } },
        )
    }

    // ── Favourites picker ──
    if (favoritesOpen) {
        FavoritesDialog(state = state, vm = vm, onDismiss = { favoritesOpen = false })
    }

    // ── Tracker control ──
    if (trackerOpen) {
        TrackerDialog(state = state, vm = vm, onDismiss = { trackerOpen = false })
    }
}

/** `px-0 w-9` icon button from the hero toolbar (default or outline variant). */
@Composable
private fun HeroActionButton(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    primary: Boolean = false,
    tint: Color = if (primary) RenzoColors.PrimaryForeground else RenzoColors.Foreground,
    borderColor: Color = Border60,
    background: Color = if (primary) MaterialTheme.colorScheme.primary else Color.Transparent,
) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(36.dp)
            .clip(MaterialTheme.shapes.small)
            .background(background)
            .border(1.dp, if (primary) Color.Transparent else borderColor, MaterialTheme.shapes.small)
            .clickable(onClick = onClick),
    ) {
        Icon(icon, contentDescription = contentDescription, tint = tint, modifier = Modifier.size(16.dp))
    }
}

/** read-series-button.tsx's target resolution: continue / read / reread. */
private data class ReadTarget(val number: Double, val label: String)

private fun computeReadTarget(chapters: List<ChapterRowUi>): ReadTarget? {
    val readable = chapters.filter { it.filename != null }.sortedBy { it.number }
    if (readable.isEmpty()) return null
    readable.firstOrNull { !it.isCompleted && it.progress > 0f }?.let {
        return ReadTarget(it.number, "Continue")
    }
    readable.firstOrNull { !it.isCompleted }?.let {
        val anyRead = readable.any { c -> c.isCompleted }
        return ReadTarget(it.number, if (anyRead) "Continue" else "Read")
    }
    return ReadTarget(readable.first().number, "Reread")
}

/**
 * favorite-button.tsx's picker: tick the lists this series belongs to, plus a
 * "New tab" creator. Sub-lists render indented under their parent tab.
 */
@Composable
private fun FavoritesDialog(
    state: SeriesDetailUiState,
    vm: SeriesDetailViewModel,
    onDismiss: () -> Unit,
) {
    var newTabName by remember { mutableStateOf("") }
    val seriesId = state.series?.id ?: ""
    val topLevel = state.favoriteLists.filter { it.parentId == null }.sortedBy { it.sortOrder }

    RenzoDialog(
        title = "Favourites",
        description = "Tick the lists this series belongs to. Create tabs and sub-lists to organize.",
        onDismiss = onDismiss,
        body = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                if (topLevel.isEmpty()) {
                    Text(
                        "No favourites lists yet — create your first tab below (e.g. “Manhwa favourites”).",
                        style = MaterialTheme.typography.bodySmall,
                        color = Muted,
                        modifier = Modifier.padding(vertical = 8.dp),
                    )
                }
                topLevel.forEach { list ->
                    FavoriteListRow(list.id, list.name, seriesId in list.seriesIds, 0, vm)
                    state.favoriteLists.filter { it.parentId == list.id }.sortedBy { it.sortOrder }
                        .forEach { child ->
                            FavoriteListRow(child.id, child.name, seriesId in child.seriesIds, 1, vm)
                        }
                }
                HorizontalDivider(color = Border40, modifier = Modifier.padding(vertical = 8.dp))
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    OutlinedTextField(
                        value = newTabName,
                        onValueChange = { newTabName = it },
                        placeholder = { Text("New tab, e.g. Manhwa favourites #2", style = MaterialTheme.typography.bodySmall) },
                        singleLine = true,
                        shape = MaterialTheme.shapes.small,
                        modifier = Modifier.weight(1f),
                    )
                    PrimaryDialogButton(text = "Tab", enabled = newTabName.isNotBlank()) {
                        vm.createFavoriteList(newTabName)
                        newTabName = ""
                    }
                }
            }
        },
        footer = { OutlineDialogButton(text = "Done") { onDismiss() } },
    )
}

@Composable
private fun FavoriteListRow(
    listId: String,
    name: String,
    checked: Boolean,
    depth: Int,
    vm: SeriesDetailViewModel,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = (depth * 22).dp)
            .clip(MaterialTheme.shapes.extraSmall)
            .clickable { vm.toggleFavoriteList(listId, checked) }
            .padding(horizontal = 8.dp, vertical = 8.dp),
    ) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(18.dp)
                .clip(MaterialTheme.shapes.extraSmall)
                .background(if (checked) MaterialTheme.colorScheme.primary else Color.Transparent)
                .border(1.dp, if (checked) Color.Transparent else Border60, MaterialTheme.shapes.extraSmall),
        ) {
            if (checked) {
                Icon(
                    Icons.Filled.Check,
                    contentDescription = null,
                    tint = RenzoColors.PrimaryForeground,
                    modifier = Modifier.size(12.dp),
                )
            }
        }
        Text(name, style = MaterialTheme.typography.bodyMedium, color = RenzoColors.Foreground)
    }
}

/**
 * series-tracking-button.tsx: one "Track this series" switch plus a per-tracker
 * list. Connecting a tracker itself still happens on Account → Trackers.
 */
@Composable
private fun TrackerDialog(
    state: SeriesDetailUiState,
    vm: SeriesDetailViewModel,
    onDismiss: () -> Unit,
) {
    RenzoDialog(
        title = "Track",
        onDismiss = onDismiss,
        body = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
                ) {
                    Text(
                        "Track this series",
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.Medium,
                        color = RenzoColors.Foreground,
                        modifier = Modifier.weight(1f),
                    )
                    RenzoSwitch(
                        checked = state.tracked,
                        onCheckedChange = { vm.setTracking(it) },
                        enabled = !state.trackerBusy,
                    )
                }
                HorizontalDivider(color = Border40)
                Text(
                    "Per tracker",
                    fontSize = 11.sp,
                    color = Muted,
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 6.dp),
                )
                state.trackerConfigs.forEach { c ->
                    val link = state.trackerMatches.firstOrNull {
                        it.seriesId.equals(state.series?.id ?: "", ignoreCase = true) &&
                            it.provider == c.provider &&
                            (it.mappingStatus == 1 || it.mappingStatus == 2)
                    }
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp),
                    ) {
                        Text(
                            c.displayName,
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (c.isConnected) RenzoColors.Foreground else Muted,
                            modifier = Modifier.weight(1f),
                        )
                        Text(
                            when {
                                !c.isConnected -> "Not connected"
                                link != null -> link.externalSeriesTitle ?: "Linked"
                                else -> "Not linked"
                            },
                            fontSize = 11.sp,
                            color = if (link != null) MaterialTheme.colorScheme.primary else Muted,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
        },
        footer = { OutlineDialogButton(text = "Done") { onDismiss() } },
    )
}
