package app.rensaio.client

import android.annotation.SuppressLint
import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
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

    private val prefs by lazy { getSharedPreferences("rensaio", Context.MODE_PRIVATE) }

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
                    showServerPanel(getString(R.string.error_lost))
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
            val server = candidates.firstOrNull { isRensaioServer(it) }
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
    private fun isRensaioServer(base: String): Boolean {
        return try {
            val conn = URL("$base/api/system/info/public").openConnection() as HttpURLConnection
            conn.connectTimeout = 10_000
            conn.readTimeout = 10_000
            try {
                if (conn.responseCode != 200) return false
                val body = conn.inputStream.bufferedReader().use { it.readText() }
                JSONObject(body).optString("product").equals("Rensaio", ignoreCase = true)
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
        executor.shutdownNow()
        super.onDestroy()
    }
}
