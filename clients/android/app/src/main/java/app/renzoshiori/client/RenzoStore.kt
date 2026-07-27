package app.renzoshiori.client

import android.content.Context
import android.content.SharedPreferences
import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import org.json.JSONObject
import java.io.File

/**
 * Shared offline storage — file/SAF writes, the manifest (JSON, matching the web
 * app's OfflineManifest v2), a small KV store, and the chosen download folder.
 * Used by both the JS bridge ([RenzoNativeBridge]) and the native downloader
 * ([RenzoDownloadService]); both share the same SharedPreferences.
 */
class RenzoStore(private val context: Context) {
    private val prefs: SharedPreferences =
        context.getSharedPreferences("renzo_offline", Context.MODE_PRIVATE)

    private val manifestPrefKey = "kv_renzo.offline.manifest.v1"

    private val defaultRoot: File
        get() = context.getExternalFilesDir("offline") ?: File(context.filesDir, "offline")

    private fun treeUri(): Uri? = prefs.getString("downloadTree", null)?.let { Uri.parse(it) }

    private fun segments(relPath: String): List<String> =
        relPath.replace("\\", "/").split("/").filter { it.isNotEmpty() && it != "." && it != ".." }

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

    private fun docKey(relPath: String) = "doc_$relPath"
    private fun rememberDoc(relPath: String, uri: Uri) = prefs.edit().putString(docKey(relPath), uri.toString()).apply()
    private fun rememberedDoc(relPath: String): Uri? = prefs.getString(docKey(relPath), null)?.let { Uri.parse(it) }

    fun writeFile(relPath: String, bytes: ByteArray) {
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

    fun readFile(relPath: String): ByteArray? =
        if (treeUri() != null) {
            rememberedDoc(relPath)?.let { context.contentResolver.openInputStream(it)?.use { s -> s.readBytes() } }
        } else {
            File(defaultRoot, segments(relPath).joinToString("/")).takeIf { it.exists() }?.readBytes()
        }

    fun exists(relPath: String): Boolean =
        if (treeUri() != null) rememberedDoc(relPath) != null
        else File(defaultRoot, segments(relPath).joinToString("/")).exists()

    fun deletePath(relPath: String) {
        if (treeUri() != null) {
            val prefix = docKey(relPath)
            val e = prefs.edit()
            for ((k, v) in prefs.all) {
                if (k == prefix || k.startsWith("$prefix/")) {
                    try { DocumentFile.fromSingleUri(context, Uri.parse(v as String))?.delete() } catch (_: Exception) {}
                    e.remove(k)
                }
            }
            e.apply()
            safDir(false, segments(relPath))?.delete()
        } else {
            File(defaultRoot, segments(relPath).joinToString("/")).deleteRecursively()
        }
    }

    fun kvGet(key: String): String? = prefs.getString("kv_$key", null)
    fun kvSet(key: String, value: String) = prefs.edit().putString("kv_$key", value).apply()

    @Synchronized
    fun getManifest(): JSONObject {
        val raw = prefs.getString(manifestPrefKey, null)
            ?: return JSONObject().put("version", 2).put("series", JSONObject()).put("chapters", JSONObject())
        return try {
            val m = JSONObject(raw)
            if (!m.has("series")) m.put("series", JSONObject())
            if (!m.has("chapters")) m.put("chapters", JSONObject())
            m.put("version", 2)
        } catch (_: Exception) {
            JSONObject().put("version", 2).put("series", JSONObject()).put("chapters", JSONObject())
        }
    }

    @Synchronized
    fun setManifest(m: JSONObject) = prefs.edit().putString(manifestPrefKey, m.toString()).apply()

    fun hasChapter(chapterKey: String): Boolean = getManifest().optJSONObject("chapters")?.has(chapterKey) == true

    fun hasDownloads(): Boolean = (getManifest().optJSONObject("chapters")?.length() ?: 0) > 0

    fun folderLabel(): String? = treeUri()?.let {
        try { DocumentFile.fromTreeUri(context, it)?.name ?: Uri.decode(it.lastPathSegment) } catch (_: Exception) { null }
    }

    fun setFolder(uri: Uri?) = prefs.edit().apply {
        if (uri != null) putString("downloadTree", uri.toString()) else remove("downloadTree")
        apply()
    }
}
