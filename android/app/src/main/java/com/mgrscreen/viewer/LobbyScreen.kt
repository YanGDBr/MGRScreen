package com.mgrscreen.viewer

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp

@Composable
internal fun LobbyScreen(state: ViewerState, model: ViewerModel) {
    var manual by rememberSaveable { mutableStateOf(false) }
    val canEnter = state.nickname.isNotBlank()
    Box(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing).imePadding(),
        contentAlignment = Alignment.TopCenter) {
        Column(
            Modifier.widthIn(max = 640.dp).fillMaxWidth().verticalScroll(rememberScrollState()).padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Surface(color = MaterialTheme.colorScheme.primaryContainer, shape = MaterialTheme.shapes.large) {
                Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    BrandLogo(Modifier.size(54.dp))
                    Column {
                        Text("MGRScreen Viewer", style = MaterialTheme.typography.headlineSmall)
                        Text("Assista à tela dos seus amigos", style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
            OutlinedTextField(state.nickname, model::nickname, Modifier.fillMaxWidth(), singleLine = true,
                label = { Text("Apelido") },
                supportingText = { if (!canEnter) Text("Informe um apelido para entrar em uma sala.") },
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words))

            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically) {
                Text("Salas disponíveis", Modifier.weight(1f), style = MaterialTheme.typography.titleLarge)
                TextButton(onClick = model::refreshRooms, enabled = !state.scanning) {
                    Text("Atualizar", maxLines = 1)
                }
            }
            if (state.rooms.isEmpty()) {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        if (state.scanning) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(if (state.scanning) "Buscando salas…" else "Nenhuma sala encontrada",
                                style = MaterialTheme.typography.titleMedium)
                            Text("A busca se repete automaticamente pela rede local e pelo Tailscale.",
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
            state.rooms.forEach { room ->
                RoomCard(room, canEnter) { model.connect(room.host, room.control, room.media) }
            }

            FilledTonalButton(onClick = { manual = !manual }) {
                Text(if (manual) "Ocultar entrada manual" else "Entrar por IP ou nome")
            }
            if (manual) ManualEntry(canEnter, model::connect)

            if (state.history.isNotEmpty()) {
                Text("Conexões recentes", style = MaterialTheme.typography.titleMedium)
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    state.history.forEach { entry ->
                        TextButton(enabled = canEnter, onClick = { model.connect(entry.host, entry.control, entry.media) }) {
                            Text(if (entry.control == 51900) entry.host else "${entry.host}:${entry.control}")
                        }
                    }
                }
            }
            Text("Fora de casa, conecte o Tailscale no celular e no computador do anfitrião.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
internal fun BrandLogo(modifier: Modifier) {
    Image(painterResource(R.drawable.mgrscreen_logo), contentDescription = null, modifier = modifier)
}

@Composable
private fun RoomCard(room: RoomAddress, canEnter: Boolean, enter: () -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Row(Modifier.fillMaxWidth().padding(14.dp), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(room.name, style = MaterialTheme.typography.titleMedium)
                Text("${room.route} · ${room.host}", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                val count = room.participants
                Text(if (count == 1) "1 participante" else "$count participantes",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Button(onClick = enter, enabled = canEnter) { Text("Entrar") }
        }
    }
}

@Composable
private fun ManualEntry(canEnter: Boolean, connect: (String, Int, Int) -> Unit) {
    var host by rememberSaveable { mutableStateOf("") }
    var control by rememberSaveable { mutableStateOf("51900") }
    var media by rememberSaveable { mutableStateOf("51901") }
    var advanced by rememberSaveable { mutableStateOf(false) }
    val controlPort = control.toIntOrNull()?.takeIf { it in 1..65535 }
    val mediaPort = media.toIntOrNull()?.takeIf { it in 1..65535 }
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(host, { host = it }, Modifier.fillMaxWidth(), singleLine = true,
                label = { Text("IP ou nome do computador") },
                placeholder = { Text("IP do Tailscale ou nome MagicDNS") },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
            TextButton(onClick = { advanced = !advanced }) { Text(if (advanced) "Ocultar portas" else "Configurar portas") }
            if (advanced) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(control, { control = it }, Modifier.weight(1f), singleLine = true,
                        label = { Text("Controle") }, isError = controlPort == null,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
                    OutlinedTextField(media, { media = it }, Modifier.weight(1f), singleLine = true,
                        label = { Text("Mídia") }, isError = mediaPort == null,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
                }
            }
            Button(enabled = canEnter && host.isNotBlank() && controlPort != null && mediaPort != null, onClick = {
                if (controlPort != null && mediaPort != null) connect(host, controlPort, mediaPort)
            }) { Text("Conectar à sala") }
        }
    }
}
