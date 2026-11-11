package com.mgrscreen.viewer

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PlayoutTest {
    @Test fun absorbsRepeatedNetworkJitterAndReturnsToLowDelay() {
        val clock = PlayoutClock()
        val localOffset = 5_000_000_000L
        val firstPts = 1_000_000L
        clock.observe(firstPts, localOffset + firstPts * 1000)
        assertNull(clock.deadline(firstPts))
        clock.start(firstPts, localOffset + firstPts * 1000)
        var previousDeadline = clock.deadline(firstPts)!!
        var previousArrival = localOffset + firstPts * 1000
        var delayedFramesAbsorbed = 0
        for (index in 1..240) {
            val pts = firstPts + index * 33_333L
            val burst = index in 30..90 && index % 5 < 2
            val nominalArrival = localOffset + pts * 1000
            val arrival = maxOf(previousArrival + 1_000_000,
                nominalArrival + if (burst) 140_000_000 else 0)
            clock.observe(pts, arrival)
            val deadline = clock.deadline(pts)!!
            assertTrue("Presentation stays ordered as the buffer changes", deadline > previousDeadline)
            if (burst && deadline >= arrival) delayedFramesAbsorbed++
            previousDeadline = deadline
            previousArrival = arrival
        }
        assertTrue("Most repeated 140 ms bursts should land before presentation", delayedFramesAbsorbed >= 18)
        val lastPts = firstPts + 240 * 33_333L
        assertTrue("The extra buffer should shrink after the network settles",
            clock.deadline(lastPts)!! - (localOffset + lastPts * 1000) < 105_000_000L)
    }

    @Test fun staleVideoResumesAtTheFirstDecodableFrame() {
        val queue = VideoBacklog()
        assertFalse(queue.offer(packet(0)))
        assertTrue(queue.offer(packet(0, keyframe = true)))
        for (index in 1..13) assertTrue(queue.offer(packet(index * 33_333L)))
        assertFalse(queue.offer(packet(14 * 33_333L)))
        assertNull(queue.poll())
        assertFalse(queue.offer(packet(700_000)))
        assertTrue(queue.offer(packet(800_000, keyframe = true)))
        assertTrue(queue.offer(packet(833_333)))
        assertEquals(800_000L, queue.poll()!!.also { assertTrue(it.resetDecoder) }.packet.ptsUs)
        assertFalse(queue.poll()!!.resetDecoder)
    }

    @Test fun retainsRegularSixtyFpsFramesWithinTheLatencyBudget() {
        val queue = VideoBacklog()
        for (index in 0..26) assertTrue(queue.offer(packet(index * 16_667L, keyframe = index == 0)))
        for (index in 0..26) {
            val input = queue.poll()!!
            assertEquals(index * 16_667L, input.packet.ptsUs)
            assertFalse(input.resetDecoder)
        }
    }

    private fun packet(ptsUs: Long, keyframe: Boolean = false) =
        Packet(type = 1, publisher = 1, generation = 1, ptsUs = ptsUs, keyframe = keyframe, data = byteArrayOf(1))
}
