package app.renzoshiori.client.ui.reader

import android.content.Context

/**
 * Reader settings — a 1:1 transliteration of the web reader's `ReaderSettings`
 * (RenzoFrontend/src/app/reader/page.tsx). Same option set, same labels, same
 * defaults. Persisted in SharedPreferences, which is this client's localStorage:
 * the web stores the blob under "renzo_reader_settings" and the per-series mode
 * override under "renzo_reader_mode_<seriesId>" — both mirrored here.
 *
 * The web's `hotkeys` map is deliberately absent: it rebinds physical keyboard
 * keys, which a touch client has no equivalent for (its actions — page turn,
 * chapter skip, chrome/settings/chapter-list toggles, bookmark, exit — are all
 * reachable from the chrome and tap zones instead).
 */
enum class ReaderMode(val value: String, val label: String) {
    AUTO("auto", "Auto (smart detect)"),
    PAGED("paged", "Paged — left to right"),
    PAGED_RTL("paged-rtl", "Paged — right to left"),
    DOUBLE("double", "Double page"),
    WEBTOON("webtoon", "Webtoon (no gaps)"),
    LONGSTRIP("longstrip", "Long strip (width-matched)"),
    VERTICAL("vertical", "Vertical (with gaps)");

    companion object {
        fun from(value: String?): ReaderMode = values().firstOrNull { it.value == value } ?: AUTO
    }
}

enum class FitMode(val value: String, val label: String) {
    WIDTH("width", "Fit width"),
    HEIGHT("height", "Fit height"),
    ORIGINAL("original", "Original size");

    companion object {
        fun from(value: String?): FitMode = values().firstOrNull { it.value == value } ?: WIDTH
    }
}

/** Web `BG` map: black #000, gray #18181b, white #fafafa. */
enum class ReaderBackground(val value: String, val label: String, val argb: Long) {
    BLACK("black", "Black", 0xFF000000L),
    GRAY("gray", "Dark gray", 0xFF18181BL),
    WHITE("white", "White", 0xFFFAFAFAL);

    companion object {
        fun from(value: String?): ReaderBackground = values().firstOrNull { it.value == value } ?: BLACK
    }
}

/** A reading mode with "auto" already resolved — the web's `resolvedMode`. */
enum class ResolvedMode {
    PAGED, PAGED_RTL, DOUBLE, WEBTOON, LONGSTRIP, VERTICAL;

    /** webtoon / longstrip / vertical — the infinite strip modes. */
    val continuous: Boolean get() = this == WEBTOON || this == LONGSTRIP || this == VERTICAL
    val rtl: Boolean get() = this == PAGED_RTL

    companion object {
        fun of(mode: ReaderMode): ResolvedMode = when (mode) {
            ReaderMode.PAGED_RTL -> PAGED_RTL
            ReaderMode.DOUBLE -> DOUBLE
            ReaderMode.WEBTOON -> WEBTOON
            ReaderMode.LONGSTRIP -> LONGSTRIP
            ReaderMode.VERTICAL -> VERTICAL
            else -> PAGED
        }
    }
}

data class ReaderSettings(
    val mode: ReaderMode = ReaderMode.AUTO,
    val fit: FitMode = FitMode.WIDTH,
    /** % of viewport width cap in continuous modes. */
    val maxWidthPct: Int = 60,
    val background: ReaderBackground = ReaderBackground.BLACK,
    val preload: Int = 4,
    /** Vertical-mode gap, in px on the web → dp here. */
    val gapPx: Int = 12,
    val showPageNumber: Boolean = true,
    val tapNavigation: Boolean = true,
    /** Continuous tap-to-scroll step, as % of viewport height. */
    val tapAdvancePct: Int = 80,
    /** Continuous: append the next chapter at the bottom. */
    val infiniteScroll: Boolean = true,
    /** Show a "finished / up next" screen between chapters (paged: its own page). */
    val chapterTransition: Boolean = true,
    val autoMarkRead: Boolean = true,
    /** Clear the streamed-page cache when leaving the reader. */
    val autoClearCache: Boolean = true,
)

