# Building Renzo Shiori

This document covers building every artifact in the repository: the **server**
(a .NET 8 backend that embeds the web UI), the **web frontend** (Next.js static
export), the **Android** and **Windows** native clients, and the auxiliary
projects. It also documents the deploy cycle and the full release checklist.

> The build machine used for releases is Linux/amd64. The backend and Android
> APK build natively there; the Windows client cross-compiles from Linux via
> the .NET SDK. Signing certificates/keystores are **not** in the repo.

---

## Repository layout

| Path | What it is |
|---|---|
| `RenzoBackend/` | .NET 8 ASP.NET Core server. Serves the API **and** the web UI, which is embedded as `wwwroot.zip`. |
| `RenzoFrontend/` | Next.js (static export, `output: 'export'`) web UI → `out/`. |
| `clients/android/` | Android client — a remote-first WebView shell (Kotlin) with the offline bridge. |
| `clients/windows/` | Windows client — a WebView2 WPF shell (with the native offline bridge + background downloader), packaged with NSIS. |
| `RenzoOAuthProxy/` | Tracker OAuth service (AniList/MAL/Kitsu/MangaDex), bundled **inside** the server container. |
| `Mihon.ExtensionsBridge.Net/` | Prebuilt IKVM compatibility layer for running Mihon/Tachiyomi extensions. |
| `RenzoTray/`, `Renzo.Web/`, `RenzoOAuthProxy.CF/` | Ancillary/experimental — not part of a release. |

---

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 8.x (9.x SDK also builds the net8.0 targets) | backend, Windows client, OAuth proxy |
| Node.js | 18+ (22 used for releases) | frontend |
| Docker (+ buildx) | recent | server image |
| Android SDK | API 34 | Android APK |
| Gradle | 8.7 | Android APK |
| `makensis` (NSIS) | 3.x | Windows installer |
| `osslsigncode` | 2.x | signing the Windows exe/installer on Linux |
| `zip`, `sha256sum` | — | packaging the UI + checksums |

---

## 1. Web frontend (`RenzoFrontend`)

```bash
cd RenzoFrontend
npm install
npm run build          # → RenzoFrontend/out  (static export)
```

`out/` is the entire UI. It is not served directly in production — it is zipped
into the backend (next section). Pre-existing TypeScript errors are ignored at
build time (`next.config.*` → `typescript.ignoreBuildErrors`).

---

## 2. Server (`RenzoBackend`) — embeds the UI

The backend embeds the frontend as `wwwroot.zip` and, at startup, extracts it to
a persisted directory **only when the embedded `wwwroot.sha256` changes**. This
makes the sha step below mandatory — skip it and the server keeps serving the
old UI even after a rebuild.

Run all of this **from the repo root**:

```bash
# 1) build the UI (section 1) so RenzoFrontend/out exists, then package it:
(cd RenzoFrontend/out && rm -f ../../RenzoBackend/wwwroot.zip && zip -r -q -X ../../RenzoBackend/wwwroot.zip .)

# 2) REQUIRED: regenerate the embedded checksum (plain hash, into RenzoBackend/).
#    Writing it anywhere else (e.g. repo root) is a silent no-op.
sha256sum RenzoBackend/wwwroot.zip | awk '{print $1}' > RenzoBackend/wwwroot.sha256

# 3) publish (framework-dependent; the container ships the ASP.NET 8 runtime):
dotnet publish RenzoBackend/RenzoBackend.csproj -c Release -r linux-x64 \
  --self-contained false -o RenzoBackend/bin/linux/amd64
```

`wwwroot.zip` and `wwwroot.sha256` are git-ignored build artifacts embedded via
`<EmbeddedResource>` in `RenzoBackend.csproj`.

The **tracker OAuth proxy** (`RenzoOAuthProxy`) is published alongside the
backend into `RenzoBackend/bin/linux/amd64/oauthproxy/` and started by the
container entrypoint — no external proxy is required.

### Server Docker image + deploy

The image is built **from the `RenzoBackend/` directory** (its `Dockerfile`
does `COPY ./bin/$TARGETPLATFORM/ .`, so `TARGETPLATFORM` must resolve to
`linux/amd64` to match the publish output):

```bash
cd RenzoBackend
docker buildx build --platform linux/amd64 --load -t renzo-shiori:latest .

cd ..            # then (re)create the container from your compose file:
docker compose up -d --force-recreate renzo-shiori
```

> Do **not** build the bare top-level `Dockerfile` of the host's stack — that is
> an unrelated container. Always use `RenzoBackend/Dockerfile` as above.

Verify:

```bash
curl -s http://127.0.0.1:9833/api/system/info/public   # {"product":"Renzo Shiori",...}
curl -s http://127.0.0.1:9833/api/system/version        # {"version":"x.y.z","build":"x.y.z.<hash>"}
```

`build` embeds the frontend hash and changes on every UI deploy — the clients
poll it and silently reload (deferring while the reader is open).

---

## 3. Android client (`clients/android`)

A remote-first WebView shell: it probes `/api/system/info/public`, then loads
the server UI. Offline support is provided by a native `@JavascriptInterface`
(`window.__RenzoAndroid`) plus a bundled offline reader
(`app/src/main/assets/offline/index.html`).

Bump the version in `app/build.gradle.kts` (`versionName` **and** `versionCode`
— Android requires `versionCode` to increase for an in-place update), then:

