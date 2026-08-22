# ARCHIVED — do not build, do not edit

Superseded by **Renzo Hub** (`/opt/zurg-stack/renzo-ecosystem/hub`), which ships
both halves as one app.

Archived 2026-08-21 on user direction: the service-specific APKs are "useless and
heavily out of date". Verified before archiving — this tree ships to NOBODY:
zero APK assets across every GitHub release on `Levitate0/Renzo` and
`Levitate0/Renzo-Shiori`, no CI, and no build script outside this directory
references it.

**Edits here reach no user.** The shipping client is:

| | |
|---|---|
| anime | `hub/feature-renzo` |
| manga | `hub/feature-shiori` |
| shared | `hub/core`, `hub/app`, `hub/hub-desktop` |

This is not hypothetical. On 2026-08-21, commit `7387d46` shipped an 18+ filter
fix to this tree and missed the Hub, leaving the only client users actually run
with a 24-tag-short adult filter for 5h30m. The build guard in this directory
exists so that cannot happen silently again.

If you arrived here from a document that called this live, that document is
stale — fix the document rather than building this.
