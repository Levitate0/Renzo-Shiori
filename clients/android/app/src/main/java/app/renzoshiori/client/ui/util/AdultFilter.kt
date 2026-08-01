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
    private val ADULT_TAGS = setOf(
        "hentai", "erotica", "adult", "smut", "pornographic", "porn",
        "18+", "r18", "r-18", "r18+", "r-18g", "nsfw",
    )

    private var hiddenState by mutableStateOf(false)

    /** Loads the persisted flag. Called once from RenzoApp.onCreate. */
    fun init(context: Context) {
        hiddenState = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(KEY, false)
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
        isNsfw ?: genres.any { it.trim().lowercase() in ADULT_TAGS }
}

/** Compose-friendly handle mirroring the web's `useHideAdult()` hook. */
class HideAdultState(private val context: Context) {
    /** Derived from the shared flag, so it tracks whoever last changed it. */
    val hidden: State<Boolean> = derivedStateOf { AdultFilter.isHidden(context) }

    fun toggle() = AdultFilter.setHidden(context, !AdultFilter.isHidden(context))
}

@Composable
fun rememberHideAdult(context: Context): HideAdultState = remember { HideAdultState(context) }
