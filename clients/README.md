# Rensaiō native clients

Thin native shells around the Rensaiō web UI — the same approach as Jellyfin Media Player.
Both start with a Jellyfin-style server-address screen, validate the address against the
unauthenticated `/api/system/info/public` discovery endpoint, then load the web UI with
persistent cookies so the 90-day sliding refresh-token login survives restarts ("permanent
login" until the server revokes it).

## Windows (`clients/windows`)

WPF app hosting Microsoft Edge WebView2. Requires the WebView2 Runtime on the target
machine (preinstalled on Windows 11 and current Windows 10; the app opens the download
page if it is missing).

Build from Linux/macOS/Windows:

```sh
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o bin/publish
```

Produces a single self-contained `Rensaio.exe` (no .NET install needed on the target).
Server address is stored in `%AppData%\Rensaio\settings.json`; browser data (cookies,
cache) in `%LocalAppData%\Rensaio\WebView2`. Press **Ctrl+Shift+S** in the app to change
servers.

The exe is unsigned, so Windows SmartScreen will warn on first run
(More info → Run anyway).

## Android (`clients/android`)

Kotlin WebView app (minSdk 24 / Android 7.0+, target SDK 34). Plain-http LAN addresses
are allowed (`usesCleartextTraffic`), so `http://192.168.x.x:8080` works like in the
Jellyfin app. Back button navigates web history; at the root it offers Exit / Change
server. Downloads are handed to the system DownloadManager.

Build (needs JDK 17, Android SDK with platform 34 + build-tools 34, Gradle 8.7+):

```sh
export ANDROID_HOME=/opt/android-sdk
cd clients/android
gradle assembleRelease
```

Release signing reads `clients/android/key.properties` (untracked):

```properties
keystoreFile=/path/to/rensaio.keystore
keystorePassword=...
keyAlias=rensaio
keyPassword=...
```

Without it the APK is signed with the debug key. Either way the APK is not from Play,
so installing requires allowing installs from unknown sources. Keep using the same
keystore for updates — Android refuses to update an app whose signature changed.
