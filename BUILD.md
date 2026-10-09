# Building Renzo Shiori

This document covers building every artifact in the repository: the **server**
(a .NET 8 backend that embeds the web UI), the **web frontend** (Next.js static
export), and the auxiliary projects. It also documents the deploy cycle and the
release checklist.

> There are no Shiori-specific apps. The standalone Android and Windows clients
> were retired on 2026-08-21 and removed from the repo on 2026-10-09: the one
> client is **Renzo Hub** (github.com/Levitate0/Renzo-Hub), which ships manga and
> anime as a single app on Android, Android TV and Windows.
>
> The build machine used for releases is Linux/amd64.

---

## Repository layout

| Path | What it is |
|---|---|
| `RenzoBackend/` | .NET 8 ASP.NET Core server. Serves the API **and** the web UI, which is embedded as `wwwroot.zip`. |
| `RenzoFrontend/` | Next.js (static export, `output: 'export'`) web UI → `out/`. |
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

## 3. Auxiliary projects

- **`RenzoOAuthProxy`** — built and shipped with the server (section 2). To build
  standalone: `dotnet publish RenzoOAuthProxy/RenzoOAuthProxy.csproj -c Release`.
- **`Mihon.ExtensionsBridge.Net`** — the compatibility layer used to run Mihon
  extensions. It is a prebuilt IKVM DLL consumed by the backend; rebuild only if
  changing the bridge (`dotnet build Mihon.ExtensionsBridge.Net/Mihon.ExtensionsBridge.sln -c Release`).
- **`RenzoTray`**, **`Renzo.Web`**, **`RenzoOAuthProxy.CF`** — ancillary; not part
  of a release.

The whole solution can be restored/built with `dotnet build Renzo.sln -c Release`
(this does not produce the packaged server image).

---

## 4. Release checklist

1. **Bump `<Version>`** in `RenzoBackend.csproj` in the release commit (it has
   drifted a whole release behind the tag before).
2. **Build + deploy the server** (sections 1–2), verifying `/api/system/version`
   reports the new version.
3. **Tag `v<ver>`** and push the tag. CI (`.github/workflows/docker-publish.yml`)
   builds the frontend, the sidecar jar and the backend from source and publishes
   `ghcr.io/levitate0/renzo-shiori:<ver>` (amd64). Verify with an anonymous
   manifest fetch.
4. Client changes ship as a **Renzo Hub** release, not from this repo.

---

## Architecture notes

- **One client: Renzo Hub.** It is a native Kotlin/Compose app (Android, Android
  TV, Windows) talking to this server's API — not a shell around the web UI. Its
  offline reading is its own; see the Renzo-Hub repo.
- `RenzoFrontend/src/lib/native/` (the `__RenzoAndroid` / `__RenzoWindows`
  adapters) served the retired WebView shells. It is a no-op on the web build and
  is now dormant; it can be removed in a later cleanup.
- **Back up `/config`** (SQLite DB + extracted UI) before upgrading — the server
  migrates on startup.
