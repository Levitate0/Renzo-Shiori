# Publishing the image: readiness checklist

Prep is done — `.github/workflows/docker-publish.yml` will build and push a
correct image. This is what to confirm before pointing strangers at it.

## Done during prep

- [x] **CI actually builds the sidecar.** The old `docker-build.yml` never did,
      so it failed at `COPY ./sidecar/` — and had it been patched past that, the
      image would have started up healthy and supported zero sources. Deleted
      and replaced.
- [x] **Lockfile fixed.** Both `pnpm-lock.yaml` and `package-lock.json` still
      listed `next-themes`, dropped from `package.json` with the dark-only
      change, so *both* `pnpm install --frozen-lockfile` and `npm ci` failed.
      Regenerated the pnpm lockfile (verified: frozen install + build pass) and
      deleted the stale `package-lock.json` so there is one source of truth.
- [x] **OAuth proxy is built.** `entrypoint.sh` starts it if present and shrugs
      if not, so its absence is silent — tracker linking would just be dead in
      the published image. Now built and asserted.
- [x] **amd64 only**, deliberately — see `docs/DOCKER.md`.
- [x] **Publish is explicit** — `v*.*.*` tags or manual dispatch, never a branch
      push. `:latest` only moves for a non-prerelease tag or an explicit opt-in.
- [x] **User docs** — `docs/DOCKER.md` (compose, ports, volumes, env, limits).

## Before the first publish

- [ ] **Dry run it.** Dispatch the workflow with `extra_tag: testing` and
      `tag_latest: false`, then pull that image on a clean machine and confirm:
      the UI loads, an extension installs, a chapter downloads, and a
      WebView-dependent source (Comix / AllAnime / Lunar) actually works — that
      last one is what proves the sidecar and JCEF made it into the image.
- [ ] **Make the package public.** A GHCR package is private even on a public
      repo. After the first push: repo → Packages → the package → Package
      settings → Change visibility → Public. Otherwise `docker pull` fails for
      everyone with `denied`/`manifest unknown`.
- [ ] **Verify action major versions.** They were pinned against what was
      current at the time of writing (`checkout@v7`, `login-action@v4`,
      `setup-buildx-action@v4`, `metadata-action@v6`, `build-push-action@v7`).
      If the first run fails immediately on an action version, that's why.
- [ ] **Pin the base image.** `RenzoBackend/Dockerfile` starts from
      `ubuntu:resolute`, a rolling tag. Fine for a personal build, poor for a
      published artifact — a rebuild months later can silently pick up a
      different userland. Pin a release tag or a digest.
- [ ] **Decide the branch story.** The workflow defaults to `main`, but the work
      has been on `jvm-sidecar`. Merge to `main` first, or dispatch with an
      explicit ref.
- [ ] **Tag a version.** `git tag v1.0.0 && git push shiori v1.0.0` triggers the
      publish and moves `:latest`.

## Worth doing, not blocking

- [ ] **Secrets audit before the repo is public.** Anything committed under
      `appsettings.json` (there are host IPs and a `DefaultConnection` in there)
      and any OAuth client ids/secrets should be reviewed. Git history too, not
      just the tip.
- [ ] **A stall-free soak.** The catalogue sweep and the JCEF helper leak both
      caused host-level trouble recently; both are fixed, but a few days of
      clean uptime is what makes a published image defensible.
- [ ] **Registry cache.** `type=gha` is used, but GHA cache entries evict after
      7 days without access, so a release-only workflow will nearly always run
      cold. `type=registry` cache would actually help if build time becomes
      annoying.
- [ ] **Healthcheck.** The image has none. A `HEALTHCHECK` hitting the app on
      9833 would let `restart: unless-stopped` recover a wedged container.
- [ ] **Provenance/SBOM attestations.** Cheap to enable, but they add an extra
      manifest entry that shows as an untagged `unknown/unknown` package in the
      GHCR UI and confuses people. Skip unless you want them.
