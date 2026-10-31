package org.splortch.mediaserver.android

import android.app.Application
import android.media.MediaCodecList
import android.net.Uri
import androidx.core.content.edit
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.MediaSession
import java.io.IOException
import javax.net.ssl.SSLException
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.splortch.mediaserver.core.*
import retrofit2.HttpException

internal data class Screen(
    val phase: String = "connect",
    val address: String = "",
    val remember: Boolean = true,
    val busy: Boolean = false,
    val error: String? = null,
    val version: String = "",
    val user: String = "",
    val items: List<MediaSummary> = emptyList(),
    val continuing: List<MediaSummary> = emptyList(),
    val libraries: List<LibraryBrowse> = emptyList(),
    val prefix: String = "",
    val libraryId: Int? = null,
    val cursor: String? = null,
    val detail: MediaDetails? = null,
    val tracks: TrackCatalog? = null,
    val playbackOptions: PlaybackOptions? = null,
    val audioTrack: Int? = null,
    val subtitleTrack: Int? = null,
    val quality: String = "Auto",
    val conversionStatus: String? = null,
    val syncError: String? = null,
    val videoReady: Boolean = false,
    val fullscreen: Boolean = false,
)

@UnstableApi
class MediaViewModel(app: Application) : AndroidViewModel(app) {
    private val prefs = app.getSharedPreferences("connection", 0)
    private val vault = KeystoreVault(app)
    private val savedConnections =
        MutableStateFlow(
            SavedServers.read(prefs.getString("servers", null), BuildConfig.DEBUG).ifEmpty {
                try {
                    SavedServers.remember(
                        emptyList(),
                        prefs.getString("origin", "") ?: "",
                        "",
                        BuildConfig.DEBUG,
                    )
                } catch (_: IllegalArgumentException) {
                    emptyList()
                }
            }
        )
    internal val savedServers = savedConnections.asStateFlow()

    internal fun forgetServer(address: String) = action {
        val remaining = savedConnections.value.filter { it.address != address }
        require(prefs.edit().putString("servers", SavedServers.encode(remaining)).commit()) {
            "Could not remove the saved server."
        }
        savedConnections.value = remaining
        if (prefs.getString("origin", null) == address) {
            vault.clear()
            prefs.edit { remove("origin") }
        }
    }

    private val mutable = MutableStateFlow(Screen(address = prefs.getString("origin", "") ?: ""))
    internal val screen = mutable.asStateFlow()
    internal val player = MutableStateFlow<ExoPlayer?>(null)
    internal val pendingSaves = MutableStateFlow(0)
    private var session: MediaSession? = null
    private var conversion: Pair<ServerConnection, String>? = null
    private var cleanup: Job? = null
    private var selectedCaptions = false
    private var changingQuality = false
    private var connection: ServerConnection? = null
    private var operation: Job? = null
    private var ticker: Job? = null
    private val progressMutex = Mutex()
    private var progress: ProgressState? = null
    private var epoch = 0
    private var actionId = 0
    private var playbackGeneration = 0

    private fun message(e: Exception): String =
        when (e) {
            is SSLException ->
                "TLS connection failed. Check the server certificate and your device clock."
            is HttpException ->
                when (e.code()) {
                    401 -> "Sign-in expired or credentials were rejected."
                    403 -> "Access denied. Check your account permissions."
                    409 ->
                        "Progress changed on another device. Reopen this video to resume the server position."
                    429 -> "Too many requests. Wait a minute and try again."
                    else -> "Server request failed (${e.code()}). Try again."
                }
            is kotlinx.serialization.SerializationException ->
                "The server returned an invalid API response."
            is IllegalArgumentException -> e.message?.take(220) ?: "Invalid input."
            is IOException ->
                "Cannot reach the server. Check its address, connection and server status."
            else -> "The operation failed. Please reconnect and try again."
        }

