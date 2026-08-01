# UI parity contract — native Compose app ⇄ web frontend

**The rule (user directive, 2026-08-01): the native Android app's UI is a
transliteration of the web frontend, not an interpretation.** Before building
or changing any native screen, READ the corresponding web component source
and port its actual layout, spacing, colors, and copy — "copy and paste, but
in Kotlin." Do not design from memory or approximate with Material defaults.

Two absolute rules added after the 2026-08-01 review:

1. **Nothing is greyed out.** No disabled rows, no dimmed "pending native
   screen" placeholders, no "coming soon". If the web has a control, the
   native app has it and it works. Permission-gated items are HIDDEN exactly
   where the web hides them — never shown-but-dead.
2. **Every screen gets ported.** "PORT ALL SCREENS TO THE APP, NOTHING
   REMAINS OF THE CURRENT MOBILE." The only permitted deviation is layout
   direction: what is side-by-side on desktop stacks vertically on a phone.
   Content, wording, ordering, badges and colors stay identical.

## Source-of-truth map

| Native (ui/…) | Web source (RenzoFrontend/src/…) | Status |
|---|---|---|
| `theme/Theme.kt` | `styles/globals.css` `:root` tokens | ✅ tokens ported verbatim (hex of each HSL var; 8dp radius scale); Geist Sans bundled |
| `home/HomeShell.kt` top bar | `components/comp/layout/command-bar.tsx` (<lg branch) | ✅ hamburger/logo/wordmark/expanding search/pill/avatar (avatar image when set) |
| `home/HomeShell.kt` drawer | `components/comp/layout/section-pills.tsx` `SectionList` + drawer footer | ✅ all 7 sections incl. native-only Downloads, accent bar, Queue dot+badge, footer = offline pill + download status + GitHub/Discord/Website |
| `home/HomeShell.kt` account panel | `components/comp/layout/user-menu.tsx` | ✅ slides in from the RIGHT; role badge, OPDS copy (full external URL), every item live, Import Series picker modal, adult toggle |
| `library/LibraryScreen.kt` | `app/library/page.tsx` + `components/comp/series/list-series/*` | 🔄 full port in progress |
| `browse/BrowseScreen.kt` | `app/cloud-latest/page.tsx` | 🔄 full port in progress (spotlight hero, source picker, add-to-library) |
| `downloads/DownloadsScreen.kt` | `app/downloads/page.tsx` | 🔄 new screen in progress |
| `queue/QueueScreen.kt` | `app/queue/page.tsx` | 🔄 full port in progress |
| `updates/UpdatesScreen.kt` | `app/updates/page.tsx` | 🔄 full port in progress |
| `series/SeriesDetailScreen.kt` | `app/library/series/page.tsx` + `components/comp/series/detail/*` | 🔄 full port in progress (hero, chapters toolbar, **sources card**, dialogs) |
| `reader/ReaderScreen.kt` | `app/reader/page.tsx` + `components/comp/reader/*` | 🔄 full port in progress (top bar, scrubber, settings sheet, chapter list) |
| `status/StatusScreen.kt` | `app/status/page.tsx` | 🔄 new screen in progress |
| `sources/SourcesScreen.kt` | `app/providers/page.tsx` + `components/comp/sources/*` | 🔄 new screen in progress |
| `settings/AccountScreen.kt` | `app/account/page.tsx` (Account / Site Logins / Scrobbler) | 🔄 full port in progress |
| `settings/AppearanceScreen.kt` | `app/appearance/page.tsx` | 🔄 new screen in progress |
| `settings/UsersScreen.kt` | `app/users/page.tsx` | 🔄 new screen in progress |
| `settings/ServerSettingsScreen.kt` | `components/comp/settings-manager.tsx` | 🔄 new screen in progress |
| `settings/TrackersScreen.kt` | `components/comp/scrobbler/*` | 🔄 new screen in progress |
| `importwizard/` | `components/providers/import-wizard-provider.tsx` | 🔄 new flow in progress |
| `onboarding/` | `lib/utils/onboarding.ts` + walkthrough component | 🔄 new flow in progress |
| `auth/LoginScreen.kt` + password flows | `app/login`, `app/user-select`, `app/auth/*` | 🔄 full port in progress |
| `auth/ConnectScreen.kt` | old `res/layout/activity_main.xml` serverPanel | ✅ pixel-faithful — user asked to KEEP this one as-is |

## Architecture notes for parallel porting

- `NetworkModule` exposes `retrofitFor(baseUrl)`, `currentServiceOf<T>()` and
  `serverUrl`, so each feature area declares its own Retrofit interface in its
  own file instead of everything piling into `ApiService.kt`.
- `ui/util/AdultFilter.kt` is the native twin of `lib/utils/adult-filter.ts`
  (same key name, same tag set, local-only — unrelated to the server's
  `nsfwVisibility` setting, which filters the Sources list).
- Crash forensics: `RenzoApp` persists uncaught exceptions to
  `filesDir/last-crash.txt` **and kills the process** (handing off to a
  missing default handler left a dead UI thread behind a live window — the
  "app glitched to a black screen" report). `MainActivity` shows the trace on
  the next launch with a Copy button, since the device has no adb.

## Gotchas learned porting

- M3 `Button` does NOT take shapes from the theme — pass
  `shape = MaterialTheme.shapes.small` explicitly or you get Material pills.
  (`IconButton` has no shape param at all — don't add one.)
- `painterResource()` on a **platform** drawable (`android.R.drawable.*`) can
  throw at runtime while compiling fine — it crashed the app at drawer
  composition. Use bundled vectors (`res/drawable/ic_github.xml`, `ic_discord.xml`,
  `ic_globe.xml`) or Material icons.
- Material icon names ≠ lucide names: lucide `Activity` → `MonitorHeart`,
  `Sparkles` → `AutoAwesome`, `Medal` → `MilitaryTech`, `KeyRound` → `VpnKey`.
- Web `text-[11px]`≈labelSmall, `text-sm`≈bodyMedium/titleSmall(medium),
  `h-8` buttons = 32dp, `px-3 py-2.5` = 12dp/10dp.
- Tailwind palette hexes used by the web UI: emerald-500 #10B981,
  amber-500 #F59E0B, violet-400 #A78BFA, pink-500 #EC4899, blue-500 #3B82F6,
  yellow-500 #EAB308, green-500 #22C55E.
- Chapter rows need BOTH `api/serie/chapters` (downloaded/source/date/locked)
  and `api/reader/chapters` (progress/completed/bookmarked), joined by
  chapter number — same join the web chapters-section does.
- Never `import androidx.compose.foundation.layout.weight` — it's a scope
  function and importing it breaks the build with a RowColumnParentData error.
- Never write a fully-qualified `app.renzoshiori.client....` reference inside
  a composable that has a local named `app` — the local shadows the package.
