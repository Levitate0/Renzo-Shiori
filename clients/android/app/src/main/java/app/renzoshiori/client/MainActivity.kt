package app.renzoshiori.client

import android.annotation.SuppressLint
import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.view.View
import android.view.inputmethod.EditorInfo
import android.webkit.CookieManager
import android.webkit.URLUtil
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.EditText
import android.widget.ProgressBar
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

class MainActivity : AppCompatActivity() {

    private lateinit var webView: WebView
    private lateinit var serverPanel: View
    private lateinit var addressInput: EditText
    private lateinit var connectButton: Button
    private lateinit var errorText: TextView
    private lateinit var progress: ProgressBar

    private val executor = Executors.newSingleThreadExecutor()
    private var serverUrl: String? = null

    private var fileChooserCallback: ValueCallback<Array<Uri>>? = null
    private val fileChooserLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            fileChooserCallback?.onReceiveValue(
                WebChromeClient.FileChooserParams.parseResult(result.resultCode, result.data)
            )
            fileChooserCallback = null
        }

    private val prefs by lazy { getSharedPreferences("renzo", Context.MODE_PRIVATE) }

    private lateinit var nativeBridge: RenzoNativeBridge

    // User's offline download folder (Storage Access Framework). Result is
    // reported back to the web UI via a `renzo:folderpicked` event.
    private val folderPicker =
        registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
            var label: String? = null
            if (uri != null) {
                try {
                    contentResolver.takePersistableUriPermission(
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                    )
                } catch (_: Exception) {
                }
                nativeBridge.setFolder(uri)
                label = nativeBridge.getFolder()
            }
            val labelJs = if (label != null) JSONObject.quote(label) else "null"
            webView.evaluateJavascript(
                "window.dispatchEvent(new CustomEvent('renzo:folderpicked',{detail:{label:$labelJs}}))",
                null
            )
        }

    private val connectivityManager by lazy {
        getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    }
    private val netCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = emitNet(true)
        override fun onLost(network: Network) = emitNet(false)
    }

    private fun emitNet(online: Boolean) {
        runOnUiThread {
            if (::webView.isInitialized && webView.visibility == View.VISIBLE) {
                webView.evaluateJavascript(
                    "window.dispatchEvent(new CustomEvent('renzo:netchange',{detail:{online:$online}}))",
                    null
                )
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        webView = findViewById(R.id.webView)
        serverPanel = findViewById(R.id.serverPanel)
        addressInput = findViewById(R.id.addressInput)
        connectButton = findViewById(R.id.connectButton)
        errorText = findViewById(R.id.errorText)
        progress = findViewById(R.id.progress)

        setupWebView()

        try {
            connectivityManager.registerDefaultNetworkCallback(netCallback)
        } catch (_: Exception) {
        }

        connectButton.setOnClickListener { connect(addressInput.text.toString()) }
        addressInput.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_GO) {
                connect(addressInput.text.toString()); true
            } else false
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (webView.visibility == View.VISIBLE && webView.canGoBack()) {
                    webView.goBack()
                } else if (webView.visibility == View.VISIBLE) {
                    showExitDialog()
                } else {
                    finish()
                }
            }
        })

        val saved = prefs.getString("serverUrl", null)
        if (saved != null) {
            addressInput.setText(saved)
            connect(saved)
        }
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun setupWebView() {
        webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            databaseEnabled = true
            loadWithOverviewMode = true
            useWideViewPort = true
            mediaPlaybackRequiresUserGesture = false
        }
        // Persist cookies (incl. the httpOnly refresh token) so login survives restarts.
        CookieManager.getInstance().setAcceptCookie(true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(webView, false)

        webView.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val target = request.url
                val server = serverUrl?.let { Uri.parse(it) } ?: return false
                // Same-server links stay in the app; external links open in the browser.
                return if (target.host.equals(server.host, ignoreCase = true)) {
                    false
                } else {
                    try {
                        startActivity(Intent(Intent.ACTION_VIEW, target))
                    } catch (_: Exception) {
                    }
                    true
                }
            }

            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (request.isForMainFrame) {
                    // Server unreachable: drop into the bundled offline reader if there
                    // are downloads, otherwise show the connect panel.
                    if (::nativeBridge.isInitialized && nativeBridge.hasDownloads()) {
                        loadOfflineReader()
                    } else {
                        showServerPanel(getString(R.string.error_lost))
                    }
                }
            }

            override fun onPageStarted(view: WebView, url: String?, favicon: Bitmap?) {
                CookieManager.getInstance().flush()
            }
        }

        webView.webChromeClient = object : WebChromeClient() {
            override fun onShowFileChooser(
                view: WebView,
                callback: ValueCallback<Array<Uri>>,
                params: FileChooserParams
            ): Boolean {
                fileChooserCallback?.onReceiveValue(null)
                fileChooserCallback = callback
                return try {
                    fileChooserLauncher.launch(params.createIntent())
                    true
                } catch (_: Exception) {
                    fileChooserCallback = null
                    false
                }
            }
        }

        // Offline bridge — window.__RenzoAndroid. Only the trusted server UI runs
        // in this WebView (external links open in the system browser).
        nativeBridge = RenzoNativeBridge(
            this,
            onPickFolder = { folderPicker.launch(null) },
            onReconnect = { runOnUiThread { reconnectToServer() } },
        )
        webView.addJavascriptInterface(nativeBridge, "__RenzoAndroid")

        webView.setDownloadListener { url, userAgent, contentDisposition, mimeType, _ ->
            try {
                val request = DownloadManager.Request(Uri.parse(url)).apply {
                    setMimeType(mimeType)
                    addRequestHeader("Cookie", CookieManager.getInstance().getCookie(url))
                    addRequestHeader("User-Agent", userAgent)
                    setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                    setDestinationInExternalPublicDir(
                        Environment.DIRECTORY_DOWNLOADS,
                        URLUtil.guessFileName(url, contentDisposition, mimeType)
                    )
                }
                (getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager).enqueue(request)
            } catch (_: Exception) {
            }
        }
    }

    private fun connect(rawInput: String) {
        val input = rawInput.trim().trimEnd('/')
        if (input.isEmpty()) {
            showError(getString(R.string.error_empty))
            return
        }
        setBusy(true)
        executor.execute {
            val candidates =
                if (input.startsWith("http://", true) || input.startsWith("https://", true)) listOf(input)
                else listOf("https://$input", "http://$input")
            val server = candidates.firstOrNull { isRenzoServer(it) }
            runOnUiThread {
                setBusy(false)
                if (server == null) {
                    showError(getString(R.string.error_unreachable))
                } else {
                    serverUrl = server
                    prefs.edit().putString("serverUrl", server).apply()
                    errorText.visibility = View.GONE
                    serverPanel.visibility = View.GONE
                    webView.visibility = View.VISIBLE
                    webView.loadUrl(server)
                }
            }
        }
    }

    /** Probes {base}/api/system/info/public — same discovery endpoint the web client uses. */
    private fun isRenzoServer(base: String): Boolean {
        return try {
            val conn = URL("$base/api/system/info/public").openConnection() as HttpURLConnection
            conn.connectTimeout = 10_000
            conn.readTimeout = 10_000
            try {
                if (conn.responseCode != 200) return false
                val body = conn.inputStream.bufferedReader().use { it.readText() }
                // Accept the current brand plus the legacy handshake ids (the server
                // reported "Renzo"/"Rensaio" as product for older clients).
                val product = JSONObject(body).optString("product")
                product.equals("Renzo Shiori", ignoreCase = true) ||
                    product.equals("Renzo", ignoreCase = true) || product.equals("Rensaio", ignoreCase = true)
            } finally {
                conn.disconnect()
            }
        } catch (_: Exception) {
            false
        }
    }

    private fun showExitDialog() {
        AlertDialog.Builder(this)
            .setTitle(R.string.exit_dialog_title)
            .setPositiveButton(R.string.exit_dialog_exit) { _, _ -> finish() }
            .setNeutralButton(R.string.exit_dialog_change_server) { _, _ -> showServerPanel(null) }
            .setNegativeButton(R.string.exit_dialog_cancel, null)
            .show()
    }

    /** Load the bundled offline reader (reads downloads via window.__RenzoAndroid). */
    private fun loadOfflineReader() {
        serverPanel.visibility = View.GONE
        errorText.visibility = View.GONE
        webView.visibility = View.VISIBLE
        webView.loadUrl("file:///android_asset/offline/index.html")
    }

    /** From the offline reader's "Reconnect" button: retry the saved server. */
    private fun reconnectToServer() {
        val saved = serverUrl ?: prefs.getString("serverUrl", null)
        if (saved != null) connect(saved) else showServerPanel(null)
    }

    private fun showServerPanel(error: String?) {
        webView.visibility = View.GONE
        serverPanel.visibility = View.VISIBLE
        addressInput.setText(serverUrl ?: prefs.getString("serverUrl", "") ?: "")
        if (error != null) showError(error) else errorText.visibility = View.GONE
    }

    private fun showError(message: String) {
        errorText.text = message
        errorText.visibility = View.VISIBLE
    }

    private fun setBusy(busy: Boolean) {
        connectButton.isEnabled = !busy
        addressInput.isEnabled = !busy
        progress.visibility = if (busy) View.VISIBLE else View.GONE
        if (busy) errorText.visibility = View.GONE
    }

    override fun onPause() {
        super.onPause()
        CookieManager.getInstance().flush()
    }

    override fun onDestroy() {
        try {
            connectivityManager.unregisterNetworkCallback(netCallback)
        } catch (_: Exception) {
        }
        executor.shutdownNow()
        super.onDestroy()
    }
}
