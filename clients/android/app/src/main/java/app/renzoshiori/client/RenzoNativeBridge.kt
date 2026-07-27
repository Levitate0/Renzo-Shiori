package app.renzoshiori.client

import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.os.Build
import android.net.NetworkCapabilities
import android.net.Uri
import android.util.Base64
import android.webkit.JavascriptInterface
import androidx.documentfile.provider.DocumentFile
import org.json.JSONObject
import java.io.File

/**
 * Native file / KV / network / storage bridge, exposed to the web UI as
 * `window.__RenzoAndroid`. The frontend adapter (lib/native/adapters.ts) wraps
 * these synchronous, string-only methods into the async NativePrimitives
 * contract; binary is carried as base64 across the @JavascriptInterface boundary.
 *
 * Storage backends:
 *  - default: app-external private dir (getExternalFilesDir — a real folder, no
 *    runtime permission, auto-removed on uninstall).
 *  - chosen folder: a Storage Access Framework tree the user picked. Files are
 *    created there, and each file's exact document Uri is remembered in prefs
 *    (keyed by its logical relPath) so reads/deletes never depend on SAF's
 *    filename (which it may mangle) — we always use the real Uri.
 *
 * Security note: addJavascriptInterface exposes this to whatever the WebView
 * loads. The app only loads a verified Renzo Shiori server (external links open
 * in the system browser), so only the user's own server UI can reach it.
 */
