package app.renzoshiori.client.ui.downloads

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.RenzoDownloadService
import app.renzoshiori.client.RenzoStore
import app.renzoshiori.client.data.offline.OfflineRepository
import app.renzoshiori.client.ui.library.formatBytes
import app.renzoshiori.client.ui.library.formatChapter
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** The auto-clean-on-reconnect toggle, stored under the web's own key name. */
private const val AUTO_PURGE_KEY = "renzo.offline.autopurge"

private data class ActiveSeriesProgress(val title: String, val done: Int, val total: Int)

/**
 * Offline downloads — 1:1 port of RenzoFrontend src/app/downloads/page.tsx
 * (the page is native-only on the web too: it renders a "this lives in the
 * app" notice in a browser). Header with the chapter/byte totals and Clear
 * all, the live "Downloading now" panel, the Download folder + Auto-clean
 * settings card, and the saved-chapter list with per-row delete.
 */
@Composable
fun DownloadsScreen() {
    val context = LocalContext.current
    val renzoApp = context.applicationContext as RenzoApp
    val scope = rememberCoroutineScope()
    val store = remember { RenzoStore(context.applicationContext) }

    var chapters by remember { mutableStateOf<List<OfflineRepository.OfflineChapter>?>(null) }
    var bytes by remember { mutableStateOf(0L) }
    var folder by remember { mutableStateOf<String?>(null) }
    var autoPurge by remember { mutableStateOf(store.kvGet(AUTO_PURGE_KEY) != "off") }
    var toast by remember { mutableStateOf<String?>(null) }
    var reloadTick by remember { mutableStateOf(0) }

    // Live per-series progress, fed by RenzoDownloadService's broadcast.
    val activeSeries = remember { mutableStateMapOf<String, ActiveSeriesProgress>() }
    var downloading by remember { mutableStateOf(false) }

    val folderPicker = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocumentTree(),
    ) { uri: Uri? ->
        if (uri != null) {
            runCatching {
                context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
                )
            }
            store.setFolder(uri)
            folder = store.folderLabel()
            toast = "Download folder set — ${folder ?: uri}"
        }
    }

    LaunchedEffect(reloadTick) {
        val loaded = withContext(Dispatchers.IO) {
            val list = renzoApp.offline.listChapters()
            val total = list.sumOf { it.bytes }
            val label = store.folderLabel()
            Triple(list, total, label)
        }
        chapters = loaded.first
        bytes = loaded.second
        folder = loaded.third
    }

    DisposableEffect(Unit) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context?, intent: Intent?) {
                if (intent == null) return
                val state = intent.getStringExtra("state") ?: return
                val seriesId = intent.getStringExtra("seriesId")
                val done = intent.getIntExtra("done", 0)
                val total = intent.getIntExtra("total", 0)
                when (state) {
                    "idle" -> {
                        downloading = false
                        activeSeries.clear()
                        reloadTick++
                    }
                    "downloading", "saved" -> {
                        downloading = true
                        if (seriesId != null && total > 0) {
                            val known = activeSeries[seriesId]?.title
                                ?: chapters?.firstOrNull { it.seriesId == seriesId }?.seriesTitle
                                ?: "Saving offline…"
                            activeSeries[seriesId] = ActiveSeriesProgress(known, done, total)
                        }
                        if (state == "saved") reloadTick++
                    }
                }
            }
        }
        val filter = IntentFilter(RenzoDownloadService.BROADCAST)
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.TIRAMISU) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }
        onDispose { runCatching { context.unregisterReceiver(receiver) } }
    }

    val list = chapters
    val activeList = activeSeries.entries.filter { it.value.total > 0 }

    LazyColumn(
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 64.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        // ── Header ───────────────────────────────────────────────────────
        item(key = "header") {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        "Offline downloads",
                        style = MaterialTheme.typography.titleLarge.copy(
                            fontSize = 20.sp,
                            fontWeight = FontWeight.SemiBold,
                        ),
                        color = RenzoColors.Foreground,
                    )
                    Text(
                        "${list?.size ?: 0} chapter${if ((list?.size ?: 0) == 1) "" else "s"} · " +
                            "${formatBytes(bytes)} on device",
                        style = MaterialTheme.typography.bodySmall,
                        color = RenzoColors.MutedForeground,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
                if (!list.isNullOrEmpty()) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .height(32.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                            .clickable {
                                scope.launch {
                                    val purged = withContext(Dispatchers.IO) {
                                        val all = renzoApp.offline.listChapters()
                                        all.forEach { renzoApp.offline.deleteChapter(it.chapterKey) }
                                        all.size
                                    }
                                    toast = "Cleared — $purged chapter${if (purged == 1) "" else "s"} removed."
                                    reloadTick++
                                }
                            }
                            .padding(horizontal = 12.dp),
                    ) {
                        Icon(
                            Icons.Filled.Delete,
                            contentDescription = null,
                            tint = RenzoColors.Foreground,
                            modifier = Modifier.size(16.dp),
                        )
                        Text(
                            "Clear all",
                            style = MaterialTheme.typography.labelMedium,
                            color = RenzoColors.Foreground,
                            modifier = Modifier.padding(start = 6.dp),
                        )
                    }
                }
            }
        }

        // Toast line (the web's useToast).
        val currentToast = toast
        if (currentToast != null) {
            item(key = "toast") {
                Text(
                    currentToast,
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.Primary,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 12.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(RenzoColors.Primary.copy(alpha = 0.10f))
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                )
            }
        }

        // ── Downloading now ──────────────────────────────────────────────
        if (downloading && activeList.isNotEmpty()) {
            item(key = "active") {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 24.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .border(1.dp, RenzoColors.Emerald.copy(alpha = 0.30f), RoundedCornerShape(8.dp))
                        .background(RenzoColors.Emerald.copy(alpha = 0.04f))
                        .padding(16.dp),
                ) {
                    Text(
                        "Downloading now",
                        style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                        color = RenzoColors.Foreground,
                    )
                    activeList.forEach { entry ->
                        val s = entry.value
                        val pct = if (s.total > 0) minOf(100, (s.done * 100) / s.total) else 0
                        Spacer(Modifier.height(12.dp))
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                            Text(
                                s.title,
                                style = MaterialTheme.typography.bodySmall,
                                color = RenzoColors.Foreground,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f),
                            )
                            Text(
                                "${s.done}/${s.total}",
                                style = MaterialTheme.typography.labelSmall,
                                color = RenzoColors.MutedForeground,
                            )
                        }
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 6.dp)
                                .height(6.dp)
                                .clip(RoundedCornerShape(50))
                                .background(RenzoColors.Foreground.copy(alpha = 0.10f)),
                        ) {
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth(pct / 100f)
                                    .height(6.dp)
                                    .clip(RoundedCornerShape(50))
                                    .background(RenzoColors.Emerald),
                            )
                        }
                    }
                }
            }
        }

        // ── Settings ─────────────────────────────────────────────────────
        item(key = "settings") {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 24.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                    .padding(16.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(
                                Icons.Filled.FolderOpen,
                                contentDescription = null,
                                tint = RenzoColors.Foreground,
                                modifier = Modifier.size(16.dp),
                            )
                            Text(
                                "Download folder",
                                style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                                color = RenzoColors.Foreground,
                                modifier = Modifier.padding(start = 8.dp),
                            )
                        }
                        Text(
                            folder ?: "App default (private storage)",
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(top = 2.dp),
                        )
                    }
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier
                            .height(32.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                            .clickable { folderPicker.launch(null) }
                            .padding(horizontal = 12.dp),
                    ) {
                        Text(
                            "Choose…",
                            style = MaterialTheme.typography.labelMedium,
                            color = RenzoColors.Foreground,
                        )
                    }
                }

                Spacer(Modifier.height(12.dp))
                HorizontalDivider(color = RenzoColors.Border)
                Spacer(Modifier.height(12.dp))

                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(
                                Icons.Filled.Storage,
                                contentDescription = null,
                                tint = RenzoColors.Foreground,
                                modifier = Modifier.size(16.dp),
                            )
                            Text(
                                "Auto-clean on reconnect",
                                style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                                color = RenzoColors.Foreground,
                                modifier = Modifier.padding(start = 8.dp),
                            )
                        }
                        Text(
                            "When you're back online, ask to remove trip downloads " +
                                "(never the chapter you're reading).",
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.padding(top = 2.dp),
                        )
                    }
                    Switch(
                        checked = autoPurge,
                        onCheckedChange = { value ->
                            autoPurge = value
                            store.kvSet(AUTO_PURGE_KEY, if (value) "on" else "off")
                        },
                        colors = SwitchDefaults.colors(
                            checkedThumbColor = RenzoColors.PrimaryForeground,
                            checkedTrackColor = RenzoColors.Primary,
                            uncheckedThumbColor = RenzoColors.MutedForeground,
                            uncheckedTrackColor = RenzoColors.Muted,
                        ),
                        modifier = Modifier.padding(start = 12.dp),
                    )
                }
            }
        }

        // ── List ─────────────────────────────────────────────────────────
        when {
            list == null -> item(key = "loading") {
                Text(
                    "Loading…",
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.MutedForeground,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 32.dp),
                )
            }
            list.isEmpty() -> item(key = "empty") {
                Text(
                    "No downloads yet. Open a series and tap Save offline on a chapter.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.MutedForeground,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(vertical = 48.dp),
                )
            }
            else -> items(list, key = { it.chapterKey }) { chapter ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 8.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                        .background(RenzoColors.Card.copy(alpha = 0.5f))
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            chapter.seriesTitle,
                            style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                            color = RenzoColors.Foreground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            "Ch. ${formatChapter(chapter.chapterNumber)} · " +
                                "${chapter.pageCount} pages · ${formatBytes(chapter.bytes)}",
                            style = MaterialTheme.typography.labelSmall,
                            color = RenzoColors.MutedForeground,
                            modifier = Modifier.padding(top = 2.dp),
                        )
                    }
                    Box(
                        contentAlignment = Alignment.Center,
                        modifier = Modifier
                            .size(32.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .clickable {
                                scope.launch {
                                    withContext(Dispatchers.IO) {
                                        renzoApp.offline.deleteChapter(chapter.chapterKey)
                                    }
                                    toast = "Removed — Chapter ${formatChapter(chapter.chapterNumber)} " +
                                        "deleted from device."
                                    reloadTick++
                                }
                            },
                    ) {
                        Icon(
                            Icons.Filled.Delete,
                            contentDescription = "Remove chapter ${formatChapter(chapter.chapterNumber)}",
                            tint = RenzoColors.MutedForeground,
                            modifier = Modifier.size(16.dp),
                        )
                    }
                }
            }
        }
    }
}
