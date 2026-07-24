using android.graphics;
using android.net.http;
using android.os;
using android.print;
using android.view;
using android.view.textclassifier;
using android.webkit;
using Microsoft.Extensions.Logging;
using xyz.nulldev.androidcompat;
using xyz.nulldev.androidcompat.webkit;

namespace Mihon.ExtensionsBridge.Core.Runtime
{
    /// <summary>
    /// Replaces the compat layer's WebView provider factory with one that wraps
    /// <c>KcefWebViewProvider</c> in a null-safety/diagnostic shim.
    ///
    /// Why: extensions routinely call <c>WebView.evaluateJavascript(script, null)</c>
    /// (a null result callback is explicitly allowed by the Android API). The
    /// Kotlin KCEF provider stores the callback in a ConcurrentHashMap, which
    /// throws NPE on null values — inside the extension's own runCatching, so the
    /// script silently never executes and every WebView flow (Comix et al.) hangs
    /// to its timeout. The compat DLL is prebuilt and can't be rebuilt here, but
    /// the factory hook is public, so we intercept in C#.
    /// </summary>
    public static class SafeWebViewHook
    {
        public static void Install(ILogger logger)
        {
            WebView.setProviderFactory(new SafeWebViewFactory(logger));
            logger.LogInformation("SafeWebView provider factory installed (null-callback shim + diagnostics over KCEF).");
        }

        private sealed class SafeWebViewFactory : CallableArgument
        {
            private readonly ILogger _logger;
            public SafeWebViewFactory(ILogger logger) => _logger = logger;

            public object call(object arg)
            {
                var view = (WebView)arg;
                return new SafeWebViewProvider(new KcefWebViewProvider(view), _logger);
            }
        }
    }

    /// <summary>Android allows a null callback; the KCEF provider does not. This absorbs it.</summary>
    internal sealed class NoOpValueCallback : ValueCallback
    {
        public static readonly NoOpValueCallback Instance = new();
        public void onReceiveValue(object value) { }
    }

    /// <summary>
    /// Pure delegation to <see cref="KcefWebViewProvider"/> except:
    /// evaluateJavaScript substitutes a no-op callback for null, and the page
    /// lifecycle entry points log at Debug so WebView-driven extensions can be
    /// diagnosed from the container logs.
    /// </summary>
    internal sealed class SafeWebViewProvider : WebViewProvider
    {
        private readonly KcefWebViewProvider _inner;
        private readonly ILogger _logger;

        public SafeWebViewProvider(KcefWebViewProvider inner, ILogger logger)
        {
            _inner = inner;
            _logger = logger;
        }

        public void init(java.util.Map javaScriptInterfaces, bool privateBrowsing)
        {
            _logger.LogDebug("WebView init (privateBrowsing={Private})", privateBrowsing);
            _inner.init(javaScriptInterfaces, privateBrowsing);
        }

        public void evaluateJavaScript(string script, ValueCallback resultCallback)
        {
            try
            {
                _inner.evaluateJavaScript(script, resultCallback ?? NoOpValueCallback.Instance);
            }
            catch (global::System.Exception e)
            {
                _logger.LogWarning(e, "WebView evaluateJavaScript failed ({Length} chars).", script?.Length ?? 0);
                throw;
            }
        }

        public void loadUrl(string url, java.util.Map additionalHttpHeaders)
        {
            _logger.LogDebug("WebView loadUrl {Url}", url);
            _inner.loadUrl(url, additionalHttpHeaders);
        }

        public void loadUrl(string url)
        {
            _logger.LogDebug("WebView loadUrl {Url}", url);
            _inner.loadUrl(url);
        }

        public void postUrl(string url, byte[] postData)
        {
            _logger.LogDebug("WebView postUrl {Url}", url);
            _inner.postUrl(url, postData);
        }

        public void loadData(string data, string mimeType, string encoding)
        {
            _logger.LogDebug("WebView loadData ({Length} chars, {Mime})", data?.Length ?? 0, mimeType);
            _inner.loadData(data, mimeType, encoding);
        }

        public void loadDataWithBaseURL(string baseUrl, string data, string mimeType, string encoding, string historyUrl)
        {
            _logger.LogDebug("WebView loadDataWithBaseURL base={Base} ({Length} chars)", baseUrl, data?.Length ?? 0);
            _inner.loadDataWithBaseURL(baseUrl, data, mimeType, encoding, historyUrl);
        }

