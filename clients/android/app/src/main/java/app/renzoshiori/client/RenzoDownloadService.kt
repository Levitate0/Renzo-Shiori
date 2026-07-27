package app.renzoshiori.client

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Fully-native offline downloader. Runs as a foreground service so it is
 * independent of the WebView (which Android suspends in the background). Fetches
 * page images over HTTP (Bearer auth — valid for hours), writes them via
 * [RenzoStore], and updates the manifest — so downloads continue when the app is
 * tabbed out or killed. Progress is broadcast back to the app for the live UI.
 */
class RenzoDownloadService : Service() {
    companion object {
        const val CHANNEL_ID = "renzo_downloads"
        const val NOTIF_ID = 4711
        const val ACTION_ENQUEUE = "enqueue"
        const val ACTION_STOP = "stop"
        const val EXTRA_PAYLOAD = "payload"
        const val BROADCAST = "app.renzoshiori.client.DOWNLOAD"
        /** Concurrent page fetches per chapter. */
        const val PAGE_CONCURRENCY = 5
    }

    private lateinit var store: RenzoStore
    private val queue = ConcurrentLinkedQueue<JSONObject>()
    private val executor = Executors.newSingleThreadExecutor()
    private val running = AtomicBoolean(false)
    private val writeLock = Any()

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        store = RenzoStore(applicationContext)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            queue.clear()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        intent?.getStringExtra(EXTRA_PAYLOAD)?.let {
            try { queue.add(JSONObject(it)) } catch (_: Exception) {}
        }
        ensureChannel()
        startForeground(NOTIF_ID, notification("Preparing offline download…", 0, 0))
        if (running.compareAndSet(false, true)) {
            executor.execute { runQueue() }
        }
        return START_STICKY
    }

    private fun runQueue() {
        try {
            while (true) {
                val job = queue.poll() ?: break
                try { downloadJob(job) } catch (_: Exception) {}
            }
        } finally {
            running.set(false)
            broadcast("idle", null, null, 0.0, 0, 0)
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun downloadJob(job: JSONObject) {
        val baseUrl = job.getString("baseUrl").trimEnd('/')
        val token = job.getString("token")
        val series = job.getJSONObject("series")
        val seriesId = series.getString("seriesId")
        val title = series.optString("title")
        ensureSeriesMeta(series, baseUrl, token)

        val chapters = job.getJSONArray("chapters")
        val total = chapters.length()
        for (i in 0 until total) {
            if (queueStopped()) return
            val ch = chapters.getJSONObject(i)
            val chapterKey = ch.getString("chapterKey")
            val chapterNumber = ch.optDouble("chapterNumber", 0.0)
            updateNotification("Saving $title · ${i + 1}/$total", i, total)
            broadcast("downloading", seriesId, chapterKey, chapterNumber, i, total)
            if (!store.hasChapter(chapterKey)) {
                try {
                    downloadChapter(ch, seriesId, title, baseUrl, token)
                } catch (_: Exception) { /* skip a bad chapter, keep the trip going */ }
            }
            broadcast("saved", seriesId, chapterKey, chapterNumber, i + 1, total)
        }
    }

    private fun downloadChapter(ch: JSONObject, seriesId: String, title: String, baseUrl: String, token: String) {
        val chapterKey = ch.getString("chapterKey")
        val chapterNumber = ch.optDouble("chapterNumber", 0.0)
        val pagePaths = ch.getJSONArray("pagePaths")
        val n = pagePaths.length()
        val dir = "offline/${sanitize(chapterKey)}"

        // Fetch pages in parallel (network is the bottleneck); serialize only the
        // writes so SAF stays safe. Results are placed by page index, so page
        // order is preserved regardless of completion order.
        val rels = arrayOfNulls<String>(n)
        val sizes = LongArray(n)
        val pool = Executors.newFixedThreadPool(minOf(PAGE_CONCURRENCY, maxOf(1, n)))
        try {
            val tasks = (0 until n).map { p ->
                pool.submit {
                    if (queueStopped()) return@submit
                    val res = httpGet(resolve(baseUrl, pagePaths.getString(p)), token) ?: return@submit
                    val rel = "$dir/${p.toString().padStart(4, '0')}.${ext(res.second)}"
                    synchronized(writeLock) { store.writeFile(rel, res.first) }
                    rels[p] = rel
                    sizes[p] = res.first.size.toLong()
                }
            }
            tasks.forEach { try { it.get() } catch (_: Exception) {} }
        } finally {
            pool.shutdown()
        }

        val savedPaths = JSONArray()
        var bytes = 0L
        for (p in 0 until n) {
            val rel = rels[p] ?: continue
            savedPaths.put(rel)
            bytes += sizes[p]
        }
        if (savedPaths.length() == 0) return
        val entry = JSONObject()
            .put("seriesId", seriesId)
            .put("chapterKey", chapterKey)
            .put("chapterNumber", chapterNumber)
            .put("seriesTitle", title)
            .put("pageCount", savedPaths.length())
            .put("pagePaths", savedPaths)
            .put("bytes", bytes)
            .put("savedAt", System.currentTimeMillis())
        synchronized(store) {
            val m = store.getManifest()
            m.getJSONObject("chapters").put(chapterKey, entry)
            store.setManifest(m)
        }
    }

    private fun ensureSeriesMeta(series: JSONObject, baseUrl: String, token: String) {
        val seriesId = series.getString("seriesId")
        synchronized(store) {
            val m = store.getManifest()
            val seriesMap = m.getJSONObject("series")
            if (seriesMap.optJSONObject(seriesId)?.optString("coverPath")?.isNotEmpty() == true) return
        }
        var coverPath: String? = null
        val coverSrc = series.optString("coverPath")
        if (coverSrc.isNotEmpty()) {
            val (data, ct) = httpGet(resolve(baseUrl, coverSrc), token) ?: (null to null)
            if (data != null) {
                coverPath = "offline/covers/${sanitize(seriesId)}.${ext(ct)}"
                store.writeFile(coverPath, data)
            }
        }
        val entry = JSONObject()
            .put("seriesId", seriesId)
            .put("title", series.optString("title"))
            .put("description", series.optString("description"))
            .put("author", series.optString("author"))
        if (coverPath != null) entry.put("coverPath", coverPath)
        synchronized(store) {
            val m = store.getManifest()
            m.getJSONObject("series").put(seriesId, entry)
            store.setManifest(m)
        }
    }

    private fun queueStopped(): Boolean = !running.get()

    private fun resolve(baseUrl: String, path: String): String =
        if (path.startsWith("http", true)) path else baseUrl + path

    private fun httpGet(urlString: String, token: String): Pair<ByteArray, String?>? {
        return try {
            val conn = URL(urlString).openConnection() as HttpURLConnection
            conn.connectTimeout = 20_000
            conn.readTimeout = 30_000
            conn.setRequestProperty("Authorization", "Bearer $token")
            try {
                if (conn.responseCode != 200) return null
                val ct = conn.contentType
                conn.inputStream.use { it.readBytes() } to ct
            } finally {
                conn.disconnect()
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun ext(ct: String?): String {
        val c = ct?.lowercase() ?: return "jpg"
        return when {
            c.contains("png") -> "png"
            c.contains("webp") -> "webp"
            c.contains("avif") -> "avif"
            c.contains("gif") -> "gif"
            else -> "jpg"
        }
    }

    private fun sanitize(key: String): String = key.replace(Regex("[^a-zA-Z0-9._-]"), "_")

    private fun broadcast(state: String, seriesId: String?, chapterKey: String?, chapterNumber: Double, done: Int, total: Int) {
        val i = Intent(BROADCAST).setPackage(packageName)
            .putExtra("state", state)
            .putExtra("seriesId", seriesId)
            .putExtra("chapterKey", chapterKey)
            .putExtra("chapterNumber", chapterNumber)
            .putExtra("done", done)
            .putExtra("total", total)
        sendBroadcast(i)
    }

    // ── notification ─────────────────────────────────────────────────────────
    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (nm.getNotificationChannel(CHANNEL_ID) == null) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, "Offline downloads", NotificationManager.IMPORTANCE_LOW)
                )
            }
        }
    }

    private fun updateNotification(text: String, done: Int, total: Int) {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.notify(NOTIF_ID, notification(text, done, total))
    }

    private fun notification(text: String, done: Int, total: Int): Notification {
        val open = packageManager.getLaunchIntentForPackage(packageName)?.let {
            PendingIntent.getActivity(this, 0, it, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        }
        val b = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Renzo Shiori")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .setPriority(NotificationCompat.PRIORITY_LOW)
        if (total > 0) b.setProgress(total, done, false)
        return b.build()
    }
}