```bash
cd clients/android
SIGNING_DIR=/path/to/signing \
ANDROID_HOME=/opt/android-sdk \
GRADLE=/opt/gradle-8.7/bin/gradle \
  ./build-release.sh
# → app/build/outputs/apk/release/app-release.apk  (signed, v2+v3)
```

`build-release.sh` generates a keystore in `SIGNING_DIR` on first run and reuses
it after. The APK is minified with R8; `app/proguard-rules.pro` keeps
`@JavascriptInterface` methods (without that rule the offline bridge silently
breaks in release builds).

---

## 4. Windows client (`clients/windows`)

A self-contained WebView2 WPF shell, packaged with NSIS. Like Android it is
remote-first (probes `/api/system/info/public`, then loads the server UI) and
carries a native offline stack — feature-parity with Android since v1.2.0:

| File | Role |
|---|---|
| `RenzoStore.cs` | File/KV/manifest (v2) storage + the download job queue, under a chosen or app-default folder. |
| `RenzoDownloader.cs` | Native background downloader (parallel page fetch, Bearer auth); keeps running when the window is hidden and resumes a queued job on launch. |
| `RenzoBridge.cs` | `[ComVisible]` host object added via `CoreWebView2.AddHostObjectToScript`. |
| `NativeAssets.cs` | The JS shim that exposes the host object as the synchronous `window.__RenzoWindows` the shared frontend expects, plus the bundled offline reader (shown when the server is unreachable). |

These `.cs` files are auto-included by the SDK-style project — no `.csproj`
edit is needed when adding sources. Bump the version in **both**
`RenzoWindows.csproj` (`<Version>`) and `renzoshiori-installer.nsi`
(`!define VERSION`), then:

```bash
cd clients/windows
# 1) publish (self-contained, folder — matches the installer's File /r):
dotnet publish RenzoWindows.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=false -o /tmp/renzo-exe-folder

# 2) sign the inner exe (self-signed cert; RFC3161 timestamp):
osslsigncode sign -pkcs12 <cert.pfx> -pass <pw> -h sha256 \
  -t http://timestamp.digicert.com -n "Renzo Shiori" \
  -in /tmp/renzo-exe-folder/RenzoShiori.exe -out /tmp/renzo-exe-folder/RenzoShiori.exe

# 3) build the installer (reads /tmp/renzo-exe-folder):
makensis renzoshiori-installer.nsi          # → RenzoShiori-Setup.exe

# 4) sign the installer too, then rename to RenzoShiori-Setup-<ver>.exe
osslsigncode sign -pkcs12 <cert.pfx> -pass <pw> -h sha256 \
  -t http://timestamp.digicert.com -n "Renzo Shiori Setup" \
  -in RenzoShiori-Setup.exe -out RenzoShiori-Setup-<ver>.exe
```

The self-signed certificate triggers a SmartScreen "unknown publisher" prompt on
first run — this is expected.

---

## 5. Auxiliary projects

- **`RenzoOAuthProxy`** — built and shipped with the server (section 2). To build
  standalone: `dotnet publish RenzoOAuthProxy/RenzoOAuthProxy.csproj -c Release`.
- **`Mihon.ExtensionsBridge.Net`** — the compatibility layer used to run Mihon
  extensions. It is a prebuilt IKVM DLL consumed by the backend; rebuild only if
  changing the bridge (`dotnet build Mihon.ExtensionsBridge.Net/Mihon.ExtensionsBridge.sln -c Release`).
- **`RenzoTray`**, **`Renzo.Web`**, **`RenzoOAuthProxy.CF`** — ancillary; not part
  of a release.

The whole solution can be restored/built with `dotnet build Renzo.sln -c Release`
(this does not produce the packaged server image or the signed clients).

---

## 6. Release checklist

1. **Bump versions** together: `RenzoBackend.csproj`, `clients/windows/RenzoWindows.csproj`
   + `renzoshiori-installer.nsi`, and `clients/android/app/build.gradle.kts`
   (`versionName` + `versionCode`).
2. **Build + deploy the server** (sections 1–2), verifying `/api/system/version`
   reports the new version.
3. **Build + sign the APK** (section 3) → `RenzoShiori.apk`.
4. **Build + sign the Windows installer** (section 4) → `RenzoShiori-Setup-<ver>.exe`.
5. **Checksums:** `sha256sum RenzoShiori-Setup-<ver>.exe RenzoShiori.apk > SHA256SUMS.txt`.
6. **Tag + GitHub Release** `v<ver>` on `main` with the three assets
   (`RenzoShiori-Setup-<ver>.exe`, `RenzoShiori.apk`, `SHA256SUMS.txt`) — **never**
   a code-signing `.cer`. GitHub attaches the source archives automatically.

---

## Architecture notes

- **Clients are thin & remote-first.** They render the server's UI, so a
  server-side change reaches every client instantly (the version poller reloads
  stale pages silently).
- **Offline** works on **both native clients** (Android + Windows desktop). All
  offline *logic* lives once in `RenzoFrontend/src/lib/native/` and is a complete
  no-op on the web build; each shell only injects dumb primitives, which the
  frontend adapter (`adapters.ts`) wraps into the shared contract:
  - **Android** exposes `window.__RenzoAndroid` via `@JavascriptInterface`.
  - **Windows** exposes `window.__RenzoWindows` via a WebView2 host object + a
    small injected JS shim (section 4).
  Downloads run in a native background service on each platform (not in the
  WebView), so they continue when the app is tabbed out. Only chapters already
  downloaded on the server can be saved offline.
- **Back up `/config`** (SQLite DB + extracted UI) before upgrading — the server
  migrates on startup.
