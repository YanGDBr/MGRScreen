package com.mgrscreen.viewer

import java.util.ArrayDeque

internal data class VideoInput(val packet: Packet, val resetDecoder: Boolean)

/** Keep complete reference chains. A local overflow can only resume at an IDR. */
internal class VideoBacklog {
    private val items = ArrayDeque<VideoInput>()
    private var bytes = 0
    private var needIdr = true
    private var resetDecoder = false

    @Synchronized fun offer(packet: Packet): Boolean {
        if (packet.data.size > MAX_BYTES) return false
        val oldest = items.peekFirst()
        if (items.size >= MAX_FRAMES || bytes + packet.data.size > MAX_BYTES ||
            (oldest != null && packet.ptsUs - oldest.packet.ptsUs > MAX_SPAN_US)) {
            clear()
            needIdr = true
            resetDecoder = true
        }
        if (needIdr && !packet.keyframe) return false
        items.addLast(VideoInput(packet, resetDecoder))
        bytes += packet.data.size
        needIdr = false
        resetDecoder = false
        return true
    }

    @Synchronized fun poll(): VideoInput? = items.pollFirst()?.also { bytes -= it.packet.data.size }

    @Synchronized fun clear() {
        items.clear()
        bytes = 0
    }

    private companion object {
        const val MAX_BYTES = 6 * 1024 * 1024
        const val MAX_FRAMES = 48
        const val MAX_SPAN_US = 450_000L
    }
}
