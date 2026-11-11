package com.mgrscreen.viewer

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.Surface
import java.io.Closeable
import java.nio.ByteBuffer
import java.util.ArrayDeque
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.locks.LockSupport
import kotlin.math.abs

internal class Playback(
    private val video: StreamInfo,
    private val audio: StreamInfo?,
    private val surface: Surface,
    private val onError: (String) -> Unit,
) : Closeable {
    private val closed = AtomicBoolean()
    private val videoQueue = VideoBacklog()
    private val audioQueue = PacketQueue(32, 512 * 1024)
    private val videoThread = Thread(::decode, "MGRScreen video")
    private val audioThread = audio?.let { Thread({ playAudio(it) }, "MGRScreen audio") }
    private val clock = PlayoutClock()
    private val receivedBytes = AtomicLong()
    private val renderedFrames = AtomicLong()
    @Volatile private var volume = 1f

    private data class VideoOutput(val index: Int, val ptsUs: Long)

    fun start() {
        videoThread.start()
        audioThread?.start()
    }

    fun setVolume(value: Float) { volume = value.coerceIn(0f, 1f) }
    fun takeBytes() = receivedBytes.getAndSet(0)
    fun takeFrames() = renderedFrames.getAndSet(0)

    fun offer(packet: Packet) {
        if (closed.get() || packet.publisher != video.key.publisher) return
        if (packet.type == 2) {
            if (packet.generation != audio?.generation || audioQueue.offer(packet)) return
            audioQueue.clear()
            audioQueue.offer(packet)
            return
        }

        val type = if (video.key.kind == 1) 3 else 1
        if (packet.type != type || packet.generation != video.generation) return
        receivedBytes.addAndGet(packet.data.size.toLong())
        clock.observe(packet.ptsUs, System.nanoTime())
        if (videoQueue.offer(packet)) LockSupport.unpark(videoThread)
    }

    private fun decode() {
        var codec: MediaCodec? = null
        var pending: VideoInput? = null
        var futureOutput: VideoOutput? = null
        val info = MediaCodec.BufferInfo()
        try {
            while (!closed.get()) {
                if (pending == null) {
                    pending = videoQueue.poll()
                    if (pending?.resetDecoder == true) {
                        codec?.flush()
                        futureOutput = null
                    }
                }
                val packet = pending?.packet
                val decodeLead = packet?.let { clock.deadline(it.ptsUs) }?.minus(System.nanoTime())
                if (packet != null && (decodeLead == null || decodeLead <= DECODE_LEAD_NS)) {
                    if (codec == null) codec = createDecoder(packet.data)
                    val index = codec.dequeueInputBuffer(0)
                    if (index >= 0) {
                        val buffer = checkNotNull(codec.getInputBuffer(index))
                        buffer.clear()
                        require(buffer.remaining() >= packet.data.size) { "Frame maior que o buffer do decoder." }
                        buffer.put(packet.data)
                        codec.queueInputBuffer(index, 0, packet.data.size, packet.ptsUs, 0)
                        pending = null
                    }
                }

                if (codec != null) {
                    // Keep one future output locally. Sending several ahead of their VSYNC
                    // makes the Surface hold decoder buffers and can stall the input queue.
                    var ready: VideoOutput? = null
                    while (true) {
                        val output = futureOutput ?: run {
                            val index = codec.dequeueOutputBuffer(info, 0)
                            if (index >= 0) VideoOutput(index, info.presentationTimeUs) else null
                        } ?: break
                        val now = System.nanoTime()
                        clock.start(output.ptsUs, now)
                        val due = clock.deadline(output.ptsUs) ?: now
                        if (due - now > DISPLAY_LEAD_NS) {
                            futureOutput = output
                            break
                        }
                        futureOutput = null
                        // When the decoder catches up in a burst, show the newest due
                        // output. Discarding a decoded image leaves H.264 references intact.
                        ready?.let { codec.releaseOutputBuffer(it.index, false) }
                        ready = output
                    }
                    ready?.let {
                        val now = System.nanoTime()
                        codec.releaseOutputBuffer(it.index,
                            (clock.deadline(it.ptsUs) ?: now).coerceIn(now, now + DISPLAY_LEAD_NS))
                    }
                }
                if (packet == null || pending != null || futureOutput != null) {
                    val now = System.nanoTime()
                    val wakeAt = futureOutput?.let { clock.deadline(it.ptsUs) }
                    val delay = when {
                        wakeAt != null -> (wakeAt - now - DISPLAY_LEAD_NS).coerceIn(0, 4_000_000)
                        codec == null -> 20_000_000L
                        else -> 2_000_000L
                    }
                    if (delay > 0) LockSupport.parkNanos(delay)
                }
            }
        } catch (_: InterruptedException) {
        } catch (error: Exception) {
            if (!closed.get()) onError(error.message ?: "Falha ao decodificar vídeo.")
        } finally {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
        }
    }

    private fun createDecoder(data: ByteArray): MediaCodec {
        val sps = parameterSet(data, 7) ?: error("A transmissão não enviou a configuração H.264 (SPS).")
        val pps = parameterSet(data, 8) ?: error("A transmissão não enviou a configuração H.264 (PPS).")
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, video.width, video.height)
        format.setByteBuffer("csd-0", ByteBuffer.wrap(sps))
        format.setByteBuffer("csd-1", ByteBuffer.wrap(pps))
        format.setInteger(MediaFormat.KEY_FRAME_RATE, video.fps)
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1024 * 1024)
        val mime = MediaFormat.MIMETYPE_VIDEO_AVC
        val candidates = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .filter { !it.isEncoder && mime in it.supportedTypes }
            .sortedByDescending { it.isHardwareAccelerated }
        for (candidate in candidates) {
            val caps = candidate.getCapabilitiesForType(mime)
            if (!runCatching { caps.isFormatSupported(format) }.getOrDefault(false)) continue
            val codec = runCatching { MediaCodec.createByCodecName(candidate.name) }.getOrNull() ?: continue
            try {
                if (Build.VERSION.SDK_INT >= 30)
                    format.setInteger(MediaFormat.KEY_LOW_LATENCY,
                        if (caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)) 1 else 0)
                codec.configure(format, surface, null, 0)
                codec.setOnFrameRenderedListener({ _, _, _ -> renderedFrames.incrementAndGet() },
                    Handler(Looper.getMainLooper()))
                codec.start()
                return codec
            } catch (_: Exception) {
                runCatching { codec.release() }
            }
        }
        error("O aparelho não suporta ${video.width} × ${video.height} a ${video.fps} fps. Reduza a qualidade no Windows.")
    }

    private fun parameterSet(data: ByteArray, type: Int): ByteArray? {
        var start = -1
        for (i in 0 until data.size - 3) {
            if (data[i] != 0.toByte() || data[i + 1] != 0.toByte() || data[i + 2] != 1.toByte()) continue
            val boundary = if (i > 0 && data[i - 1] == 0.toByte()) i - 1 else i
            if (start >= 0) return data.copyOfRange(start, boundary)
            if ((data[i + 3].toInt() and 31) == type) start = boundary
        }
        return if (start >= 0) data.copyOfRange(start, data.size) else null
    }

    private fun playAudio(stream: StreamInfo) {
        var track: AudioTrack? = null
        try {
            val channelMask = if (stream.channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
            val encoding = AudioFormat.ENCODING_PCM_16BIT
            val minSize = AudioTrack.getMinBufferSize(stream.sampleRate, channelMask, encoding)
            require(minSize > 0) { "Formato de áudio incompatível." }
            val bytesPerSecond = stream.sampleRate * stream.channels * 2L
            val bufferSize = maxOf(minSize, (bytesPerSecond / 15).toInt())
            val output = AudioTrack.Builder()
                .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).build())
                .setAudioFormat(AudioFormat.Builder().setEncoding(encoding).setChannelMask(channelMask)
                    .setSampleRate(stream.sampleRate).build())
                .setTransferMode(AudioTrack.MODE_STREAM).setBufferSizeInBytes(bufferSize).build()
            track = output
            check(output.state == AudioTrack.STATE_INITIALIZED) { "Saída de áudio indisponível." }
            var expectedPts: Long? = null
            var lastVolume = -1f
            while (!closed.get()) {
                val currentVolume = volume
                if (lastVolume != currentVolume) {
                    output.setVolume(currentVolume)
                    lastVolume = currentVolume
                }
                val packet = if (clock.deadline(0) != null) audioQueue.poll() else null
                if (packet == null) {
                    Thread.sleep(5)
                    continue
                }
                val now = System.nanoTime()
                val due = clock.deadline(packet.ptsUs) ?: continue
                if (now - due > 150_000_000 || due - now > 450_000_000) continue
                if (expectedPts?.let { abs(it - packet.ptsUs) > 65_000 } != false) {
                    output.pause()
                    output.flush()
                    if (due > now) Thread.sleep((due - now) / 1_000_000)
                    output.play()
                } else if (due - now > 45_000_000) {
                    Thread.sleep((due - now - 45_000_000) / 1_000_000)
                }
                var offset = 0
                while (offset < packet.data.size && !closed.get()) {
                    val count = output.write(packet.data, offset, packet.data.size - offset, AudioTrack.WRITE_NON_BLOCKING)
                    check(count >= 0) { "Falha na saída de áudio." }
                    if (count == 0) Thread.sleep(3) else offset += count
                }
                expectedPts = packet.ptsUs + packet.data.size * 1_000_000L / bytesPerSecond
            }
        } catch (_: InterruptedException) {
        } catch (error: Exception) {
            if (!closed.get()) onError(error.message ?: "Falha no áudio.")
        } finally {
            runCatching { track?.release() }
        }
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        videoThread.interrupt()
        audioThread?.interrupt()
        videoQueue.clear()
        audioQueue.clear()
    }

    private class PacketQueue(private val maxItems: Int, private val maxBytes: Int) {
        private val queue = ArrayDeque<Packet>()
        private var bytes = 0

        @Synchronized fun offer(packet: Packet): Boolean {
            if (queue.size >= maxItems || bytes + packet.data.size > maxBytes) return false
            queue.addLast(packet)
            bytes += packet.data.size
            return true
        }

        @Synchronized fun poll(): Packet? = queue.pollFirst()?.also { bytes -= it.data.size }
        @Synchronized fun clear() { queue.clear(); bytes = 0 }
    }

    private companion object {
        const val DISPLAY_LEAD_NS = 12_000_000L
        const val DECODE_LEAD_NS = 120_000_000L
    }
}
