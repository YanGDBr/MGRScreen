package com.mgrscreen.viewer

import android.content.pm.ActivityInfo
import android.media.AudioManager
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

class MainActivity : ComponentActivity() {
    private val model: ViewerModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        volumeControlStream = AudioManager.STREAM_MUSIC
        WindowCompat.setDecorFitsSystemWindows(window, false)
        val insets = WindowCompat.getInsetsController(window, window.decorView)
        insets.isAppearanceLightStatusBars = false
        insets.isAppearanceLightNavigationBars = false
        setContent {
            val state by model.state.collectAsState()
            var fullScreen by rememberSaveable { mutableStateOf(false) }
            val immersive = fullScreen && state.selected != null
            LaunchedEffect(immersive) {
                insets.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                if (immersive) insets.hide(WindowInsetsCompat.Type.systemBars())
                else insets.show(WindowInsetsCompat.Type.systemBars())
            }
            DisposableEffect(state.receiving) {
                if (state.receiving) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                onDispose { window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
            }
            LaunchedEffect(state.endpoint) {
                if (state.endpoint == null) {
                    fullScreen = false
                    if (requestedOrientation != ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED)
                        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                }
            }
            BackHandler(enabled = state.endpoint != null) {
                when {
                    immersive -> fullScreen = false
                    state.selected != null -> model.select(null)
                    else -> model.leave()
                }
            }
            MaterialTheme(colorScheme = ViewerColors) {
                Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    if (state.endpoint == null) {
                        LobbyScreen(state, model)
                    } else {
                        RoomScreen(state, model, immersive, { fullScreen = it }) { orientation ->
                            requestedOrientation = orientation
                        }
                    }
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        model.enterForeground()
    }

    override fun onStop() {
        model.leaveForeground()
        super.onStop()
    }
}

private val ViewerColors = darkColorScheme(
    primary = Color(0xFF9BD8FF),
    onPrimary = Color(0xFF062A43),
    primaryContainer = Color(0xFF173A56),
    onPrimaryContainer = Color(0xFFDAEEFF),
    secondaryContainer = Color(0xFF223B4C),
    onSecondaryContainer = Color(0xFFD6E9F7),
    background = Color(0xFF0A121D),
    onBackground = Color(0xFFE9F2FC),
    surface = Color(0xFF101D2C),
    onSurface = Color(0xFFE9F2FC),
    surfaceVariant = Color(0xFF273746),
    onSurfaceVariant = Color(0xFFB4C8DA),
    outline = Color(0xFF768D9F),
)
