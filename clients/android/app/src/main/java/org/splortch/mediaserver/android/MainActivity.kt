package org.splortch.mediaserver.android

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.media3.common.util.UnstableApi
import androidx.media3.ui.PlayerView
import org.splortch.mediaserver.core.MediaSummary

@UnstableApi
class MainActivity : ComponentActivity() {
    val model: MediaViewModel by viewModels()
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            val screen by model.screen.collectAsStateWithLifecycle()
            val player by model.player.collectAsStateWithLifecycle()
            LaunchedEffect(screen.fullscreen, player) {
                val controller = WindowCompat.getInsetsController(window, window.decorView)
                controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                if (screen.fullscreen) controller.hide(WindowInsetsCompat.Type.systemBars()) else controller.show(WindowInsetsCompat.Type.systemBars())
                if (player != null) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            }
            BackHandler(screen.detail != null) { model.back() }
            MaterialTheme(colorScheme = darkColorScheme(primary = Color(0xFF9DB5FF), background = Color(0xFF10131B), surface = Color(0xFF1C2130))) {
                Surface(Modifier.fillMaxSize()) {
                    if (screen.fullscreen && player != null) {
                        Box(Modifier.fillMaxSize()) {
                            VideoView(requireNotNull(player), Modifier.fillMaxSize())
                            TextButton(onClick = model::fullscreen, modifier = Modifier.statusBarsPadding()) { Text("Exit fullscreen") }
                        }
                    } else {
                        LazyColumn(Modifier.fillMaxSize().safeDrawingPadding().padding(horizontal = 20.dp), verticalArrangement = Arrangement.spacedBy(14.dp), contentPadding = PaddingValues(vertical = 20.dp)) {
                            item {
                                Text("MEDIA SERVER", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                                Text(if (screen.phase == "connect") "Your library. Anywhere." else if (screen.detail != null) "Now exploring" else "Welcome${if (screen.user.isNotEmpty()) ", ${screen.user}" else ""}", style = MaterialTheme.typography.headlineMedium)
                            }
                            if (screen.busy) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
                            screen.error?.let { item { Text(it, color = MaterialTheme.colorScheme.error) } }
                            if (screen.phase == "connect") item { ConnectionScreen(screen.address, screen.busy, model::connect) }
                            if (screen.phase == "login") item { LoginScreen(screen.address, screen.busy, model::login) }
                            if (screen.phase != "connect") item {
                                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                                    Text("Server ${screen.version} · API 1", Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                                    TextButton(onClick = model::logout, enabled = !screen.busy) { Text("Sign out") }
                                }
                            }
                            val detail = screen.detail
                            if (screen.phase == "library" && detail == null) {
                                item { SearchScreen(screen, model) }
                                if (screen.continuing.isNotEmpty()) {
                                    item { Text("Continue watching", style = MaterialTheme.typography.titleLarge) }
                                    items(screen.continuing, key = { "continue-${it.id}" }) { media -> MediaCard(media, !screen.busy) { model.details(media.id) } }
                                }
                                item {
                                    Row(Modifier.fillMaxWidth()) {
                                        Text("Your library", Modifier.weight(1f), style = MaterialTheme.typography.titleLarge)
                                        TextButton(onClick = { model.refresh() }, enabled = !screen.busy) { Text("Refresh") }
                                    }
                                    if (screen.items.isEmpty() && !screen.busy) Text("No matching media. Add a library and scan it in the server web interface.")
                                }
                                items(screen.items, key = { "browse-${it.id}" }) { media -> MediaCard(media, !screen.busy) { model.details(media.id) } }
                                if (screen.cursor != null && screen.items.size < 2000) item { OutlinedButton(onClick = { model.refresh(true) }, enabled = !screen.busy) { Text("Load more") } }
                                item { Text("Direct Play alpha · Media stays on your server. Remote connections require HTTPS.", style = MaterialTheme.typography.bodySmall) }
                            }
                            if (detail != null) {
                                item { TextButton(onClick = model::back, enabled = !screen.busy) { Text(if (player != null) "Close player" else "Back to library") } }
                                item { Text(detail.title, style = MaterialTheme.typography.headlineMedium); Text("${detail.container} · ${detail.videoCodec} / ${detail.audioCodec} · ${(detail.duration / 60).toInt()} min"); Text("Title and technical information from your media file.", style = MaterialTheme.typography.bodySmall) }
                                item {
                                    val current = player
                                    if (current == null) Button(onClick = model::play, enabled = !screen.busy) { Text("Play / resume") }
                                    else {
                                        VideoView(current, Modifier.fillMaxWidth().aspectRatio(16f / 9f))
                                        TextButton(onClick = model::fullscreen) { Text("Fullscreen") }
                                        var volume by remember(current) { mutableFloatStateOf(current.volume) }
                                        Text("Volume")
                                        Slider(value = volume, onValueChange = { volume = it; current.volume = it })
                                    }
                                }
                                screen.syncError?.let { item { Text(it, color = MaterialTheme.colorScheme.error) } }
                                detail.playback.warning?.let { item { Text(it.take(512)) } }
                                item {
                                    Text("Tracks", style = MaterialTheme.typography.titleLarge)
                                    Text("The server currently exposes track information only. Track switching awaits server support.", style = MaterialTheme.typography.bodySmall)
                                }
                                items(screen.tracks?.tracks ?: emptyList()) { track -> Text("${track.type.take(32)} · ${track.codec} · ${track.language ?: "und"} ${track.title ?: ""}") }
                            }
                        }
                    }
                }
            }
        }
    }
    override fun onStop() { if (!isChangingConfigurations) model.background(); super.onStop() }
}

