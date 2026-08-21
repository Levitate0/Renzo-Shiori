package app.renzoshiori.client.ui.util

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.State
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue

/**
 * Adult (18+) view filter — the native twin of the web's
 * lib/utils/adult-filter.ts. It filters Library/Browse rendering only; the
 * server-side `nsfwVisibility` setting is a different thing entirely (that one
 * filters the Sources list).
 *
 * The flag is snapshot state, not just a preference read: the web broadcasts a
 * `renzo-hide-adult-changed` event so every mounted view re-filters the moment
 * the menu item is clicked. Reading a plain SharedPreferences boolean inside a
 * composable gives no such signal — the grids would keep showing the old set
 * until the app was restarted.
 */
object AdultFilter {
    private const val PREFS = "renzo_prefs"
    private const val KEY = "renzo_hide_adult"

    /** Tag set the web classifies on, verbatim. */
    // Kept in step with the server's AdultContentClassifier.cs and the web's
    // adult-filter.ts. Explicit RATING/act tags only — ecchi/mature/seinen/josei/
    // harem are deliberately absent (this targets 18+, not fanservice), as are
    // orientation genres and formats.
    private val ADULT_TAGS = setOf(
        "hentai", "erotica", "erotic", "adult", "smut", "pornographic", "porn",
        "18+", "r18", "r-18", "r18+", "r-18g", "nsfw", "adult (18+)", "explicit",
        // Explicit acts/kinks: a source tagging "Blowjob" but not "Adult" would
        // otherwise pass straight through the filter.
        "blowjob", "double penetration", "sex toys", "sexual violence",
        "sexual abuse", "rape", "incest", "netorare", "ntr", "bdsm",
        "bestiality", "futanari", "shemale", "dickgirl", "milf", "nudity",
        // Sexualised minors — never reachable with 18+ hidden.
        "loli", "lolicon", "shota", "shotacon",
    )

    /**
     * The comparable pieces of a raw tag. Sources dress the same rating up in
     * different ways: MangaDex-style prefixes ("Content rating: Pornographic")
     * and bundled alternatives ("Futanari | Shemale | Dickgirl").
     */
    private fun tagParts(raw: String): List<String> {
        var tag = raw.trim()
        val colon = tag.indexOf(':')
        if (colon > 0 && colon < tag.length - 1) {
            val prefix = tag.substring(0, colon).trim().lowercase()
            if (prefix == "content rating" || prefix == "rating" || prefix == "genre") {
                tag = tag.substring(colon + 1).trim()
            }
        }
        val parts = mutableListOf(tag)
        if (tag.contains('|') || tag.contains('/')) {
            tag.split('|', '/').forEach { p -> p.trim().takeIf { it.isNotEmpty() }?.let(parts::add) }
        }
        return parts
    }

    /** True when a single tag name is an explicit 18+ rating. */
    fun isAdultTag(tag: String): Boolean =
        tagParts(tag).any { it.lowercase() in ADULT_TAGS }

    private var hiddenState by mutableStateOf(false)

    /**
     * Loads the persisted flag. Called once from RenzoApp.onCreate.
     *
     * Defaults to HIDDEN. A fresh install shows nothing adult until the user
     * asks for it — the catalogue a source returns is not under our control,
     * so opt-in is the only defensible default (and it's what the store
     * questionnaire is really asking about).
     */
    fun init(context: Context) {
        hiddenState = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(KEY, true)
    }

    /** Observable — a composable that reads this recomposes when it changes. */
    fun isHidden(context: Context): Boolean = hiddenState

    fun setHidden(context: Context, hidden: Boolean) {
        hiddenState = hidden
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY, hidden).apply()
    }

    /** Web `isAdultItem`: prefer the server's flag, fall back to the tags. */
    fun isAdultItem(isNsfw: Boolean?, genres: List<String>): Boolean =
        // OR, not elvis: the web is `isNsfw === true || tags`. With `?:` a FALSE
        // flag short-circuits and the tags are never checked, so where the server
        // flag and the tags disagree this showed what the web hides.
        isNsfw == true || genres.any { isAdultTag(it) }
}

/** Compose-friendly handle mirroring the web's `useHideAdult()` hook. */
class HideAdultState(private val context: Context) {
    /** Derived from the shared flag, so it tracks whoever last changed it. */
    val hidden: State<Boolean> = derivedStateOf { AdultFilter.isHidden(context) }

    fun toggle() = AdultFilter.setHidden(context, !AdultFilter.isHidden(context))
}

@Composable
fun rememberHideAdult(context: Context): HideAdultState = remember { HideAdultState(context) }
