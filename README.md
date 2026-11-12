<p align="center">
  <img src="./docs/renzo-banner.png" alt="Renzo Shiori: Your World. Your Library." width="100%">
</p>

<h1 align="center">連蔵・栞 · Renzo Shiori</h1>
<p align="center"><em>Your world. Your library.</em></p>

**Renzo Shiori** is a self-hosted, multi-user manga, manhwa and manhua server. Add a series once, and it keeps every chapter downloaded, organised and up to date from the sources you pick. When one source fails, it falls back to the next. It includes a web reader, tracker sync and an OPDS feed. Every user has their own private library, sources, queue and reading progress.

**Documentation: [the wiki](https://github.com/Levitate0/Renzo-Shiori/wiki)**

## Part of the Renzo ecosystem

| | |
|---|---|
| **Renzo Shiori** | Manga server and web reader (this repository) |
| **Renzo** | The anime counterpart |
| **[Renzo Hub](https://github.com/Levitate0/Renzo-Hub)** | The native client for both, on Android, Android TV and Windows. It is the only app; there are no separate Shiori apps. |

## Features

- **Mihon/Tachiyomi extensions** for sources. Install from repositories, pin versions, roll back, or sideload your own APK. Extensions run on a real JVM with an embedded Chromium, so sites that need a browser or a Cloudflare check work too.
- **Many sources per series, in priority order.** Downloads come from the best source that has the chapter. Failing sources step down automatically, and a **Health** tab shows which sources are dying. **Migrate** moves whole series off a broken source.
- **A fair download queue.** No single source can hog it. Clear completed or failed downloads, or retry everything, in one click.
- **A web reader** with paged, right-to-left, double-page and long-strip (webtoon) modes. It streams chapters that aren't downloaded yet, has **Switch source** for a bad upload, and makes chapter breaks hard to scroll past by accident.
- **Paid chapters.** Sign in to coin-based sites with your own account. Chapters you own unlock and download automatically, and auto-buy is optional, per series.
- **Trackers.** Sync reading progress to AniList, MyAnimeList, Kitsu and MangaDex.
- **OPDS** feed and an **MCP** endpoint for each user, behind a private random URL.
- **Multi-user**, with User, Manager, Admin and Owner roles, invites, password reset, per-device sign-in and TV pairing.
- **Collections** and drag-to-reorder **categories**. Series are filed into Manga, Manhwa, Manhua or Comic automatically.
- **18+ filter.** Adult series are hidden in the library and Browse until you choose to show them.
- **Import** an existing library from disk, or reading progress from a Tachiyomi/Mihon backup.
- **Plain files on disk:** CBZ chapters with `ComicInfo.xml`, `cover.jpg`, and a `renzo.json` per series that carries metadata and read state.

The full guide is on the [Features](https://github.com/Levitate0/Renzo-Shiori/wiki/Features) wiki page.

## Quick start (Docker)

The image is `ghcr.io/levitate0/renzo-shiori`, for **linux/amd64** only.

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
      - ./config:/config     # database, settings, extensions, logs
      - ./library:/series    # your library
    deploy:
      resources:
        limits:
          memory: 16G
          pids: 6000
```

```bash
docker compose up -d
```

Open `http://<host>:9833` and follow the setup wizard. The first account becomes the **Owner**.

### Why the limits matter

- **`memory`.** The .NET server sizes its heap from the container's memory limit. Without one, it can grow until the whole host swaps. The image caps .NET at half the limit and leaves the rest for the extension JVM and its browsers.
- **`pids`.** This limit counts threads, not just processes, and each embedded browser uses hundreds. If the container runs out, the server aborts with a misleading `Out of memory.` while memory is fine. Keep this generous; the reference deployment uses 6000.
- **CPU.** Don't cap CPU tightly. Downloads are many concurrent page fetches, some through a real browser.

Only port `9833` needs publishing. Back up `/config` before upgrading: database migrations run automatically on start.

See [Installation](https://github.com/Levitate0/Renzo-Shiori/wiki/Installation) for volumes, permissions, reverse proxies and the reasoning behind amd64-only. See [Configuration](https://github.com/Levitate0/Renzo-Shiori/wiki/Configuration) for every setting and environment variable.

## Documentation

| Page | |
|---|---|
| [Installation](https://github.com/Levitate0/Renzo-Shiori/wiki/Installation) | Docker, volumes, ports, limits, reverse proxy |
| [Configuration](https://github.com/Levitate0/Renzo-Shiori/wiki/Configuration) | Settings page, `appsettings.json`, environment variables, tracker OAuth |
| [Features](https://github.com/Levitate0/Renzo-Shiori/wiki/Features) | User guide |
| [Troubleshooting](https://github.com/Levitate0/Renzo-Shiori/wiki/Troubleshooting) | Known failure modes and what to check |
| [Architecture](https://github.com/Levitate0/Renzo-Shiori/wiki/Architecture) | How the server, the JVM sidecar, the database and the queues fit together |
| [API Reference](https://github.com/Levitate0/Renzo-Shiori/wiki/API-Reference) | Every HTTP route and its permission level |
| [Development](https://github.com/Levitate0/Renzo-Shiori/wiki/Development) | Building from source, tests, migrations, custom extensions |
| [Releasing](https://github.com/Levitate0/Renzo-Shiori/wiki/Releasing) | Versioning and publishing the image |

## Building from source

In short: build the web UI with pnpm, zip it into `RenzoBackend/wwwroot.zip` with a fresh `wwwroot.sha256` (**before** `dotnet publish`, because it is embedded in the binary), build the sidecar jar with JDK 21, publish the backend, then build the image from `RenzoBackend/`. Clone with Git LFS. The full steps are on the [Development](https://github.com/Levitate0/Renzo-Shiori/wiki/Development) page.

## Contributing

Issues and pull requests are welcome. When reporting a problem, include your version (`/api/system/version`) and the relevant lines from `config/logs/`. Remove cookies, tokens and your OPDS address first.

## Credits and licence

Renzo Shiori continues a fork of [maxpiva/Rensaio](https://github.com/maxpiva/Rensaio), itself descended from **Kaizoku** by OAE. The extension engine's Android compatibility layer derives from [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server), and the server runs [Mihon](https://mihon.app) extensions. Thanks to all of their authors.

Licensed under the [GNU GPL v3](./LICENSE).