@UnstableApi
@Composable private fun VideoView(player: androidx.media3.exoplayer.ExoPlayer, modifier: Modifier) {
    AndroidView(factory = { context -> PlayerView(context).apply { this.player = player; useController = true } }, modifier = modifier,
        onReset = null, onRelease = { it.player = null }, update = { it.player = player })
}
@Composable private fun ConnectionScreen(saved: String, busy: Boolean, connect: (String, Boolean) -> Unit) {
    var address by remember(saved) { mutableStateOf(saved) }
    var rememberSession by remember { mutableStateOf(true) }
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Connect to your own Media Server. Complete first-time server setup in a browser first.")
        OutlinedTextField(value = address, onValueChange = { address = it.take(512) }, label = { Text("Server address") }, singleLine = true, modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
        Row { Checkbox(rememberSession, { rememberSession = it }); Text("Remember this session on this device", Modifier.padding(top = 12.dp)) }
        Button(onClick = { connect(address, rememberSession) }, enabled = !busy) { Text("Connect") }
        Text("Use https://your-server.example. For private USB testing: adb reverse tcp:5050 tcp:5050, then http://127.0.0.1:5050. LAN HTTP is blocked.", style = MaterialTheme.typography.bodySmall)
    }
}
@Composable private fun LoginScreen(server: String, busy: Boolean, login: (String, String) -> Unit) {
    var name by remember { mutableStateOf("") }
    // Deliberately not rememberSaveable: passwords never survive Activity recreation or enter saved state.
    var password by remember { mutableStateOf("") }
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Sign in to $server")
        OutlinedTextField(name, { name = it.take(64) }, label = { Text("Username") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(password, { password = it.take(256) }, label = { Text("Password") }, singleLine = true, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.fillMaxWidth())
        Button(onClick = { val supplied = password; password = ""; login(name, supplied) }, enabled = !busy) { Text("Sign in") }
    }
}
@UnstableApi
@Composable private fun SearchScreen(screen: Screen, model: MediaViewModel) {
    var prefix by remember(screen.prefix) { mutableStateOf(screen.prefix) }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(prefix, { prefix = it.take(128) }, label = { Text("Title starts with") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Button(onClick = { model.search(prefix) }, enabled = !screen.busy) { Text("Search") }
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(screen.libraryId == null, { model.search(prefix, null) }, label = { Text("All libraries") }, enabled = !screen.busy)
            screen.libraries.forEach { library -> FilterChip(screen.libraryId == library.id, { model.search(prefix, library.id) }, label = { Text(library.name) }, enabled = !screen.busy) }
        }
    }
}
@Composable private fun MediaCard(media: MediaSummary, enabled: Boolean, open: () -> Unit) {
    Card(onClick = open, enabled = enabled, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(media.title, style = MaterialTheme.typography.titleMedium)
            Text("${(media.duration / 60).toInt()} min · ${media.container.take(32)} · ${media.videoCodec.take(64)}", style = MaterialTheme.typography.bodySmall)
        }
    }
}