    private fun action(block: suspend () -> Unit) {
        val previous = operation
        previous?.cancel()
        val generation = ++actionId
        operation = viewModelScope.launch {
            previous?.join()
            mutable.update { it.copy(busy = true, error = null) }
            try {
                block()
            } catch (e: TimeoutCancellationException) {
                if (actionId == generation)
                    mutable.update {
                        it.copy(
                            error =
                                "The server operation timed out. Check the connection and retry."
                        )
                    }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (actionId == generation) mutable.update { it.copy(error = message(e)) }
            } finally {
                if (actionId == generation) mutable.update { it.copy(busy = false) }
            }
        }
    }

    internal fun connect(address: String, remember: Boolean, serverName: String = "") = action {
        val origin = ServerAddress.parse(address, BuildConfig.DEBUG)
        stopPlayer(false)
        connection?.close()
        connection = null
        val generation = ++epoch
        mutable.update { it.copy(busy = true) }
        // Restoration is bound to exactly one remembered origin.
        val restore =
            remember &&
                prefs.getString("origin", null) == origin.toString() &&
                try {
                    vault.read() != null
                } catch (_: Exception) {
                    vault.clear()
                    false
                }
        if (!restore) vault.clear()
        val conn =
            ServerConnection(origin, vault, remember, restore) {
                viewModelScope.launch { if (epoch == generation) expire() }
            }
        connection = conn
        try {
            val info = conn.check()
            val updatedServers =
                SavedServers.remember(
                    savedConnections.value,
                    origin.toString(),
                    serverName,
                    BuildConfig.DEBUG,
                )
            require(
                prefs
                    .edit()
                    .putString("origin", origin.toString())
                    .putString("servers", SavedServers.encode(updatedServers))
                    .commit()
            ) {
                "Connected, but the server address could not be saved."
            }
            savedConnections.value = updatedServers
            mutable.value =
                Screen(
                    phase = "login",
                    address = origin.toString(),
                    remember = remember,
                    version = info.serverVersion,
                    busy = true,
                )
            if (restore) {
                try {
                    val me = conn.api.me()
                    signedIn(conn, me)
                } catch (e: HttpException) {
                    if (e.code() != 401) throw e
                }
            }
        } finally {
            mutable.update { it.copy(busy = false) }
        }
    }

    internal fun login(name: String, password: String) = action {
        require(name.isNotBlank() && name.length <= 64 && password.length in 12..256) {
            "Enter a username and a password of 12–256 characters."
        }
        val conn = requireNotNull(connection)
        val me = conn.api.login(conn.token(), LoginRequest(name, password, "Android phone"))
        signedIn(conn, me)
    }

    private suspend fun signedIn(conn: ServerConnection, me: UserIdentity) {
        require(me.name.length <= 64 && me.role.length <= 32) { "Invalid account response." }
        mutable.update { it.copy(phase = "library", user = me.name) }
        val codecs =
            MediaCodecList(MediaCodecList.REGULAR_CODECS)
                .codecInfos
                .filterNot { it.isEncoder }
                .flatMap { it.supportedTypes.toList() }
                .distinct()
                .filter { it.length <= 64 }
                .take(32)
        conn.api.capabilities(
            conn.token(),
            ClientCapabilities("android", "Media3", listOf("mp4", "webm", "matroska"), codecs),
        )
        refreshNow(conn)
    }

    private fun validate(items: List<MediaSummary>): List<MediaSummary> = items.also { list ->
        require(
            list.size <= 200 &&
                list.all {
                    it.id > 0 &&
                        it.title.length <= 512 &&
                        it.duration.isFinite() &&
                        it.duration in 0.0..1e9 &&
                        it.size >= 0
                }
        ) {
            "Invalid media response."
        }
    }

    private suspend fun refreshNow(conn: ServerConnection, more: Boolean = false) {
        val old = mutable.value
        val page = conn.api.page(old.prefix, if (more) old.cursor else null, old.libraryId)
        require((page.nextCursor?.length ?: 0) <= 4096) { "Invalid page cursor." }
        val items = validate(page.items)
        val continuing = validate(conn.api.continuing())
        val libraries =
            conn.api.libraries().also {
                require(it.size <= 200 && it.all { l -> l.id > 0 && l.name.length <= 128 }) {
                    "Invalid libraries."
                }
            }
        mutable.update {
            it.copy(
                items =
                    (if (more) (it.items + items).distinctBy { m -> m.id } else items).take(2000),
                cursor = page.nextCursor,
                continuing = continuing,
                libraries = libraries,
            )
        }
    }

