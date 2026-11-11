package com.mgrscreen.viewer

import android.app.Application
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.os.Handler
import android.os.Looper
import android.view.Surface
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import android.os.SystemClock

private val Context.settings by preferencesDataStore("viewer")
private val historyKey = stringPreferencesKey("servers")
private val nicknameKey = stringPreferencesKey("nickname")
internal data class ViewerState(
    val endpoint: Endpoint? = null,
    val nickname: String = "",
    val history: List<Endpoint> = emptyList(),
    val rooms: List<RoomAddress> = emptyList(),
    val scanning: Boolean = false,
    val streams: List<StreamInfo> = emptyList(),
    val selected: StreamInfo? = null,
    val status: String = "",
    val playbackError: String? = null,
    val receiving: Boolean = false,
    val stats: String = "",
    val volume: Float = 1f,
    val muted: Boolean = false,
)

internal class ViewerModel(application: Application) : AndroidViewModel(application) {
    private val mutable = MutableStateFlow(ViewerState())
    val state = mutable.asStateFlow()
    private val main = Handler(Looper.getMainLooper())
    private val settings = application.settings
    private val audioManager = application.getSystemService(AudioManager::class.java)
    private var audioFocus = false
    private var focusScale = 0f
    private val focusRequest = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
        .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).build())
        .setOnAudioFocusChangeListener({ change ->
            focusScale = when (change) {
                AudioManager.AUDIOFOCUS_GAIN -> 1f
                AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK -> .25f
                else -> 0f
            }
            applyVolume()
        }, main).build()
    private var endpoint: Endpoint? = null
    private var roomJob: Job? = null
    @Volatile private var connection: RoomConnection? = null
    private var surface: Surface? = null
    private var requested: StreamInfo? = null
    private var currentSelection: Selection? = null
    private var currentAudio: StreamInfo? = null
    @Volatile private var player: Playback? = null
    private var playEpoch = 0
    private var nicknameEdited = false
    private var inForeground = false
    private var discoveryJob: Job? = null
    private var announcementJob: Job? = null
    private var scannedRooms = emptyList<RoomAddress>()
    private val announcedRooms = linkedMapOf<Pair<String, Int>, Pair<RoomAddress, Long>>()

    init {
        viewModelScope.launch {
            val preferences = runCatching { settings.data.first() }.getOrNull()
            val saved = preferences?.get(historyKey)
            val entries = runCatching {
                val array = JSONArray(saved ?: "[]")
                List(array.length()) { i ->
                    val item = array.getJSONObject(i)
                    Endpoint(item.getString("host"), item.getString("nick"),
                        item.getInt("control"), item.getInt("media"))
                }
            }.getOrDefault(emptyList())
            mutable.update {
                it.copy(
                    history = it.history.ifEmpty { entries },
                    nickname = if (nicknameEdited) it.nickname else preferences?.get(nicknameKey).orEmpty(),
                )
            }
            if (inForeground && endpoint == null && entries.isNotEmpty()) restartDiscovery()
        }
        viewModelScope.launch {
            while (true) {
                delay(1000)
                val active = player ?: continue
                val fps = active.takeFrames()
                val bitrate = active.takeBytes() * 8 / 1000
                mutable.update {
                    it.copy(receiving = it.receiving || fps > 0, stats = "$fps fps · $bitrate kb/s de vídeo")
                }
            }
        }
    }

    fun nickname(value: String) {
        nicknameEdited = true
        mutable.update { it.copy(nickname = value) }
    }

    fun connect(host: String, control: Int, media: Int) {
        val nickname = mutable.value.nickname.trim()
        val address = host.trim().removeSurrounding("[", "]")
        if (nickname.isBlank() || address.isBlank() || control !in 1..65535 || media !in 1..65535) return
        val target = Endpoint(address, nickname, control, media)
        stopDiscovery()
        suspendRoom()
        requested = null
        endpoint = target
        val history = (listOf(target) + mutable.value.history.filter { it.host != target.host ||
            it.control != target.control || it.media != target.media }).take(6)
        mutable.update { it.copy(endpoint = target, history = history, status = "Conectando…") }
        viewModelScope.launch {
            val json = JSONArray()
            history.forEach {
                json.put(JSONObject().put("host", it.host).put("nick", it.nickname)
                    .put("control", it.control).put("media", it.media))
            }
            runCatching {
                settings.edit {
                    it[historyKey] = json.toString()
                    it[nicknameKey] = nickname
                }
            }
        }
        resume()
    }

    fun resume() {
        val target = endpoint ?: return
        if (roomJob?.isActive == true) return
        roomJob = viewModelScope.launch {
            var delayMs = 500L
            while (true) {
                val session = RoomConnection()
                connection = session
                mutable.update { it.copy(status = if (delayMs == 500L) "Conectando…" else "Reconectando…") }
                try {
                    withContext(Dispatchers.IO) {
                        session.run(target, { streams ->
                            main.post {
                                if (session === connection) {
                                    mutable.update { it.copy(streams = streams, status = "") }
                                    updatePlayback()
                                }
                            }
                        }, { packet ->
                            if (session === connection) player?.offer(packet)
                        }, {
                            main.post {
                                if (session === connection)
                                    mutable.update { it.copy(status = "A conexão está com dificuldade para acompanhar o vídeo.") }
                            }
                        })
                    }
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Rejected) {
                    mutable.update { it.copy(status = error.message.orEmpty()) }
                    return@launch
                } catch (error: Exception) {
                    if (mutable.value.streams.isNotEmpty()) delayMs = 500
                    mutable.update {
                        it.copy(status = "Conexão interrompida. Confira se a sala está aberta e o endereço é acessível.",
                            streams = emptyList(), selected = null)
                    }
                } finally {
                    session.close()
                    if (connection === session) {
                        connection = null
                        resetPlayer()
                    }
                }
                delay(delayMs)
                delayMs = (delayMs * 2).coerceAtMost(5000)
            }
        }
    }

    fun enterForeground() {
        inForeground = true
        if (endpoint == null) startDiscovery() else resume()
    }

    fun leaveForeground() {
        inForeground = false
        stopDiscovery()
        suspendRoom()
    }

    fun suspendRoom() {
        val previous = connection
        connection = null
        previous?.close()
        roomJob?.cancel()
        roomJob = null
        resetPlayer()
        mutable.update { it.copy(streams = emptyList(), selected = null) }
    }

    fun leave() {
        suspendRoom()
        endpoint = null
        requested = null
        mutable.update { it.copy(endpoint = null, streams = emptyList(), selected = null, status = "") }
        if (inForeground) startDiscovery()
    }

    fun select(stream: StreamInfo?) {
        requested = stream
        resetPlayer()
        updatePlayback()
    }

    fun setSurface(value: Surface?) {
        if (value === surface) return
        surface = value
        updatePlayback()
    }

    fun clearSurface(value: Surface) {
        if (surface === value) setSurface(null)
    }

    fun volume(value: Float) {
        val volume = value.coerceIn(0f, 1f)
        mutable.update { it.copy(volume = volume, muted = false) }
        applyVolume()
    }

    fun toggleMute() {
        mutable.update {
            if (it.volume == 0f) it.copy(volume = .65f, muted = false)
            else it.copy(muted = !it.muted)
        }
        applyVolume()
    }

    private fun outputVolume(): Float {
        val state = mutable.value
        return if (state.muted) 0f else state.volume * focusScale
    }

    private fun applyVolume() { player?.setVolume(outputVolume()) }

    private fun updatePlayback() {
        val streams = mutable.value.streams
        val stream = requested?.let { previous ->
            streams.firstOrNull { it.key == previous.key && it.name == previous.name }
                ?: streams.filter { it.name == previous.name && it.key.kind == previous.key.kind }.singleOrNull()
        }
        mutable.update { it.copy(selected = stream) }
        val audio = streams.firstOrNull { it.key == StreamKey(stream?.key?.publisher ?: -1, 0) && it.pcmSupported }
            ?.takeIf { stream?.key?.kind == 0 }
        val targetSurface = surface?.takeIf { it.isValid }
        val selection = Selection(stream?.takeIf { it.isPlayable && targetSurface != null }, audio != null)
        if (selection == currentSelection && currentAudio == audio) return
        resetPlayer()
        currentSelection = selection
        currentAudio = audio
        val session = connection ?: return
        if (selection.stream != null && targetSurface != null) {
            if (audio != null) {
                audioFocus = audioManager.requestAudioFocus(focusRequest) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
                focusScale = if (audioFocus) 1f else 0f
            }
            val epoch = playEpoch
            player = Playback(selection.stream, audio, targetSurface) { message ->
                main.post {
                    if (epoch == playEpoch) mutable.update { it.copy(playbackError = message) }
                }
            }.also {
                it.setVolume(outputVolume())
                it.start()
            }
        }
        session.select(selection)
    }

    private fun resetPlayer() {
        playEpoch++
        player?.close()
        player = null
        currentSelection = null
        currentAudio = null
        if (audioFocus) audioManager.abandonAudioFocusRequest(focusRequest)
        audioFocus = false
        focusScale = 0f
        mutable.update { it.copy(receiving = false, stats = "", playbackError = null) }
    }

    private fun startDiscovery() {
        if (announcementJob?.isActive != true) {
            announcementJob = viewModelScope.launch(Dispatchers.IO) {
                while (true) {
                    RoomDiscovery.listenForTailscale { room ->
                        withContext(Dispatchers.Main) {
                            announcedRooms[room.key] = room to SystemClock.elapsedRealtime()
                            if (announcedRooms.size > 50) announcedRooms.remove(announcedRooms.keys.first())
                            publishRooms()
                        }
                    }
                    delay(2000)
                }
            }
        }
        if (discoveryJob?.isActive != true) restartDiscovery()
    }

    fun refreshRooms() {
        if (inForeground && endpoint == null && !mutable.value.scanning) restartDiscovery()
    }

    private fun restartDiscovery() {
        discoveryJob?.cancel()
        discoveryJob = viewModelScope.launch {
            while (true) {
                mutable.update { it.copy(scanning = true) }
                scannedRooms = RoomDiscovery.scan(mutable.value.history)
                publishRooms()
                mutable.update { it.copy(scanning = false) }
                delay(8_000)
            }
        }
    }

    private fun publishRooms() {
        val now = SystemClock.elapsedRealtime()
        announcedRooms.entries.removeAll { now - it.value.second > 18_000 }
        val rooms = (announcedRooms.values.map { it.first } + scannedRooms)
            .distinctBy { it.key }.sortedBy { it.name.lowercase() }
        mutable.update { it.copy(rooms = rooms) }
    }

    private fun stopDiscovery() {
        discoveryJob?.cancel()
        discoveryJob = null
        announcementJob?.cancel()
        announcementJob = null
        scannedRooms = emptyList()
        announcedRooms.clear()
        mutable.update { it.copy(scanning = false, rooms = emptyList()) }
    }

    override fun onCleared() { leaveForeground() }
}
