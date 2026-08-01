package app.renzoshiori.client.ui.util

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.State
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember

/**
 * Adult (18+) view filter — the native twin of the web's
 * lib/utils/adult-filter.ts. Purely local state (the web keeps it in
 * localStorage, we keep it in SharedPreferences under the same key name), and
 * it filters Library/Browse rendering only; the server-side `nsfwVisibility`
 * setting is a different thing entirely (it filters the Sources list).
 */
object AdultFilter {
    private const val PREFS = "renzo_prefs"
    private const val KEY = "renzo_hide_adult"

    /** Tag set the web classifies on, verbatim. */
    private val ADULT_TAGS = setOf(
        "hentai", "erotica", "adult", "smut", "pornographic", "porn",
        "18+", "r18", "r-18", "r18+", "r-18g", "nsfw",
    )

    fun isHidden(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY, false)

    fun setHidden(context: Context, hidden: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY, hidden).apply()
    }

    /** Web `isAdultItem`: prefer the server's isNsfw flag, fall back to genres. */
    fun isAdultItem(isNsfw: Boolean?, genres: List<String>): Boolean =
        isNsfw ?: genres.any { it.trim().lowercase() in ADULT_TAGS }
}

/** Compose-friendly handle mirroring the web's `useHideAdult()` hook. */
class HideAdultState(private val context: Context) {
    private val backing = mutableStateOf(AdultFilter.isHidden(context))
    val hidden: State<Boolean> get() = backing

    fun toggle() {
        val next = !backing.value
        backing.value = next
        AdultFilter.setHidden(context, next)
    }
}

@Composable
fun rememberHideAdult(context: Context): HideAdultState = remember { HideAdultState(context) }