    internal fun refresh(more: Boolean = false) = action {
        refreshNow(requireNotNull(connection), more)
    }

    internal fun search(prefix: String, libraryId: Int? = mutable.value.libraryId) = action {
        require(prefix.length <= 128 && prefix.none { it.isISOControl() }) {
            "Search must contain at most 128 characters."
        }
        mutable.update {
            it.copy(
                prefix = prefix.trim(),
                libraryId = libraryId,
                items = emptyList(),
                cursor = null,
            )
        }
        refreshNow(requireNotNull(connection))
    }

    internal fun details(id: Int) = action {
        val conn = requireNotNull(connection)
        val detail = conn.api.details(id)
        require(
            detail.id == id &&
                detail.title.length <= 512 &&
                detail.duration.isFinite() &&
                detail.duration in 0.0..1e9 &&
                detail.container.length <= 64 &&
                detail.videoCodec.length <= 64 &&
                detail.audioCodec.length <= 64
        ) {
            "Invalid media details."
        }
        val tracks = conn.api.tracks(id)
        require(
            tracks.tracks.size <= 200 &&
                tracks.tracks.all {
                    it.codec.length <= 64 &&
                        (it.title?.length ?: 0) <= 512 &&
                        (it.language?.length ?: 0) <= 64
                }
        ) {
            "Invalid tracks."
        }
        val options =
            try {
                conn.api.playbackOptions(id)
            } catch (e: HttpException) {
                if (e.code() == 404) null else throw e
            }
        if (options != null)
            require(
                options.tracks.size <= 256 &&
                    options.encoders.size <= 5 &&
                    options.conversionNotice.length <= 2048
            ) {
                "Invalid playback options."
            }
        mutable.update {
            it.copy(
                detail = detail,
                tracks = tracks,
                playbackOptions = options,
                audioTrack = null,
                subtitleTrack = null,
                conversionStatus = null,
                quality = "Auto",
            )
        }
    }

    internal fun play() = action {
        stopPlayer()
        cleanup?.join()
        awaitSaves()
        startPlayback()
    }

    private suspend fun awaitSaves() {
        withTimeout(10000) { while (pendingSaves.value > 0) delay(25) }
    }

    private suspend fun startPlayback(prepared: ConversionState? = null) {
        val conn = requireNotNull(connection)
        val media = requireNotNull(mutable.value.detail)
        ++playbackGeneration
        val saved = conn.api.progress(media.id)
        require(
            saved.mediaItemId == media.id &&
                saved.seconds.isFinite() &&
                saved.seconds in 0.0..(media.duration + 2).coerceAtLeast(2.0) &&
                saved.revision >= 0
        ) {
            "Invalid playback progress."
        }
        progress = saved
        mutable.update { it.copy(syncError = null, videoReady = false) }
        val active =
            ExoPlayer.Builder(getApplication<Application>())
                .setMediaSourceFactory(
                    DefaultMediaSourceFactory(OkHttpDataSource.Factory(conn.mediaClient))
                )
                .build()
        active.setAudioAttributes(
            AudioAttributes.Builder()
                .setUsage(C.USAGE_MEDIA)
                .setContentType(C.AUDIO_CONTENT_TYPE_MOVIE)
                .build(),
            true,
        )
        active.setHandleAudioBecomingNoisy(true)
        active.addListener(
            object : Player.Listener {
                override fun onRenderedFirstFrame() {
                    if (player.value === active) {
                        changingQuality = false
                        mutable.update { it.copy(videoReady = true) }
                    }
                }

                override fun onIsPlayingChanged(isPlaying: Boolean) {
                    if (!isPlaying && player.value === active) save()
                }

                override fun onPlaybackStateChanged(playbackState: Int) {
                    if (playbackState == Player.STATE_ENDED && player.value === active) save(true)
                }

                override fun onPlayerError(error: PlaybackException) {
                    if (player.value !== active) return
                    val code =
                        when (error.errorCode) {
                            in 2000..2999 -> "network"
                            in 4000..4999 -> "decode"
                            else -> "unsupported"
                        }
                    mutable.update {
                        it.copy(
                            error =
                                "Playback failed ($code). Check the connection and codec support. Reopen to retry."
                        )
                    }
                    viewModelScope.launch {
                        try {
                            conn.api.report(
                                media.id,
                                conn.token(),
                                PlaybackError(code, "android-media3"),
                            )
                        } catch (_: Exception) {
                            /* Error reports are best effort; never log credentials. */
                        }
                    }
                }
            }
        )
        player.value = active
        session = MediaSession.Builder(getApplication<Application>(), active).build()
        active.setMediaItem(
            if (prepared == null) MediaItem.fromUri(conn.streamUrl(media.id))
            else hlsItem(conn, prepared.id)
        )
        if (!saved.completed) active.seekTo((saved.seconds * 1000).toLong())
        active.prepare()
        active.play()
        ticker = viewModelScope.launch {
            while (isActive) {
                delay(5000)
                if (active.isPlaying && pendingSaves.value == 0) save()
            }
        }
    }

