package app.renzoshiori.client.ui.sources

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
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
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.FileUpload
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.documentfile.provider.DocumentFile
import app.renzoshiori.client.data.model.ExtensionInfoDto
import app.renzoshiori.client.data.network.SourcesApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.launch
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.toRequestBody

/**
 * Extension version manager — the "keep it working while upstream fixes it"
 * panel, transliterated from components/comp/sources/extension-versions.tsx:
 * per extension, switch between installed versions (rollback), pin against
 * auto-update, or sideload a patched APK. Loads lazily on first open, exactly
 * like the web's `enabled: open` query.
 */
@Composable
internal fun ExtensionVersionsPanel(api: SourcesApi?, snackbar: SnackbarHostState) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var open by remember { mutableStateOf(false) }
    val extensions = remember { mutableStateListOf<ExtensionInfoDto>() }
    var isLoading by remember { mutableStateOf(false) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var sideloading by remember { mutableStateOf(false) }

    suspend fun refresh() {
        val list = runCatching { api?.extensions() ?: emptyList() }.getOrNull()
        if (list != null) {
            extensions.clear()
            extensions.addAll(list)
            loadError = null
        } else {
            loadError = "Could not load extensions."
        }
    }

    LaunchedEffect(open) {
        if (open && extensions.isEmpty() && loadError == null) {
            isLoading = true
            refresh()
            isLoading = false
        }
    }

    fun replace(updated: ExtensionInfoDto?) {
        if (updated == null) return
        val i = extensions.indexOfFirst { it.name == updated.name }
        if (i >= 0) extensions[i] = updated else extensions.add(updated)
    }

    val apkPicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        val name = DocumentFile.fromSingleUri(context, uri)?.name ?: "extension.apk"
        scope.launch {
            sideloading = true
            busy = true
            val bytes = runCatching { context.contentResolver.openInputStream(uri)?.readBytes() }.getOrNull()
            if (bytes == null) {
                snackbar.showSnackbar("Sideload failed — previous version remains active.")
            } else {
                val part = MultipartBody.Part.createFormData(
                    "file", name,
                    bytes.toRequestBody("application/vnd.android.package-archive".toMediaType()),
                )
                val ext = runCatching { api?.sideloadExtension(part) }.getOrNull()
                if (ext == null) {
                    snackbar.showSnackbar("Sideload failed — previous version remains active.")
                } else {
                    replace(ext)
                    refresh()
                    snackbar.showSnackbar(
                        "Sideloaded ${ext.name} ${ext.activeVersion} (pinned against auto-update)",
                    )
                }
            }
            busy = false
            sideloading = false
        }
    }

    val chevronRotation by animateFloatAsState(if (open) 180f else 0f, label = "chevron")
    val shape = RoundedCornerShape(12.dp)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(RenzoColors.Card.copy(alpha = 0.40f))
            .border(1.dp, RenzoColors.Border.copy(alpha = 0.60f), shape),
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .fillMaxWidth()
                .clickable { open = !open }
                .padding(horizontal = 16.dp, vertical = 12.dp),
        ) {
            Icon(
                Icons.Filled.Archive,
                contentDescription = null,
                tint = RenzoColors.MutedForeground,
                modifier = Modifier.size(16.dp),
            )
            Column(modifier = Modifier.padding(start = 8.dp).weight(1f)) {
                Text(
                    "Extension versions",
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.SemiBold,
                    color = RenzoColors.Foreground,
                )
                Text(
                    "rollback · pin · sideload — keep a source working while a fix is in the works",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                )
            }
            Icon(
                Icons.Filled.ExpandMore,
                contentDescription = null,
                tint = RenzoColors.MutedForeground,
                modifier = Modifier.size(16.dp).rotate(chevronRotation),
            )
        }

        if (open) {
            Box(Modifier.fillMaxWidth().height(1.dp).background(RenzoColors.Border.copy(alpha = 0.60f)))
            Column(modifier = Modifier.padding(horizontal = 16.dp).padding(bottom = 16.dp)) {
                Text(
                    "Switching to an older version pins it — auto-update won't replace a " +
                        "pinned extension until you unpin it. Sideloaded APKs only replace the " +
                        "running version if they compile successfully.",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.padding(top = 12.dp),
                )
                // "Install APK" — beside the paragraph on desktop, under it here.
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .padding(top = 12.dp)
                        .clip(RoundedCornerShape(50))
                        .background(RenzoColors.Primary.copy(alpha = 0.10f))
                        .border(1.dp, RenzoColors.Primary.copy(alpha = 0.40f), RoundedCornerShape(50))
                        .clickable(enabled = !busy) { apkPicker.launch("*/*") }
                        .padding(horizontal = 12.dp, vertical = 6.dp),
                ) {
                    if (sideloading) {
                        CircularProgressIndicator(
                            strokeWidth = 2.dp,
                            color = RenzoColors.Primary,
                            modifier = Modifier.size(14.dp),
                        )
                    } else {
                        Icon(
                            Icons.Filled.FileUpload,
                            contentDescription = null,
                            tint = RenzoColors.Primary,
                            modifier = Modifier.size(14.dp),
                        )
                    }
                    Text(
                        "Install APK",
                        style = MaterialTheme.typography.bodySmall,
                        fontWeight = FontWeight.Medium,
                        color = RenzoColors.Primary,
                        modifier = Modifier.padding(start = 6.dp),
                    )
                }

                when {
                    isLoading -> Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.padding(vertical = 24.dp),
                    ) {
                        CircularProgressIndicator(
                            strokeWidth = 2.dp,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.size(16.dp),
                        )
                        Text(
                            "Loading extensions…",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.padding(start = 8.dp),
                        )
                    }

                    loadError != null -> Text(
                        loadError!!,
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.Red,
                        modifier = Modifier.padding(vertical = 24.dp),
                    )

                    else -> Column(
                        verticalArrangement = Arrangement.spacedBy(6.dp),
                        modifier = Modifier
                            .padding(top = 12.dp)
                            .heightIn(max = 420.dp)
                            .verticalScroll(rememberScrollState()),
                    ) {
                        extensions.forEach { ext ->
                            ExtensionVersionRow(
                                ext = ext,
                                busy = busy,
                                onSetActive = { version ->
                                    scope.launch {
                                        busy = true
                                        val updated = runCatching {
                                            api?.setActiveVersion(ext.name, version)
                                        }.getOrNull()
                                        if (updated == null) {
                                            snackbar.showSnackbar("Could not switch version.")
                                        } else {
                                            replace(updated)
                                            snackbar.showSnackbar(
                                                "${updated.name} switched to ${updated.activeVersion}" +
                                                    if (updated.autoUpdate) "" else " (pinned)",
                                            )
                                        }
                                        busy = false
                                    }
                                },
                                onSetAutoUpdate = { enabled ->
                                    scope.launch {
                                        busy = true
                                        val updated = runCatching {
                                            api?.setAutoUpdate(ext.name, enabled)
                                        }.getOrNull()
                                        if (updated == null) {
                                            snackbar.showSnackbar("Could not update pin.")
                                        } else {
                                            replace(updated)
                                            snackbar.showSnackbar(
                                                if (updated.autoUpdate)
                                                    "${updated.name} unpinned — back on ${updated.activeVersion}, auto-updating"
                                                else
                                                    "${updated.name} pinned on ${updated.activeVersion}",
                                            )
                                        }
                                        busy = false
                                    }
                                },
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ExtensionVersionRow(
    ext: ExtensionInfoDto,
    busy: Boolean,
    onSetActive: (String) -> Unit,
    onSetAutoUpdate: (Boolean) -> Unit,
) {
    var versionsOpen by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(8.dp)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(RenzoColors.Card.copy(alpha = 0.50f))
            .border(1.dp, RenzoColors.Border.copy(alpha = 0.40f), shape)
            .padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                ext.name,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.Medium,
                color = RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f, fill = false),
            )
            if (!ext.autoUpdate) {
                Icon(
                    Icons.Filled.PushPin,
                    contentDescription = null,
                    tint = RenzoColors.Amber,
                    modifier = Modifier.padding(start = 6.dp).size(12.dp),
                )
            }
        }
        Text(
            "${ext.versions.size} version${if (ext.versions.size == 1) "" else "s"} installed",
            fontSize = 11.sp,
            color = RenzoColors.MutedForeground,
        )

        // Version select + auto-update switch — a right-hand cluster on the
        // web, a row of its own underneath here.
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
        ) {
            Box {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .width(170.dp)
                        .height(32.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                        .background(RenzoColors.Background)
                        .clickable(enabled = !busy) { versionsOpen = true }
                        .padding(horizontal = 10.dp),
                ) {
                    Text(
                        ext.activeVersion,
                        fontSize = 12.sp,
                        color = RenzoColors.Foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Icon(
                        Icons.Filled.ExpandMore,
                        contentDescription = null,
                        tint = RenzoColors.MutedForeground,
                        modifier = Modifier.size(16.dp),
                    )
                }
                DropdownMenu(
                    expanded = versionsOpen,
                    onDismissRequest = { versionsOpen = false },
                    containerColor = RenzoColors.Popover,
                ) {
                    ext.versions.forEach { v ->
                        DropdownMenuItem(
                            text = {
                                Text(
                                    v.version + if (v.isLocal) " (sideloaded)" else "",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = RenzoColors.Foreground,
                                )
                            },
                            onClick = {
                                versionsOpen = false
                                if (v.version != ext.activeVersion) onSetActive(v.version)
                            },
                        )
                    }
                }
            }
            Box(Modifier.weight(1f))
            Text(
                "AUTO",
                fontSize = 10.sp,
                fontWeight = FontWeight.Medium,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(end = 6.dp),
            )
            Switch(
                checked = ext.autoUpdate,
                enabled = !busy,
                onCheckedChange = { onSetAutoUpdate(it) },
                colors = SwitchDefaults.colors(
                    checkedTrackColor = RenzoColors.Primary,
                    checkedThumbColor = RenzoColors.Background,
                    uncheckedTrackColor = RenzoColors.Secondary,
                    uncheckedThumbColor = RenzoColors.Background,
                    uncheckedBorderColor = RenzoColors.Border,
                ),
            )
        }
    }
}
