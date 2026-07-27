# Keep @JavascriptInterface methods. The web UI calls them by name via
# window.__RenzoAndroid, so R8 must not rename or remove them — otherwise the
# offline bridge silently breaks in the minified release build.
-keepclassmembers class * {
    @android.webkit.JavascriptInterface <methods>;
}
