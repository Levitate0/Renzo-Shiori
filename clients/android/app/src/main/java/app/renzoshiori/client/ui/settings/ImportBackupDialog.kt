package app.renzoshiori.client.ui.settings

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.UploadFile
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.RenzoApp
import app.renzoshiori.client.data.model.BackupImportResultDto
import app.renzoshiori.client.data.network.AccountApi
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.toRequestBody

private const val MAX_BACKUP_BYTES = 200L * 1024 * 1024

/**
 * Imports read progress / completed chapters / bookmarks from a
 * Tachiyomi/Suwayomi backup — transliterated from
 * RenzoFrontend/src/components/comp/users/import-backup-dialog.tsx.
 *
 * The web's hidden file input becomes the SAF document picker. `.tachibk` has
 * no registered MIME type on Android, so the picker accepts any type and the
 * server does the real validation (it answers "Could not parse the backup — is
 * it a .tachibk/.proto.gz file?" for anything else).
 */
@Composable
fun ImportBackupDialog(onDismiss: () -> Unit) {
    val app = LocalContext.current.applicationContext as RenzoApp
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var pending by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf<BackupImportResultDto?>(null) }
    var errorText by remember { mutableStateOf<String?>(null) }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri != null) {
            pending = true
            errorText = null
            result = null
            scope.launch {
                val outcome = withContext(Dispatchers.IO) {
                    runCatching {
                        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
                            ?: throw IllegalStateException("Couldn't read that file")
                        if (bytes.size > MAX_BACKUP_BYTES) {
                            throw IllegalStateException("Backup is larger than 200MB")
                        }
                        val name = uri.lastPathSegment?.substringAfterLast('/') ?: "backup.tachibk"
                        val part = MultipartBody.Part.createFormData(
                            "file",
                            name,
                            bytes.toRequestBody("application/octet-stream".toMediaType()),
                        )
                        app.network.currentServiceOf<AccountApi>()?.importBackup(part)
                    }
                }
                outcome
                    .onSuccess { result = it }
                    .onFailure { errorText = it.apiMessage("Import failed") }
                pending = false
            }
        }
    }

    RenzoDialog(
        onDismiss = onDismiss,
        title = "Import Suwayomi Backup",
        description = "Sync read progress, completed chapters, and bookmarks from a .tachibk " +
            "backup into your account.",
    ) {
        errorText?.let { ErrorBox(it) }

        val imported = result
        if (imported != null) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(RenzoColors.Muted)
                    .padding(12.dp),
            ) {
                Text(
                    "${imported.matchedSeries} of ${imported.backupSeries} backup series matched your library.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.Foreground,
                )
                Text(
                    "${imported.updatedChapters} chapter read-states imported" +
                        if (imported.bookmarks > 0) ", ${imported.bookmarks} bookmarks." else ".",
                    style = MaterialTheme.typography.bodyMedium,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
            if (imported.unmatched.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                Text(
                    "${imported.unmatched.size} series with progress weren't found in your library",
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                )
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = 160.dp)
                        .verticalScroll(rememberScrollState())
                        .padding(top = 4.dp, start = 8.dp),
                ) {
                    imported.unmatched.forEach { title ->
                        Text(
                            "• $title",
                            style = MaterialTheme.typography.bodySmall,
                            color = RenzoColors.MutedForeground,
                        )
                    }
                }
            }
            Spacer(Modifier.height(12.dp))
            RenzoButton(
                text = "Import another file",
                variant = "outline",
                modifier = Modifier.fillMaxWidth(),
                onClick = { result = null },
            )
        } else {
            RenzoButton(
                text = if (pending) "Importing…" else "Choose backup file",
                icon = Icons.Filled.UploadFile,
                busy = pending,
                modifier = Modifier.fillMaxWidth(),
                onClick = { picker.launch(arrayOf("*/*")) },
            )
            Hint(
                "In Suwayomi: Settings → Backup → Create backup. Matching is by title; chapters " +
                    "are matched by number. Nothing is deleted, and chapters you've already read " +
                    "here stay read.",
            )
        }
    }
}
