package app.renzoshiori.client.data.auth

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow

/**
 * App-wide announcement that the session is over — the token expired, was
 * revoked, or the server no longer accepts it.
 *
 * Without this a dead session looks like a broken app: every request fails 401,
 * the library renders empty, the series page shows nothing, and there is no
 * hint that the fix is to sign in again. The auth layer listens here and sends
 * the user back to the login screen instead.
 *
 * Deliberately a hot [SharedFlow] with no replay: a subscriber that connects
 * later shouldn't be kicked out by an expiry that was already handled.
 */
object SessionEvents {
    private val _expired = MutableSharedFlow<Unit>(extraBufferCapacity = 1)
    val expired: SharedFlow<Unit> = _expired.asSharedFlow()

    /** Safe to call from any thread, and from a burst of concurrent failures. */
    fun notifyExpired() {
        _expired.tryEmit(Unit)
    }
}
