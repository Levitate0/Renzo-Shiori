# Running Renzo Shiori in Docker

The published image is `ghcr.io/levitate0/renzo-shiori`.

> **Platform:** `linux/amd64` only. See [Why no arm64](#why-no-arm64).

## Quick start

```yaml
services:
  renzo-shiori:
    image: ghcr.io/levitate0/renzo-shiori:latest
    container_name: renzo-shiori
    restart: unless-stopped
    ports:
      - "9833:9833"
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
    volumes:
      # Config, database, installed extensions, JCEF runtime. Must persist.
      - ./config:/config
      # Your manga library. Point this wherever you keep it.
      - ./library:/series
    deploy:
      resources:
        limits:
          # See "Resource limits" — do not leave this unbounded.
          memory: 8G
          pids: 1500
```

Then open <http://localhost:9833>.

## Ports

| Port | Purpose | Publish it? |
|---|---|---|
| `9833` | Web UI and API | **Yes** |
| `9834` | JVM extension sidecar | **No** — internal only, bound for the app's own use. Publishing it exposes the extension engine with no auth in front of it. |

## Volumes

| Path | Holds | If you don't persist it |
|---|---|---|
| `/config` | `renzo.db` (SQLite), settings, installed extensions, the downloaded JCEF runtime, cookies | You lose your library metadata, users, read state and every installed source on each recreate — and the container re-downloads the JCEF runtime every start |
| `/series` | The manga library itself (archives on disk) | Downloads have nowhere to land |

`/config` is the important one. The database lives there, and so does `mihon/` — extensions and the JCEF/Chromium runtime the extension engine needs.

## Environment

Nothing is strictly required; every variable below has a working default.

| Variable | Default | What it does |
|---|---|---|
| `PUID` | `99` | UID the app runs as. Set it to your own user so files it writes are yours. |
| `PGID` | `100` | GID the app runs as. |
| `TZ` | container default | Timezone, affects schedules and displayed times. |
| `ProviderCredentials__MyAnimeList__ClientId` | – | MyAnimeList OAuth app id, for account linking |
| `ProviderCredentials__MyAnimeList__ClientSecret` | – | …and its secret |
| `ProviderCredentials__AniList__ClientId` | – | AniList OAuth app id |
| `ProviderCredentials__AniList__ClientSecret` | – | …and its secret |

The tracker variables are optional — without them the app runs fine and AniList/MAL account linking is simply unavailable.

**Library folder permissions.** The app writes as `PUID:PGID`, and it renames and moves series directories. If your library lives on a share whose directories aren't group-writable, renames fail with `EACCES`. Either set `PUID`/`PGID` to a user that owns the library, or make the directories group-writable (`chmod 2775`).

## Resource limits

**Set a memory limit.** .NET's server GC sizes its heap from the container's cgroup limit and, on a many-core host, will happily grow toward ~75% of it before collecting hard. With no limit — or a very large one — the container can balloon, push the host into swap and take down everything else on the machine. This is not hypothetical; it happened on the development host with a 32 GB limit on a 62 GB box.

`8G` is a comfortable starting point for a large library; the steady-state working set is a few GB. `pids: 1500` bounds the Chromium helper processes the extension engine spawns for sites that need a real browser.

## First run

1. Bring the container up and open <http://localhost:9833>.
2. Create the first account through the setup flow — that account is the owner.
3. Add sources from the **Sources** page (extensions install into `/config`).
4. Add series from **Browse**, or import an existing library.

## Why no arm64

The image ships `linux/amd64` only, and that is deliberate rather than an oversight:

- The Dockerfile pins `RENZO_SIDECAR_LDPATH=/usr/lib/jvm/java-21-openjdk-amd64/lib`, and the sidecar launcher *replaces* `LD_LIBRARY_PATH` with it. On arm64 that directory does not exist, so JCEF loses `libjawt.so`.
- The Chromium/JCEF natives the extension engine downloads at runtime would need the arm64 package, a path this project has never exercised.

An arm64 image would push successfully and then misbehave at runtime for any source needing a browser, which is worse than not publishing one. Unblocking it is a small change — derive the path from the build arg that is already declared:

```dockerfile
ARG TARGETARCH
ENV RENZO_SIDECAR_LDPATH=/usr/lib/jvm/java-21-openjdk-${TARGETARCH}/lib
```

…followed by actually testing an arm64 image end to end, including a WebView-dependent source.

## Building the image yourself

The Dockerfile only *assembles* — it compiles nothing. Four artefacts must exist in `RenzoBackend/` first, and all four are gitignored:

```bash
# 1. frontend -> wwwroot.zip (+ .sha256); dotnet publish FAILS without these,
#    they are EmbeddedResources
cd RenzoFrontend && pnpm install --frozen-lockfile && pnpm run build && cd ..
(cd RenzoFrontend/out && zip -qr ../../RenzoBackend/wwwroot.zip .)
sha256sum RenzoBackend/wwwroot.zip | awk '{print $1}' > RenzoBackend/wwwroot.sha256

# 2. sidecar -> the extension engine. JDK 21 REQUIRED (Java 21+ APIs).
cd Mihon.ExtensionsBridge.Net/Android.Compatibility.Layer
./gradlew :AndroidCompat:shadowJar --no-daemon && cd -
cp Mihon.ExtensionsBridge.Net/Android.Compatibility.Layer/AndroidCompat/build/libs/*-all.jar \
   RenzoBackend/sidecar/AndroidCompat-1.0-all.jar
cp -r Mihon.ExtensionsBridge.Net/tools/enjarify RenzoBackend/sidecar/enjarify

# 3. backend
dotnet publish RenzoBackend/RenzoBackend.csproj -c Release -r linux-x64 \
  --self-contained false -o RenzoBackend/bin/linux/amd64

# 4. oauth proxy — optional at runtime, but its absence is SILENT
dotnet publish RenzoOAuthProxy/RenzoOAuthProxy.csproj -c Release -r linux-x64 \
  --self-contained false -o RenzoBackend/bin/linux/amd64/oauthproxy

# then
docker buildx build --platform linux/amd64 -t renzo-shiori:latest ./RenzoBackend
```

Skipping step 2 is the dangerous one: the build fails at `COPY ./sidecar/`, and if you work around that, the app boots looking healthy and supports **no sources at all**.

CI does exactly this — see `.github/workflows/docker-publish.yml`.
