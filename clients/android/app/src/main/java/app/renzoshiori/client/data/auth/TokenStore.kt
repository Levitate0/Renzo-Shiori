package app.renzoshiori.client.data.auth

import android.content.Context
import android.content.SharedPreferences
import android.util.Log
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * Encrypted replacement for the old WebView bridge's plaintext SharedPreferences
 * bearer-token storage (RenzoStore.kt's `download_jobs` queue carried a raw
 * token string). Holds the current JWT access token, the connected server's
 * base URL, and the last-known signed-in username (used to prefill login).
 *
 * The httpOnly refresh-token cookie itself is NOT held here — that's handled
 * by OkHttp's CookieJar (see NetworkModule) exactly as the browser/WebView did.
 */
class TokenStore(context: Context) {
    private val prefs: SharedPreferences = openOrReset(context)

    var accessToken: String?
        get() = prefs.getString(KEY_TOKEN, null)
        set(value) = prefs.edit().putString(KEY_TOKEN, value).apply()

    var serverUrl: String?
        get() = prefs.getString(KEY_SERVER, null)
        set(value) = prefs.edit().putString(KEY_SERVER, value).apply()

    var lastUsername: String?
        get() = prefs.getString(KEY_USERNAME, null)
        set(value) = prefs.edit().putString(KEY_USERNAME, value).apply()

    /**
     * The server's httpOnly `refresh_token` cookie, kept so "Remember me"
     * survives the app being closed. Holding it only in OkHttp's in-memory
     * cookie jar meant the access token's ~24h lifetime was the real session
     * length no matter what the user ticked — the refresh cookie (valid for
     * the server's rememberMeExpirationDays, 90 by default) was thrown away on
     * every process death.
     */
    var refreshCookie: String?
        get() = prefs.getString(KEY_REFRESH, null)
        set(value) = prefs.edit().putString(KEY_REFRESH, value).apply()

    /** Sign-out / expiry: drop the access token AND the remember-me cookie. */
    fun clearSession() {
        prefs.edit().remove(KEY_TOKEN).remove(KEY_REFRESH).apply()
    }

    companion object {
        private const val KEY_TOKEN = "access_token"
        private const val KEY_SERVER = "server_url"
        private const val KEY_USERNAME = "last_username"
        private const val KEY_REFRESH = "refresh_cookie"
        private const val PREFS_NAME = "renzo_secure"

        /** androidx.security's default MasterKey alias. */
        private const val MASTER_KEY_ALIAS = "_androidx_security_master_key_"

        private fun open(context: Context): SharedPreferences {
            val masterKey = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            return EncryptedSharedPreferences.create(
                context,
                PREFS_NAME,
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
            )
        }

        /**
         * The encrypted store can become permanently undecryptable while its
         * file still exists — the hardware-backed master key is invalidated
         * but the Tink keyset it wrapped survives, and every read then dies
         * with AEADBadTagException ("Signature/MAC verification failed").
         * Reinstalling over a build signed with a different key does it, as
         * can a device restore or a Keystore reset.
         *
         * This is unrecoverable by design — the ciphertext is genuinely lost —
         * but it must not be fatal: the only things in here are a JWT, a
         * server URL and a username, all re-obtainable by signing in again.
         * So on failure we throw the corrupted keyset and master key away and
         * start clean, rather than letting the app crash on launch forever.
         */
        private fun openOrReset(context: Context): SharedPreferences = try {
            open(context)
        } catch (e: Throwable) {
            Log.w("TokenStore", "Encrypted prefs unreadable — resetting; sign-in required", e)
            runCatching { context.deleteSharedPreferences(PREFS_NAME) }
            runCatching {
                java.security.KeyStore.getInstance("AndroidKeyStore")
                    .apply { load(null) }
                    .deleteEntry(MASTER_KEY_ALIAS)
            }
            open(context)
        }
    }
}
