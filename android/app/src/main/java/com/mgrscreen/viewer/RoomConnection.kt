package com.mgrscreen.viewer

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

internal data class Endpoint(val host: String, val nickname: String, val control: Int, val media: Int)
internal data class StreamKey(val publisher: Int, val kind: Int)
internal data class StreamInfo(
    val key: StreamKey, val name: String, val width: Int, val height: Int, val fps: Int,
    val generation: Long, val hasAudio: Boolean, val sampleRate: Int, val channels: Int,
) {
    val isPlayable get() = generation > 0 && width in 2..8192 && height in 2..4320 && fps in 1..120
    val pcmSupported get() = hasAudio && sampleRate in 8000..192000 && channels in 1..2

    companion object {
        fun parse(json: JSONObject) = StreamInfo(
            StreamKey(json.getInt("ParticipantId"), json.optInt("Kind")),
            json.optString("Nickname", "Amigo"), json.getInt("Width"), json.getInt("Height"),
            json.getInt("Fps"), json.optLong("Generation"), json.optBoolean("HasAudio"),
            json.optInt("AudioSampleRate", 48000), json.optInt("AudioChannels", 2),
        )
    }
}

internal data class Selection(val stream: StreamInfo?, val audio: Boolean = false)
internal data class Packet(
    val type: Int, val publisher: Int, val generation: Long, val ptsUs: Long,
    val keyframe: Boolean, val data: ByteArray,
)
internal class Rejected(message: String) : IOException(message)

internal fun roomSocketAddress(host: String, port: Int): InetSocketAddress {
    val addresses = InetAddress.getAllByName(host)
    // The Windows room currently binds IPv4. MagicDNS can return both families.
    return InetSocketAddress(addresses.firstOrNull { it is Inet4Address } ?: addresses.first(), port)
}

internal fun writeRoomMessage(output: DataOutputStream, message: JSONObject) = synchronized(output) {
    val bytes = message.toString().toByteArray(Charsets.UTF_8)
    output.writeInt(Integer.reverseBytes(bytes.size))
    output.write(bytes)
    output.flush()
}

internal fun readRoomMessage(input: DataInputStream, maxSize: Int = 1024 * 1024): JSONObject {
    val length = Integer.reverseBytes(input.readInt())
    if (length !in 1..maxSize) throw IOException("Mensagem da sala inválida.")
    return JSONObject(String(ByteArray(length).also { input.readFully(it) }, Charsets.UTF_8))
}

internal class RoomConnection : Closeable {
    private val control = Socket()
    private val media = Socket()
    private val commands = Channel<Selection>(Channel.CONFLATED)

    fun select(selection: Selection) {
        commands.trySend(selection)
    }

    suspend fun run(
        endpoint: Endpoint,
        onRoom: (List<StreamInfo>) -> Unit,
        onPacket: (Packet) -> Unit,
        onCongestion: () -> Unit,
    ) {
        try {
            if (endpoint.nickname.isBlank()) throw Rejected("Informe seu apelido para entrar na sala.")
            control.tcpNoDelay = true
            control.soTimeout = 8000
            control.connect(roomSocketAddress(endpoint.host, endpoint.control), 5000)
            val input = DataInputStream(BufferedInputStream(control.getInputStream()))
            val output = DataOutputStream(BufferedOutputStream(control.getOutputStream()))
            val join = JSONObject().put("Kind", 0).put("Nickname", endpoint.nickname)
                .put("ProtocolVersion", 2).put("ViewerOnly", true)
            writeRoomMessage(output, join)
            val accepted = readRoomMessage(input)
            if (accepted.optInt("Kind") == 2)
                throw Rejected(accepted.optString("Reason", "Entrada recusada pela sala."))
            if (accepted.optInt("Kind") != 1 || accepted.optInt("ProtocolVersion") != 2)
                throw Rejected("Atualize o MGRScreen no computador que criou a sala.")

            val token = accepted.getString("MediaToken")
            if (token.length != 32 || token.any { it !in '0'..'9' && it !in 'a'..'f' })
                throw IOException("Token de mídia inválido.")
            media.receiveBufferSize = 128 * 1024
            media.connect(InetSocketAddress(control.inetAddress, endpoint.media), 5000)
            media.getOutputStream().write(token.toByteArray(Charsets.US_ASCII))
            val mediaInput = DataInputStream(BufferedInputStream(media.getInputStream(), 65536))

            val streams = linkedMapOf<StreamKey, StreamInfo>()
            accepted.optJSONArray("Streams")?.let { list ->
                for (index in 0 until list.length()) {
                    val stream = StreamInfo.parse(list.getJSONObject(index))
                    streams[stream.key] = stream
                }
            }
            val participantId = accepted.getInt("ParticipantId")
            onRoom(streams.values.toList())

            coroutineScope {
                launch {
                    try {
                        while (true) {
                            val message = readRoomMessage(input)
                            val publisher = message.optInt("ParticipantId")
                            when (message.getInt("Kind")) {
                                6 -> streams.keys.removeAll { it.publisher == publisher }
                                7 -> {
                                    val stream = StreamInfo.parse(message.getJSONObject("Stream"))
                                    streams[stream.key] = stream
                                }
                                8 -> streams.remove(StreamKey(publisher, message.optInt("StreamKind")))
                                9 -> if (publisher == participantId) onCongestion()
                            }
                            if (message.getInt("Kind") in 6..8) onRoom(streams.values.toList())
                        }
                    } finally { close() }
                }
                launch {
                    try {
                        while (true) onPacket(readPacket(mediaInput))
                    } finally { close() }
                }
                launch {
                    try {
                        for (selection in commands) {
                            val info = selection.stream
                            val message = JSONObject().put("Kind", if (info == null) 15 else 14)
                            if (info != null) message.put("ParticipantId", info.key.publisher)
                                .put("StreamKind", info.key.kind).put("IncludeAudio", selection.audio)
                            writeRoomMessage(output, message)
                        }
                    } finally { close() }
                }
                launch {
                    try {
                        while (true) {
                            writeRoomMessage(output, JSONObject().put("Kind", 12))
                            delay(2000)
                        }
                    } finally { close() }
                }
            }
        } finally { close() }
    }

    override fun close() {
        commands.close()
        runCatching { control.close() }
        runCatching { media.close() }
    }

    private fun readPacket(input: DataInputStream): Packet {
        val type = input.readUnsignedByte()
        val publisher = Integer.reverseBytes(input.readInt())
        val length = Integer.reverseBytes(input.readInt())
        if (type !in 0x81..0x83 || length !in 26..(4 * 1024 * 1024 + 25))
            throw IOException("Pacote de mídia inválido.")
        val generation = java.lang.Long.reverseBytes(input.readLong())
        input.readLong() // Sequence, reserved for diagnostics.
        val ptsUs = java.lang.Long.reverseBytes(input.readLong())
        val flags = input.readUnsignedByte()
        if (generation <= 0 || ptsUs !in 0..1_000_000_000_000_000L || flags !in 0..1)
            throw IOException("Metadados inválidos.")
        val data = ByteArray(length - 25).also { input.readFully(it) }
        return Packet(type and 0x7f, publisher, generation, ptsUs, flags == 1, data)
    }
}
