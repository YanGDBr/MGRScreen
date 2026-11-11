package com.mgrscreen.viewer

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import org.json.JSONException
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.net.SocketTimeoutException

internal data class RoomAddress(
    val name: String, val host: String, val control: Int, val media: Int,
    val participants: Int, val route: String,
) {
    val key get() = host to control
}

internal object RoomDiscovery {
    private const val PORT = 51902
    private const val LAN_REPLY = "MGRSCREEN-ROOM-1"
    private const val TAILSCALE_PUSH = "MGRSCREEN-ROOM-PUSH-1"

    suspend fun scan(knownHosts: List<Endpoint>): List<RoomAddress> = coroutineScope {
        val lan = async(Dispatchers.IO) { networkAttempt { scanLan() }.orEmpty() }
        val known = knownHosts.distinctBy { it.host to it.control }.take(6).map { target ->
            async(Dispatchers.IO) { networkAttempt { probe(target) } }
        }
        (lan.await() + known.awaitAll().filterNotNull()).distinctBy { it.key }
    }

    suspend fun listenForTailscale(onRoom: suspend (RoomAddress) -> Unit) {
        val socket = networkAttempt {
            DatagramSocket(null).apply {
                try {
                    bind(InetSocketAddress(PORT))
                    soTimeout = 1000
                } catch (error: Exception) {
                    close()
                    throw error
                }
            }
        } ?: return
        socket.use {
            val bytes = ByteArray(4096)
            while (currentCoroutineContext().isActive) {
                val packet = DatagramPacket(bytes, bytes.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                } catch (_: IOException) {
                    return
                }
                if (!isTailscale(packet.address)) continue
                val room = networkAttempt { announcement(packet, TAILSCALE_PUSH, "Tailscale") }
                if (room != null) onRoom(room)
            }
        }
    }

    private fun scanLan(): List<RoomAddress> = DatagramSocket().use { socket ->
        socket.broadcast = true
        val addresses = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
            .flatMap { network -> network.interfaceAddresses.mapNotNull { it.broadcast } }
            .plus(InetAddress.getByName("255.255.255.255")).distinct()
        val query = "MGRSCREEN-PROBE-1".toByteArray(Charsets.US_ASCII)
        addresses.forEach { address ->
            networkAttempt { socket.send(DatagramPacket(query, query.size, address, PORT)) }
        }

        val deadline = System.nanoTime() + 1_300_000_000L
        val rooms = linkedMapOf<Pair<String, Int>, RoomAddress>()
        val bytes = ByteArray(4096)
        while (System.nanoTime() < deadline && rooms.size < 50) {
            socket.soTimeout = ((deadline - System.nanoTime()) / 1_000_000).coerceAtLeast(1).toInt()
            val packet = DatagramPacket(bytes, bytes.size)
            try { socket.receive(packet) } catch (_: SocketTimeoutException) { break }
            val room = networkAttempt { announcement(packet, LAN_REPLY, "Rede local") }
            if (room != null) rooms[room.key] = room
        }
        rooms.values.toList()
    }

    private fun announcement(packet: DatagramPacket, magic: String, route: String): RoomAddress? {
        val json = JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
        if (json.optString("Magic") != magic) return null
        val host = packet.address.hostAddress ?: return null
        val control = json.optInt("ControlPort")
        val media = json.optInt("MediaPort")
        if (control !in 1..65535 || media !in 1..65535) return null
        return RoomAddress(json.optString("HostNickname").ifBlank { host }, host, control, media,
            json.optInt("ParticipantCount").coerceAtLeast(0), route)
    }

    private fun probe(target: Endpoint): RoomAddress? = Socket().use { socket ->
        socket.connect(roomSocketAddress(target.host, target.control), 1000)
        socket.soTimeout = 1000
        writeRoomMessage(DataOutputStream(socket.getOutputStream()), JSONObject().put("Kind", 10))
        val reply = readRoomMessage(DataInputStream(socket.getInputStream()), maxSize = 8192)
        if (reply.optInt("Kind") != 11) return null
        val advertisedPort = reply.optInt("MediaPort").takeIf { it in 1..65535 } ?: target.media
        val route = if (isTailscale(socket.inetAddress) || target.host.endsWith(".ts.net", true))
            "Tailscale" else "Endereço conhecido"
        val resolved = socket.inetAddress.hostAddress ?: target.host
        RoomAddress(reply.optString("Nickname").ifBlank { target.host }, resolved, target.control,
            advertisedPort, reply.optInt("ParticipantCount").coerceAtLeast(0), route)
    }

    internal fun isTailscale(address: InetAddress): Boolean {
        val bytes = address.address
        return bytes.size == 4 && bytes[0] == 100.toByte() && (bytes[1].toInt() and 255) in 64..127
    }

    private inline fun <T> networkAttempt(block: () -> T): T? = try {
        block()
    } catch (_: IOException) {
        null
    } catch (_: JSONException) {
        null
    } catch (_: SecurityException) {
        null
    }
}
