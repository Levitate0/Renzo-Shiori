package app.renzoshiori.client

import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.Build
import android.util.Base64
import android.webkit.JavascriptInterface

/**
 * Native bridge exposed to the web UI as `window.__RenzoAndroid`. File/KV/network
 * primitives are delegated to [RenzoStore] (shared with the native downloader).
 * Downloads are handed off to [RenzoDownloadService] via [enqueueDownload], so
 * they run natively and survive the app being backgrounded.
 *
 * Security note: the app only loads a verified Renzo Shiori server (external
 * links open in the system browser), so only the user's own server UI reaches it.
 */
class RenzoNativeBridge(
    private val context: Context,
    private val onPickFolder: () -> Unit,
    private val onReconnect: () -> Unit,
) {
    private val store = RenzoStore(context)

    // ── files (base64 across the JS boundary) ─────────────────────────────────
    @JavascriptInterface
    fun writeFileB64(relPath: String, b64: String) = store.writeFile(relPath, Base64.decode(b64, Base64.DEFAULT))

    @JavascriptInterface
    fun readFileB64(relPath: String): String {
        val bytes = store.readFile(relPath) ?: return ""
        return Base64.encodeToString(bytes, Base64.NO_WRAP)
    }

    @JavascriptInterface
    fun deletePath(relPath: String) = store.deletePath(relPath)

    @JavascriptInterface
    fun exists(relPath: String): Boolean = store.exists(relPath)

    // ── KV ─────────────────────────────────────────────────────────────────────
    @JavascriptInterface
    fun kvGet(key: String): String? = store.kvGet(key)

    @JavascriptInterface
    fun kvSet(key: String, value: String) = store.kvSet(key, value)

    // ── network ──────────────────────────────────────────────────────────────
    @JavascriptInterface
    fun isOnline(): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return false
        val net = cm.activeNetwork ?: return false
        val caps = cm.getNetworkCapabilities(net) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }

    // ── native background download ───────────────────────────────────────────
    /** Hand a download job (JSON: baseUrl, token, series, chapters[]) to the
     *  foreground download service so it runs natively in the background. */
    @JavascriptInterface
    fun enqueueDownload(payload: String) {
        val i = Intent(context, RenzoDownloadService::class.java)
            .setAction(RenzoDownloadService.ACTION_ENQUEUE)
            .putExtra(RenzoDownloadService.EXTRA_PAYLOAD, payload)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(i)
            else context.startService(i)
        } catch (_: Exception) {
        }
    }

    // ── folder selection ───────────────────────────────────────────────────────
    @JavascriptInterface
    fun pickFolder() = onPickFolder()

    @JavascriptInterface
    fun getFolder(): String? = store.folderLabel()

    /** Called by the Activity after the folder picker returns (uri = null clears it). */
    fun setFolder(uri: Uri?) = store.setFolder(uri)

    // ── offline reader ↔ shell ───────────────────────────────────────────────
    @JavascriptInterface
    fun reconnect() = onReconnect()

    fun hasDownloads(): Boolean = store.hasDownloads()
}
