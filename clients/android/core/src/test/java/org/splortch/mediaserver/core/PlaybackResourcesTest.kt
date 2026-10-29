package org.splortch.mediaserver.core

import org.junit.Assert.*
import org.junit.Test

class PlaybackResourcesTest {
    @Test
    fun resourcesStayInsideAuthorizedApiPaths() {
        val id = "abcdef0123456789abcdef0123456789"
        for (file in listOf("master.m3u8", "360p.m3u8", "720p_00002.ts", "captions.vtt")) {
            assertTrue(PlaybackResources.allowedPath("/api/v1/playback-sessions/$id/files/$file"))
        }
        for (file in
            listOf(
                "../source.bin",
                "https://evil.invalid/a.ts",
                "captions.vtt?token=x",
                "360p_..ts",
            )) {
            assertFalse(PlaybackResources.validFile(file))
        }
        assertFalse(
            PlaybackResources.allowedPath("/api/v1/playback-sessions/wrong/files/master.m3u8")
        )
        assertTrue(PlaybackResources.allowedPath("/api/v1/media/1/stream"))
    }

    @Test
    fun malformedConversionResponsesAreRejected() {
        val id = "abcdef0123456789abcdef0123456789"
        val valid =
            ConversionState(
                id,
                "ready",
                "libx264",
                null,
                1.0,
                "/api/v1/playback-sessions/$id/files/master.m3u8",
            )
        assertEquals(valid, PlaybackResources.validate(valid))
        for (bad in
            listOf(
                valid.copy(manifest = "https://evil.invalid/master.m3u8"),
                valid.copy(encodeSeconds = Double.NaN),
                valid.copy(id = "../secret"),
            )) {
            try {
                PlaybackResources.validate(bad)
                fail("Unsafe response was accepted")
            } catch (_: IllegalArgumentException) {}
        }
    }
}