    internal fun chooseAudio(index: Int?) {
        mutable.update { it.copy(audioTrack = index) }
    }

    internal fun chooseSubtitle(index: Int?) {
        mutable.update { it.copy(subtitleTrack = index) }
    }

    internal fun chooseQuality(value: String) {
        require(value in listOf("Auto", "360p", "480p", "720p"))
        mutable.update { it.copy(quality = value) }
        val (conn, id) = conversion ?: return
        val active = player.value ?: return
        val position = active.currentPosition
        val playing = active.playWhenReady
        changingQuality = true
        mutable.update { it.copy(videoReady = false) }
        active.setMediaItem(hlsItem(conn, id), position)
        active.prepare()
        active.playWhenReady = playing
    }

    private fun hlsItem(conn: ServerConnection, id: String): MediaItem {
        val file =
            if (mutable.value.quality == "Auto") "master.m3u8" else "${mutable.value.quality}.m3u8"
        val item =
            MediaItem.Builder()
                .setUri(conn.playbackUrl(id, file))
                .setMimeType(MimeTypes.APPLICATION_M3U8)
        if (selectedCaptions)
            item.setSubtitleConfigurations(
                listOf(
                    MediaItem.SubtitleConfiguration.Builder(
                            Uri.parse(conn.playbackUrl(id, "captions.vtt"))
                        )
                        .setMimeType(MimeTypes.TEXT_VTT)
                        .setLanguage("und")
                        .setSelectionFlags(C.SELECTION_FLAG_DEFAULT)
                        .build()
                )
            )
        return item.build()
    }

    internal fun prepareHls() = action {
        val conn = requireNotNull(connection)
        val media = requireNotNull(mutable.value.detail)
        val selection = mutable.value
        stopPlayer()
        cleanup?.join()
        awaitSaves()
        var consumed = false
        try {
            var state =
                PlaybackResources.validate(
                    conn.api.prepare(
                        media.id,
                        conn.token(),
                        PlaybackSelection(selection.audioTrack, selection.subtitleTrack),
                    )
                )
            conversion = Pair(conn, state.id)
            selectedCaptions = selection.subtitleTrack != null
            withTimeout(31 * 60 * 1000L) {
                while (state.state == "preparing") {
                    mutable.update { it.copy(conversionStatus = "Preparing HLS on the server…") }
                    delay(1500)
                    ensureActive()
                    require(connection === conn && mutable.value.detail?.id == media.id) {
                        "Playback selection changed."
                    }
                    val next = PlaybackResources.validate(conn.api.conversion(state.id))
                    require(next.id == state.id) { "Conversion identity changed." }
                    state = next
                }
            }
            require(state.state == "ready") {
                "HLS conversion failed. Check FFmpeg and server limits."
            }
            mutable.update { it.copy(conversionStatus = "HLS ready · ${state.encoder}") }
            startPlayback(state)
            consumed = true
        } finally {
            if (!consumed) stopPlayer(false)
        }
    }

