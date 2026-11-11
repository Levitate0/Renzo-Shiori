# Renzo Shiori — working rules

Manga server (.NET, `RenzoBackend/`) + web UI (Next.js, `RenzoFrontend/`). One of
three surfaces of the Renzo ecosystem; the others are Renzo (anime,
Levitate0/Renzo) and Renzo Hub (the only client app, Levitate0/Renzo-Hub). On the
owner's server the three repos sit side by side, with a shared CLAUDE.md in the
parent directory that has the full picture.

## Owner rules

- **Web changes go to Hub too (2026-11-08).** Every change to this web UI is
  also made in Renzo Hub in the same piece of work, without asking: transliterate
  the component 1:1 into Kotlin/Compose, compile-check Hub, and cut a Hub release
  so it actually reaches the owner. Server-only changes need no Hub port.
- **Every shipped change is released (2026-11-10).** Deploying locally is not
  the end. When changes ship, also release this repo, the same way Hub is
  released:
  1. Bump `<Version>` in `RenzoBackend/RenzoBackend.csproj` (it once drifted a
     whole release behind the tag) and commit.
  2. Push the branch (`jvm-sidecar`) to Levitate0/Renzo-Shiori.
  3. `git tag -a vX.Y.Z` and push the tag. CI (`.github/workflows/docker-publish.yml`)
     builds and publishes `ghcr.io/levitate0/renzo-shiori` — amd64 only, see the
     workflow for why.
  4. Create the GitHub release page for the tag with a plain-language changelog
     of everything since the previous tag.
  5. Check the Actions run succeeded and the tag is on GHCR.

  Batch a piece of work into one release; don't release every commit.

## Deploy (owner's server)

The Dockerfile only assembles; it compiles nothing, and its inputs are
gitignored, so order matters: frontend build → `wwwroot.zip` + `wwwroot.sha256`
(an EmbeddedResource, so zip BEFORE publish) → `dotnet publish -c Release -r
linux-x64 --self-contained false -o bin/linux/amd64` → `docker buildx build
--platform linux/amd64 --load -t renzo-shiori:latest .` from `RenzoBackend/` →
recreate the container. Tests: `cd RenzoBackend.Tests && dotnet test`.
