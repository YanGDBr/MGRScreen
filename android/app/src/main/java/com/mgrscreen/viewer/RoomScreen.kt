package com.mgrscreen.viewer

import android.content.pm.ActivityInfo
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.movableContentOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import kotlin.math.roundToInt

@Composable
internal fun RoomScreen(
    state: ViewerState,
    model: ViewerModel,
    fullScreen: Boolean,
    setFullScreen: (Boolean) -> Unit,
    rotate: (Int) -> Unit,
) {
    // The SurfaceView follows the video when the surrounding layout changes.
    val videoSurface = remember(model) {
        movableContentOf<Modifier> { modifier ->
            AndroidView(
                modifier = modifier,
                factory = { context ->
                    SurfaceView(context).apply {
                        holder.addCallback(object : SurfaceHolder.Callback {
                            override fun surfaceCreated(holder: SurfaceHolder) = model.setSurface(holder.surface)
                            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                            override fun surfaceDestroyed(holder: SurfaceHolder) = model.clearSurface(holder.surface)
                        })
                    }
                },
            )
        }
    }
    val current = state.selected
    if (fullScreen && current != null) {
        Player(state, current, model, true, setFullScreen, rotate, videoSurface, Modifier.fillMaxSize())
        return
    }
    BoxWithConstraints(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing).padding(14.dp)) {
        val wide = maxWidth >= 500.dp && maxWidth > maxHeight
        val sideWidth = (maxWidth * .34f).coerceAtMost(320.dp)
        if (wide && current != null) {
            Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Player(state, current, model, false, setFullScreen, rotate, videoSurface, Modifier.weight(1f))
                Column(Modifier.width(sideWidth),
                    verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    RoomHeader(state, model::leave)
                    StreamList(state, model::select, Modifier.weight(1f))
                }
            }
        } else {
            Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                RoomHeader(state, model::leave)
                if (current != null) {
                    Player(state, current, model, false, setFullScreen, rotate, videoSurface, Modifier.weight(2f))
                }
                StreamList(state, model::select, Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun RoomHeader(state: ViewerState, leave: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            BrandLogo(Modifier.size(36.dp))
            Column(Modifier.weight(1f)) {
                Text("Sala MGRScreen", style = MaterialTheme.typography.titleLarge)
                state.endpoint?.host?.let {
                    Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            TextButton(onClick = leave) { Text("Sair") }
        }
        if (state.status.isNotEmpty()) {
            Text(state.status, color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
private fun StreamList(state: ViewerState, select: (StreamInfo) -> Unit, modifier: Modifier) {
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text("Transmissões", style = MaterialTheme.typography.titleMedium)
        if (state.streams.isEmpty() && state.status.isEmpty())
            Text("Ninguém está compartilhando agora.", color = MaterialTheme.colorScheme.onSurfaceVariant)
        LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(state.streams, key = { "${it.key.publisher}:${it.key.kind}" }) { stream ->
                val kind = if (stream.key.kind == 1) "Câmera" else "Tela"
                val active = stream.key == state.selected?.key
                Card(onClick = { select(stream) }, enabled = stream.isPlayable && !active,
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surface,
                        disabledContainerColor = if (active) MaterialTheme.colorScheme.primaryContainer
                            else MaterialTheme.colorScheme.surface,
                        disabledContentColor = if (active) MaterialTheme.colorScheme.onPrimaryContainer
                            else MaterialTheme.colorScheme.onSurfaceVariant,
                    )) {
                    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp)) {
                        Text(stream.name, style = MaterialTheme.typography.titleSmall, maxLines = 1,
                            overflow = TextOverflow.Ellipsis)
                        val detail = when {
                            active -> "$kind · Assistindo"
                            stream.isPlayable -> "$kind · ${stream.height}p · ${stream.fps} fps"
                            else -> "$kind · Atualize o transmissor"
                        }
                        Text(detail, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun Player(
    state: ViewerState,
    stream: StreamInfo,
    model: ViewerModel,
    fullScreen: Boolean,
    setFullScreen: (Boolean) -> Unit,
    rotate: (Int) -> Unit,
    videoSurface: @Composable (Modifier) -> Unit,
    modifier: Modifier,
) {
    var showControls by rememberSaveable { mutableStateOf(true) }
    val audio = stream.key.kind == 0 && stream.pcmSupported
    val ratio = (stream.width.toFloat() / stream.height.coerceAtLeast(1)).coerceIn(.2f, 8f)
    if (fullScreen) {
        Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
            videoSurface(Modifier.aspectRatio(ratio))
            Box(Modifier.matchParentSize().clickable(
                onClickLabel = if (showControls) "Ocultar controles" else "Mostrar controles",
            ) { showControls = !showControls })
            PlaybackMessage(state, stream, model)
            if (showControls) {
                Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.displayCutout).padding(10.dp),
                    verticalArrangement = Arrangement.SpaceBetween) {
                    Surface(color = MaterialTheme.colorScheme.surface.copy(alpha = .94f),
                        shape = MaterialTheme.shapes.medium) {
                        FlowRow(Modifier.padding(horizontal = 4.dp)) {
                            TextButton(onClick = { setFullScreen(false) }) { Text("Sair da tela cheia") }
                            OrientationMenu(rotate)
                        }
                    }
                    if (audio) {
                        Surface(color = MaterialTheme.colorScheme.surface.copy(alpha = .94f),
                            shape = MaterialTheme.shapes.medium) {
                            AudioControls(state, model, Modifier.padding(horizontal = 8.dp))
                        }
                    }
                }
            }
        }
    } else {
        Column(modifier.fillMaxWidth()) {
            Box(Modifier.fillMaxWidth().weight(1f).clip(MaterialTheme.shapes.medium).background(Color.Black),
                contentAlignment = Alignment.Center) {
                videoSurface(Modifier.aspectRatio(ratio))
                PlaybackMessage(state, stream, model)
            }
            FlowRow(verticalArrangement = Arrangement.Center) {
                TextButton(onClick = { showControls = true; setFullScreen(true) }) { Text("Tela cheia") }
                OrientationMenu(rotate)
            }
            if (audio) AudioControls(state, model)
            if (state.stats.isNotEmpty()) {
                Text(state.stats, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    style = MaterialTheme.typography.labelSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun PlaybackMessage(state: ViewerState, stream: StreamInfo, model: ViewerModel) {
    if (state.receiving && state.playbackError == null) return
    Surface(color = MaterialTheme.colorScheme.surface.copy(alpha = .92f), shape = MaterialTheme.shapes.medium) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
            horizontalAlignment = Alignment.CenterHorizontally) {
            Text(state.playbackError ?: "Aguardando imagem…")
            if (state.playbackError != null) {
                TextButton(onClick = { model.select(stream) }) { Text("Tentar novamente") }
            }
        }
    }
}

@Composable
private fun OrientationMenu(rotate: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        TextButton(onClick = { open = true }) { Text("Orientação") }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            listOf(
                "Automática" to ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED,
                "Retrato" to ActivityInfo.SCREEN_ORIENTATION_PORTRAIT,
                "Paisagem" to ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE,
            ).forEach { (label, value) ->
                DropdownMenuItem(text = { Text(label) }, onClick = { open = false; rotate(value) })
            }
        }
    }
}

@Composable
private fun AudioControls(state: ViewerState, model: ViewerModel, modifier: Modifier = Modifier) {
    val silent = state.muted || state.volume == 0f
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        TextButton(onClick = model::toggleMute) { Text(if (silent) "Ativar som" else "Silenciar") }
        Slider(state.volume, model::volume, modifier = Modifier.weight(1f)
            .semantics { contentDescription = "Volume da transmissão" })
        Text(if (silent) "Mudo" else "${(state.volume * 100).roundToInt()}%",
            style = MaterialTheme.typography.labelSmall)
    }
}
