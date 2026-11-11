package com.mgrscreen.viewer

import java.net.InetAddress
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RoomDiscoveryTest {
    @Test fun acceptsOnlyTheTailscaleIpv4RangeForUnsolicitedAnnouncements() {
        assertTrue(RoomDiscovery.isTailscale(InetAddress.getByName("100.64.0.1")))
        assertTrue(RoomDiscovery.isTailscale(InetAddress.getByName("100.127.255.254")))
        assertFalse(RoomDiscovery.isTailscale(InetAddress.getByName("100.63.255.254")))
        assertFalse(RoomDiscovery.isTailscale(InetAddress.getByName("100.128.0.1")))
        assertFalse(RoomDiscovery.isTailscale(InetAddress.getByName("192.168.1.1")))
    }

    @Test fun rejectsBlankNicknameBeforeOpeningSockets() = runBlocking {
        val error = runCatching {
            RoomConnection().run(Endpoint("127.0.0.1", " \t ", 51900, 51901), {}, {}, {})
        }.exceptionOrNull()
        assertTrue(error is Rejected)
        assertEquals("Informe seu apelido para entrar na sala.", error?.message)
    }
}
