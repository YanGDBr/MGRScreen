package com.mgrscreen.viewer

/**
 * Maps publisher PTS to the local monotonic clock. Learn from video arrival times,
 * and slew the mapping so a burst does not abruptly move the playback deadline.
 */
internal class PlayoutClock {
    private var lastPtsUs: Long? = null
    private var baseTransitNs = 0L
    private var variationNs = 25_000_000L
    private var targetOffsetNs = 0L
    @Volatile private var offsetNs: Long? = null

    @Synchronized fun observe(ptsUs: Long, arrivalNs: Long) {
        val transit = arrivalNs - ptsUs * 1000
        val previous = lastPtsUs
        if (previous != null && ptsUs <= previous) return
        lastPtsUs = ptsUs
        val elapsed = if (previous == null) 0L else (ptsUs - previous).coerceAtMost(250_000L) * 1000
        baseTransitNs = if (previous == null) transit else minOf(transit, baseTransitNs + elapsed / 50)
        variationNs = maxOf(variationNs - elapsed / 25, transit - baseTransitNs).coerceIn(0L, 165_000_000L)
        targetOffsetNs = baseTransitNs + 65_000_000L + variationNs
        offsetNs?.let {
            offsetNs = it + (targetOffsetNs - it).coerceIn(-elapsed / 20, elapsed / 4)
        }
    }

    @Synchronized fun start(ptsUs: Long, nowNs: Long) {
        if (offsetNs == null)
            offsetNs = maxOf(targetOffsetNs, nowNs - ptsUs * 1000 + 12_000_000L)
    }

    fun deadline(ptsUs: Long): Long? = offsetNs?.let { ptsUs * 1000 + it }
}
