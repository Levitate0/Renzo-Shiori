package xyz.nulldev.androidcompat.webkit

import java.util.concurrent.atomic.AtomicLong

/**
 * Browser lifecycle counters for the sidecar's /status. Kept in its own object
 * with no dependencies on purpose: reading KcefWebViewProvider's companion
 * would run its initialiser, which needs Koin, and /status answers before
 * Koin is set up.
 */
object KcefStats {
    /** WebViews (providers) initialised. */
    val created = AtomicLong()

    /** Browser closes Shiori asked CEF for. */
    val closesRequested = AtomicLong()

    /** Browsers CEF reported closed (onBeforeClose). */
    val closed = AtomicLong()

    /** Closes asked for and not yet confirmed. */
    val pendingCloses = AtomicLong()

    /** Renderer processes at the last reaper scan (-1 before the first). */
    val renderers = AtomicLong(-1)

    /** All live jcef_helper processes at the last scan. */
    val helpers = AtomicLong(-1)

    val lastScanAt = AtomicLong()

    /** Renderers force-exited by the nothing-live sweep. */
    val leftoverKills = AtomicLong()

    /** Renderers force-exited by the ceiling valve. */
    val valveKills = AtomicLong()

    fun snapshot(): Map<String, Long> = linkedMapOf(
        "created" to created.get(),
        "closesRequested" to closesRequested.get(),
        "closed" to closed.get(),
        "pendingCloses" to pendingCloses.get(),
        "renderers" to renderers.get(),
        "helpers" to helpers.get(),
        "lastScanAgoMs" to (lastScanAt.get().takeIf { it > 0 }?.let { System.currentTimeMillis() - it } ?: -1),
        "leftoverKills" to leftoverKills.get(),
        "valveKills" to valveKills.get(),
    )
}
