package org.splortch.mediaserver.core

object PlaybackResources {
    fun validSession(value: String) = value.matches(Regex("[a-fA-F0-9]{32}"))

    fun validFile(value: String) =
        value.matches(
            Regex(
                """(?:master|360p|480p|720p)\.m3u8|captions\.vtt|(?:360p|480p|720p)_[0-9]{5,10}\.ts"""
            )
        )

    fun allowedPath(value: String): Boolean {
        if (value.matches(Regex("/api/v1/media/[1-9][0-9]*/stream"))) return true
        val parts = value.split('/')
        return parts.size == 7 &&
            parts.take(4) == listOf("", "api", "v1", "playback-sessions") &&
            validSession(parts[4]) &&
            parts[5] == "files" &&
            validFile(parts[6])
    }

    fun validate(state: ConversionState): ConversionState {
        require(
            validSession(state.id) &&
                state.state in listOf("preparing", "ready", "failed", "cancelled") &&
                state.encoder.length <= 64 &&
                state.encodeSeconds.isFinite() &&
                state.encodeSeconds >= 0 &&
                (state.error?.length ?: 0) <= 128 &&
                state.manifest == "/api/v1/playback-sessions/${state.id}/files/master.m3u8"
        ) {
            "Invalid conversion response."
        }
        return state
    }
}
