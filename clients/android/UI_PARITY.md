# UI parity contract — native Compose app ⇄ web frontend

**The rule (user directive, 2026-08-01): the native Android app's UI is a
transliteration of the web frontend, not an interpretation.** Before building
or changing any native screen, READ the corresponding web component source
and port its actual layout, spacing, colors, and copy — "copy and paste, but
in Kotlin." Do not design from memory or approximate with Material defaults.

## Source-of-truth map

| Native (ui/…) | Web source (RenzoFrontend/src/…) | Status |
|---|---|---|
| `theme/Theme.kt` | `styles/globals.css` `:root` tokens | ✅ tokens ported verbatim (hex of each HSL var; 8dp radius scale) |
| `home/HomeShell.kt` top bar | `components/comp/layout/command-bar.tsx` (<lg branch) | ✅ hamburger/logo/search/pill/avatar |
| `home/HomeShell.kt` drawer (nav) | `components/comp/layout/section-pills.tsx` `SectionList` | ✅ icons, accent bar, X header, Queue badge+dot, stats footer + links; Status/Sources rows dimmed (no native screens yet) |
| `home/HomeShell.kt` avatar dropdown | `components/comp/layout/user-menu.tsx` | ✅ anchored dropdown card per mobile-web reference (role badge, OPDS copy, full groups, icon footer); non-native items dimmed |
| `library/LibraryScreen.kt` cards | `components/comp/series/list-series/index.tsx` | ✅ status strip/provider badge/last-ch badge/title bar; ❌ unread ring, pause icon glyph |
| `series/SeriesDetailScreen.kt` hero | `components/comp/series/detail/series-hero.tsx` | 🔶 approximate — needs a real pass |
| `series/SeriesDetailScreen.kt` rows | `components/comp/series/detail/chapter-row.tsx` | ✅ full transliteration (status icon trio, subtitle line, % chip, bookmark, actions) |
| `updates/UpdatesScreen.kt` | `app/updates/page.tsx` | ✅ header+count+Update now, date buckets, icon subtitles, relative times, read-dim+check; ❌ stacked chapter runs |
| `browse/BrowseScreen.kt` | `app/cloud-latest/page.tsx` | 🔶 grid OK; ❌ spotlight hero, add-to-library |
| `queue/QueueScreen.kt` | `app/queue/page.tsx` | ✅ status-dot+thumb rows, chapter+retries, provider·scanlator, lowercase status; ❌ date buckets, retry/remove actions, All filter |
| `auth/ConnectScreen.kt` | old `res/layout/activity_main.xml` serverPanel | ✅ pixel-faithful |
| `auth/LoginScreen.kt` | `app/login/page.tsx` | ✅ card + banner |
| `reader/ReaderScreen.kt` | `app/reader/page.tsx` | 🔶 core modes + tracking OK; ❌ settings sheet, tap zones config, zoom |
| `settings/AccountScreen.kt` | `app/account/page.tsx` | ❌ minimal placeholder |

## Gotchas learned porting

- M3 `Button` does NOT take shapes from the theme — pass
  `shape = MaterialTheme.shapes.small` explicitly or you get Material pills.
  (`IconButton` has no shape param at all — don't add one.)
- Web `text-[11px]`≈labelSmall, `text-sm`≈bodyMedium/titleSmall(medium),
  `h-8` buttons = 32dp, `px-3 py-2.5` = 12dp/10dp.
- Tailwind palette hexes used by the web UI: emerald-500 #10B981,
  amber-500 #F59E0B, violet-400 #A78BFA, pink-500 #EC4899, blue-500 #3B82F6,
  yellow-500 #EAB308, green-500 #22C55E.
- Chapter rows need BOTH `api/serie/chapters` (downloaded/source/date/locked)
  and `api/reader/chapters` (progress/completed/bookmarked), joined by
  chapter number — same join the web chapters-section does.
- Fonts: Geist Sans v1.7.2 (OFL) is bundled in res/font and wired through
  RenzoTypography — typography now matches the web.
- Full mobile-web reference screenshot set received 2026-08-01 (nav drawer,
  avatar dropdown, library, series hero/sources/chapters, reader chrome +
  settings sheet, updates, browse spotlight, queue, status, sources,
  account, settings). Remaining pass-2 targets: series hero stacked layout
  + icon action row + storage path, chapters toolbar chips + count header,
  series sources card, reader top bar/bottom scrubber/settings sheet,
  native Status + Sources screens, Account page sections.