/** SharedPreferences-backed store for [ReaderSettings] + the per-series mode override. */
class ReaderPrefs(context: Context) {
    private val prefs = context.applicationContext
        .getSharedPreferences("renzo_reader_settings", Context.MODE_PRIVATE)

    fun load(): ReaderSettings {
        val d = ReaderSettings()
        return ReaderSettings(
            mode = ReaderMode.from(prefs.getString(KEY_MODE, d.mode.value)),
            fit = FitMode.from(prefs.getString(KEY_FIT, d.fit.value)),
            maxWidthPct = prefs.getInt(KEY_MAX_WIDTH, d.maxWidthPct),
            background = ReaderBackground.from(prefs.getString(KEY_BACKGROUND, d.background.value)),
            preload = prefs.getInt(KEY_PRELOAD, d.preload),
            gapPx = prefs.getInt(KEY_GAP, d.gapPx),
            showPageNumber = prefs.getBoolean(KEY_SHOW_PAGE_NUMBER, d.showPageNumber),
            tapNavigation = prefs.getBoolean(KEY_TAP_NAVIGATION, d.tapNavigation),
            tapAdvancePct = prefs.getInt(KEY_TAP_ADVANCE, d.tapAdvancePct),
            infiniteScroll = prefs.getBoolean(KEY_INFINITE_SCROLL, d.infiniteScroll),
            chapterTransition = prefs.getBoolean(KEY_CHAPTER_TRANSITION, d.chapterTransition),
            autoMarkRead = prefs.getBoolean(KEY_AUTO_MARK_READ, d.autoMarkRead),
            autoClearCache = prefs.getBoolean(KEY_AUTO_CLEAR_CACHE, d.autoClearCache),
        )
    }

    fun save(s: ReaderSettings) {
        prefs.edit()
            .putString(KEY_MODE, s.mode.value)
            .putString(KEY_FIT, s.fit.value)
            .putInt(KEY_MAX_WIDTH, s.maxWidthPct)
            .putString(KEY_BACKGROUND, s.background.value)
            .putInt(KEY_PRELOAD, s.preload)
            .putInt(KEY_GAP, s.gapPx)
            .putBoolean(KEY_SHOW_PAGE_NUMBER, s.showPageNumber)
            .putBoolean(KEY_TAP_NAVIGATION, s.tapNavigation)
            .putInt(KEY_TAP_ADVANCE, s.tapAdvancePct)
            .putBoolean(KEY_INFINITE_SCROLL, s.infiniteScroll)
            .putBoolean(KEY_CHAPTER_TRANSITION, s.chapterTransition)
            .putBoolean(KEY_AUTO_MARK_READ, s.autoMarkRead)
            .putBoolean(KEY_AUTO_CLEAR_CACHE, s.autoClearCache)
            .apply()
    }

    /** Per-series mode override ("auto" is stored as absence, exactly like the web). */
    fun seriesMode(seriesId: String): ReaderMode? =
        prefs.getString(seriesModeKey(seriesId), null)?.let { ReaderMode.from(it) }

    fun setSeriesMode(seriesId: String, mode: ReaderMode?) {
        val editor = prefs.edit()
        if (mode == null || mode == ReaderMode.AUTO) editor.remove(seriesModeKey(seriesId))
        else editor.putString(seriesModeKey(seriesId), mode.value)
        editor.apply()
    }

    private fun seriesModeKey(seriesId: String) = "renzo_reader_mode_$seriesId"

    private companion object {
        const val KEY_MODE = "mode"
        const val KEY_FIT = "fit"
        const val KEY_MAX_WIDTH = "maxWidthPct"
        const val KEY_BACKGROUND = "background"
        const val KEY_PRELOAD = "preload"
        const val KEY_GAP = "gapPx"
        const val KEY_SHOW_PAGE_NUMBER = "showPageNumber"
        const val KEY_TAP_NAVIGATION = "tapNavigation"
        const val KEY_TAP_ADVANCE = "tapAdvancePct"
        const val KEY_INFINITE_SCROLL = "infiniteScroll"
        const val KEY_CHAPTER_TRANSITION = "chapterTransition"
        const val KEY_AUTO_MARK_READ = "autoMarkRead"
        const val KEY_AUTO_CLEAR_CACHE = "autoClearCache"
    }
}