class RenzoNativeBridge(
    private val context: Context,
    private val onPickFolder: () -> Unit,
    private val onReconnect: () -> Unit,
) {
    private val prefs: SharedPreferences =
        context.getSharedPreferences("renzo_offline", Context.MODE_PRIVATE)

    private val defaultRoot: File
        get() = context.getExternalFilesDir("offline") ?: File(context.filesDir, "offline")

    private fun treeUri(): Uri? = prefs.getString("downloadTree", null)?.let { Uri.parse(it) }

    private fun segments(relPath: String): List<String> =
        relPath.replace("\\", "/").split("/").filter { it.isNotEmpty() && it != "." && it != ".." }

    // ── SAF helpers ──────────────────────────────────────────────────────────
    /** Walk/create the directory chain under the chosen tree (kept under a RenzoShiori/ container). */
    private fun safDir(create: Boolean, dirSegs: List<String>): DocumentFile? {
        val tree = treeUri() ?: return null
        var dir = DocumentFile.fromTreeUri(context, tree) ?: return null
        for (name in listOf("RenzoShiori") + dirSegs) {
            val next = dir.findFile(name)
            dir = when {
                next != null && next.isDirectory -> next
                create -> dir.createDirectory(name) ?: return null
                else -> return null
            }
        }
        return dir
    }

    private fun docKey(relPath: String) = "doc_" + relPath
    private fun rememberDoc(relPath: String, uri: Uri) = prefs.edit().putString(docKey(relPath), uri.toString()).apply()
    private fun rememberedDoc(relPath: String): Uri? = prefs.getString(docKey(relPath), null)?.let { Uri.parse(it) }

    // ── file ops ─────────────────────────────────────────────────────────────
    @JavascriptInterface
    fun writeFileB64(relPath: String, b64: String) {
        val bytes = Base64.decode(b64, Base64.DEFAULT)
        if (treeUri() != null) {
            val segs = segments(relPath)
            val parent = safDir(true, segs.dropLast(1)) ?: return
            val name = segs.last()
            val doc = parent.findFile(name) ?: parent.createFile("application/octet-stream", name) ?: return
            context.contentResolver.openOutputStream(doc.uri, "wt")?.use { it.write(bytes) }
            rememberDoc(relPath, doc.uri)
        } else {
            val f = File(defaultRoot, segments(relPath).joinToString("/"))
            f.parentFile?.mkdirs()
            f.writeBytes(bytes)
        }
    }

    @JavascriptInterface
    fun readFileB64(relPath: String): String {
        val bytes: ByteArray? = if (treeUri() != null) {
            rememberedDoc(relPath)?.let { uri ->
                context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
            }
        } else {
            File(defaultRoot, segments(relPath).joinToString("/")).takeIf { it.exists() }?.readBytes()
        }
        return if (bytes == null) "" else Base64.encodeToString(bytes, Base64.NO_WRAP)
    }

    @JavascriptInterface
    fun deletePath(relPath: String) {
        if (treeUri() != null) {
            val prefix = docKey(relPath)
            val editor = prefs.edit()
            // relPath may be a single file or a directory prefix; remove every mapped doc under it.
            for ((k, v) in prefs.all) {
                if (k == prefix || k.startsWith("$prefix/")) {
                    try {
                        DocumentFile.fromSingleUri(context, Uri.parse(v as String))?.delete()
                    } catch (_: Exception) {}
                    editor.remove(k)
                }
            }
            editor.apply()
            // Best-effort remove the now-empty directory.
            safDir(false, segments(relPath))?.delete()
        } else {
            File(defaultRoot, segments(relPath).joinToString("/")).deleteRecursively()
        }
    }

    @JavascriptInterface
    fun exists(relPath: String): Boolean =
        if (treeUri() != null) rememberedDoc(relPath) != null
        else File(defaultRoot, segments(relPath).joinToString("/")).exists()

    // ── KV ───────────────────────────────────────────────────────────────────
    @JavascriptInterface
    fun kvGet(key: String): String? = prefs.getString("kv_$key", null)

    @JavascriptInterface
    fun kvSet(key: String, value: String) {
        prefs.edit().putString("kv_$key", value).apply()
    }

    // ── network ────────────────────────────────────────────────────────────────
    @JavascriptInterface
    fun isOnline(): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return false
        val net = cm.activeNetwork ?: return false
        val caps = cm.getNetworkCapabilities(net) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }

    // ── background download (foreground service) ─────────────────────────────
    /** Show/refresh the ongoing-download notification and hold foreground
     *  importance so the JS download keeps running when the app is tabbed out. */
    @JavascriptInterface
    fun startDownloadService(text: String) {
        val i = Intent(context, RenzoDownloadService::class.java)
            .setAction(RenzoDownloadService.ACTION_START)
            .putExtra(RenzoDownloadService.EXTRA_TEXT, text)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(i)
            else context.startService(i)
        } catch (_: Exception) {
        }
    }

    /** Update the notification text (re-issues start). */
    @JavascriptInterface
    fun updateDownloadService(text: String) = startDownloadService(text)

    @JavascriptInterface
    fun stopDownloadService() {
        try {
            context.startService(
                Intent(context, RenzoDownloadService::class.java).setAction(RenzoDownloadService.ACTION_STOP)
            )
        } catch (_: Exception) {
        }
    }

    // ── folder selection ───────────────────────────────────────────────────────
    @JavascriptInterface
    fun pickFolder() = onPickFolder()

    @JavascriptInterface
    fun getFolder(): String? {
        val tree = treeUri() ?: return null
        return try {
            DocumentFile.fromTreeUri(context, tree)?.name ?: Uri.decode(tree.lastPathSegment)
        } catch (_: Exception) {
            null
        }
    }

    // ── offline reader ↔ shell ───────────────────────────────────────────────
    /** From the bundled offline reader: try to reach the server again. */
    @JavascriptInterface
    fun reconnect() = onReconnect()

    /** Kotlin-side: are there any downloaded chapters? (used to decide whether to
     *  drop into the offline reader when the server is unreachable). */
    fun hasDownloads(): Boolean {
        val raw = prefs.getString("kv_renzo.offline.manifest.v1", null) ?: return false
        return try {
            val chapters = JSONObject(raw).optJSONObject("chapters")
            chapters != null && chapters.length() > 0
        } catch (_: Exception) {
            false
        }
    }

    /** Called by the Activity after the folder picker returns (uri = null clears it). */
    fun setFolder(uri: Uri?) {
        prefs.edit().apply {
            if (uri != null) putString("downloadTree", uri.toString()) else remove("downloadTree")
            apply()
        }
    }
}