    internal fun cancelPreparation() {
        operation?.cancel()
        mutable.update { it.copy(conversionStatus = "Preparation cancelled") }
    }

    internal fun save(completed: Boolean = false) {
        if (changingQuality) return
        val active = player.value ?: return
        val conn = connection ?: return
        val id = mutable.value.detail?.id ?: return
        val generation = playbackGeneration
        val seconds =
            (active.currentPosition.coerceAtLeast(0) / 1000.0).coerceAtMost(
                mutable.value.detail!!.duration.coerceAtLeast(0.0)
            )
        pendingSaves.value += 1
        viewModelScope.launch {
            try {
                progressMutex.withLock {
                    if (
                        generation != playbackGeneration ||
                            connection !== conn ||
                            mutable.value.syncError != null ||
                            progress?.mediaItemId != id
                    )
                        return@withLock
                    val previous = progress ?: return@withLock
                    try {
                        val updated =
                            conn.api.sync(
                                id,
                                conn.token(),
                                ProgressUpdate(seconds, previous.revision, completed),
                            )
                        if (connection === conn && generation == playbackGeneration)
                            progress = updated
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        if (connection === conn && generation == playbackGeneration)
                            mutable.update {
                                it.copy(
                                    syncError =
                                        message(e) +
                                            " Progress saving is paused; reopen the video to sync."
                                )
                            }
                    }
                }
            } finally {
                pendingSaves.value -= 1
            }
        }
    }

    internal fun background() {
        player.value?.pause()
        save()
    }

    private fun stopPlayer(persist: Boolean = true) {
        if (persist) save()
        ticker?.cancel()
        ticker = null
        session?.release()
        session = null
        val active = player.value
        player.value = null
        active?.release()
        mutable.update { it.copy(fullscreen = false, videoReady = false) }
        val previous = conversion
        conversion = null
        changingQuality = false
        if (previous != null)
            cleanup = viewModelScope.launch {
                try {
                    withTimeout(5000) {
                        previous.first.api.release(previous.second, previous.first.token())
                    }
                } catch (_: Exception) {
                    /* Revoked/unreachable sessions expire on the server. */
                }
            }
    }

    internal fun fullscreen() {
        mutable.update { it.copy(fullscreen = !it.fullscreen) }
    }

    internal fun back() {
        if (mutable.value.busy) operation?.cancel()
        when {
            mutable.value.fullscreen -> fullscreen()
            player.value != null -> stopPlayer()
            mutable.value.detail != null -> {
                mutable.update { it.copy(detail = null, tracks = null, syncError = null) }
                refresh()
            }
        }
    }

    internal fun logout() = action {
        val conn = connection
        // Finish an in-flight progress write on this connection before revoking it.
        player.value?.pause()
        save(player.value?.playbackState == Player.STATE_ENDED)
        withTimeoutOrNull(5000) { pendingSaves.first { it == 0 } }
        stopPlayer(false)
        var revoked = false
        try {
            withTimeout(8000) {
                conn?.api?.logout(conn.token())
                revoked = true
            }
        } finally {
            conn?.close()
            vault.clear()
            connection = null
            ++epoch
            mutable.value =
                Screen(
                    address = prefs.getString("origin", "") ?: "",
                    error =
                        if (revoked) null
                        else
                            "Signed out locally. Server revocation could not be confirmed; revoke the session in web settings.",
                )
        }
    }

    private fun expire() {
        ++actionId
        operation?.cancel()
        stopPlayer(false)
        connection?.close()
        connection = null
        vault.clear()
        ++epoch
        mutable.value =
            Screen(
                address = prefs.getString("origin", "") ?: "",
                error = "Session expired or was revoked. Connect and sign in again.",
            )
    }

    override fun onCleared() {
        stopPlayer(false)
        connection?.close(clearSaved = false)
    }
}