        public void addJavascriptInterface(object obj, string interfaceName)
        {
            _logger.LogDebug("WebView addJavascriptInterface {Name} ({Type})", interfaceName, obj?.GetType().Name);
            _inner.addJavascriptInterface(obj, interfaceName);
        }

        public void destroy()
        {
            _logger.LogDebug("WebView destroy");
            _inner.destroy();
        }

        // ---- pure delegation below ----

        public void setHorizontalScrollbarOverlay(bool overlay) => _inner.setHorizontalScrollbarOverlay(overlay);
        public void setVerticalScrollbarOverlay(bool overlay) => _inner.setVerticalScrollbarOverlay(overlay);
        public bool overlayHorizontalScrollbar() => _inner.overlayHorizontalScrollbar();
        public bool overlayVerticalScrollbar() => _inner.overlayVerticalScrollbar();
        public int getVisibleTitleHeight() => _inner.getVisibleTitleHeight();
        public SslCertificate getCertificate() => _inner.getCertificate();
        public void setCertificate(SslCertificate certificate) => _inner.setCertificate(certificate);
        public void savePassword(string host, string username, string password) => _inner.savePassword(host, username, password);
        public void setHttpAuthUsernamePassword(string host, string realm, string username, string password) => _inner.setHttpAuthUsernamePassword(host, realm, username, password);
        public string[] getHttpAuthUsernamePassword(string host, string realm) => _inner.getHttpAuthUsernamePassword(host, realm);
        public void setNetworkAvailable(bool networkUp) => _inner.setNetworkAvailable(networkUp);
        public WebBackForwardList saveState(Bundle outState) => _inner.saveState(outState);
        public bool savePicture(Bundle b, java.io.File dest) => _inner.savePicture(b, dest);
        public bool restorePicture(Bundle b, java.io.File src) => _inner.restorePicture(b, src);
        public WebBackForwardList restoreState(Bundle inState) => _inner.restoreState(inState);
        public void saveWebArchive(string filename) => _inner.saveWebArchive(filename);
        public void saveWebArchive(string basename, bool autoname, ValueCallback callback) => _inner.saveWebArchive(basename, autoname, callback);
        public void stopLoading() => _inner.stopLoading();
        public void reload() => _inner.reload();
        public bool canGoBack() => _inner.canGoBack();
        public void goBack() => _inner.goBack();
        public bool canGoForward() => _inner.canGoForward();
        public void goForward() => _inner.goForward();
        public bool canGoBackOrForward(int steps) => _inner.canGoBackOrForward(steps);
        public void goBackOrForward(int steps) => _inner.goBackOrForward(steps);
        public bool isPrivateBrowsingEnabled() => _inner.isPrivateBrowsingEnabled();
        public bool pageUp(bool top) => _inner.pageUp(top);
        public bool pageDown(bool bottom) => _inner.pageDown(bottom);
        public void insertVisualStateCallback(long requestId, WebView.VisualStateCallback callback) => _inner.insertVisualStateCallback(requestId, callback);
        public void clearView() => _inner.clearView();
        public Picture capturePicture() => _inner.capturePicture();
        public PrintDocumentAdapter createPrintDocumentAdapter(string documentName) => _inner.createPrintDocumentAdapter(documentName);
        public float getScale() => _inner.getScale();
        public void setInitialScale(int scaleInPercent) => _inner.setInitialScale(scaleInPercent);
        public void invokeZoomPicker() => _inner.invokeZoomPicker();
        public WebView.HitTestResult getHitTestResult() => _inner.getHitTestResult();
        public void requestFocusNodeHref(Message hrefMsg) => _inner.requestFocusNodeHref(hrefMsg);
        public void requestImageRef(Message msg) => _inner.requestImageRef(msg);
        public string getUrl() => _inner.getUrl();
        public string getOriginalUrl() => _inner.getOriginalUrl();
        public string getTitle() => _inner.getTitle();
        public Bitmap getFavicon() => _inner.getFavicon();
        public string getTouchIconUrl() => _inner.getTouchIconUrl();
        public int getProgress() => _inner.getProgress();
        public int getContentHeight() => _inner.getContentHeight();
        public int getContentWidth() => _inner.getContentWidth();
        public void pauseTimers() => _inner.pauseTimers();
        public void resumeTimers() => _inner.resumeTimers();
        public void onPause() => _inner.onPause();
        public void onResume() => _inner.onResume();
        public bool isPaused() => _inner.isPaused();
        public void freeMemory() => _inner.freeMemory();
        public void clearCache(bool includeDiskFiles) => _inner.clearCache(includeDiskFiles);
        public void clearFormData() => _inner.clearFormData();
        public void clearHistory() => _inner.clearHistory();
        public void clearSslPreferences() => _inner.clearSslPreferences();
        public WebBackForwardList copyBackForwardList() => _inner.copyBackForwardList();
        public void setFindListener(WebView.FindListener listener) => _inner.setFindListener(listener);
        public void findNext(bool forward) => _inner.findNext(forward);
        public int findAll(string find) => _inner.findAll(find);
        public void findAllAsync(string find) => _inner.findAllAsync(find);
        public bool showFindDialog(string text, bool showIme) => _inner.showFindDialog(text, showIme);
        public void clearMatches() => _inner.clearMatches();
        public void documentHasImages(Message response) => _inner.documentHasImages(response);
        public void setWebViewClient(WebViewClient client) => _inner.setWebViewClient(client);
        public WebViewClient getWebViewClient() => _inner.getWebViewClient();
        public WebViewRenderProcess getWebViewRenderProcess() => _inner.getWebViewRenderProcess();
        public void setWebViewRenderProcessClient(java.util.concurrent.Executor executor, WebViewRenderProcessClient client) => _inner.setWebViewRenderProcessClient(executor, client);
        public WebViewRenderProcessClient getWebViewRenderProcessClient() => _inner.getWebViewRenderProcessClient();
        public void setDownloadListener(DownloadListener listener) => _inner.setDownloadListener(listener);
        public void setWebChromeClient(WebChromeClient client) => _inner.setWebChromeClient(client);
        public WebChromeClient getWebChromeClient() => _inner.getWebChromeClient();
        public void setPictureListener(WebView.PictureListener listener) => _inner.setPictureListener(listener);
        public void removeJavascriptInterface(string interfaceName) => _inner.removeJavascriptInterface(interfaceName);
        public WebMessagePort[] createWebMessageChannel() => _inner.createWebMessageChannel();
        public void postMessageToMainFrame(WebMessage message, android.net.Uri targetOrigin) => _inner.postMessageToMainFrame(message, targetOrigin);
        public WebSettings getSettings() => _inner.getSettings();
        public void setMapTrackballToArrowKeys(bool setMap) => _inner.setMapTrackballToArrowKeys(setMap);
        public void flingScroll(int vx, int vy) => _inner.flingScroll(vx, vy);
        public View getZoomControls() => _inner.getZoomControls();
        public bool canZoomIn() => _inner.canZoomIn();
        public bool canZoomOut() => _inner.canZoomOut();
        public bool zoomBy(float zoomFactor) => _inner.zoomBy(zoomFactor);
        public bool zoomIn() => _inner.zoomIn();
        public bool zoomOut() => _inner.zoomOut();
        public void dumpViewHierarchyWithProperties(java.io.BufferedWriter writer, int level) => _inner.dumpViewHierarchyWithProperties(writer, level);
        public View findHierarchyView(string className, int hashCode) => _inner.findHierarchyView(className, hashCode);
        public void setRendererPriorityPolicy(int rendererRequestedPriority, bool waivedWhenNotVisible) => _inner.setRendererPriorityPolicy(rendererRequestedPriority, waivedWhenNotVisible);
        public int getRendererRequestedPriority() => _inner.getRendererRequestedPriority();
        public bool getRendererPriorityWaivedWhenNotVisible() => _inner.getRendererPriorityWaivedWhenNotVisible();
        public void setTextClassifier(TextClassifier textClassifier) => _inner.setTextClassifier(textClassifier);
        public TextClassifier getTextClassifier() => _inner.getTextClassifier();
        public WebViewProvider.ViewDelegate getViewDelegate() => _inner.getViewDelegate();
        public WebViewProvider.ScrollDelegate getScrollDelegate() => _inner.getScrollDelegate();
        public void notifyFindDialogDismissed() => _inner.notifyFindDialogDismissed();
    }
}
