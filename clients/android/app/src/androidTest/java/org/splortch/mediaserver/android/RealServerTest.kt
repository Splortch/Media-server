package org.splortch.mediaserver.android

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.media3.common.util.UnstableApi
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.security.KeyStore
import kotlinx.coroutines.runBlocking
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.splortch.mediaserver.core.*
import retrofit2.HttpException

@UnstableApi
@RunWith(AndroidJUnit4::class)
class RealServerTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()

    @Test
    fun realBackendPlaybackAndRevocation() {
        compose.onNodeWithText("Server address").performTextInput("http://127.0.0.1:5050")
        compose.onNodeWithText("Connect", useUnmergedTree = true).performScrollTo().performClick()
        compose.waitUntil(30000) {
            compose.activity.model.screen.value.phase == "login" &&
                !compose.activity.model.screen.value.busy
        }
        compose.onNodeWithText("Username").performTextInput("android-fixture")
        compose.onNodeWithText("Password").performTextInput("android-fixture-password")
        compose.onNodeWithText("Sign in").performScrollTo().performClick()
        compose.waitUntil(30000) { compose.activity.model.screen.value.items.isNotEmpty() }
        compose.onAllNodesWithText("android-sample").onFirst().performScrollTo().performClick()
        compose.waitUntil(10000) { compose.activity.model.screen.value.detail != null }
        compose.onNodeWithText("Play / resume").performScrollTo().performClick()
        compose.waitUntil(30000) { compose.activity.model.screen.value.videoReady }
        compose.runOnUiThread {
            assertTrue(compose.activity.model.player.value!!.currentPosition >= 2900)
        }
        compose.onNodeWithText("Prepare HLS playback").performScrollTo().performClick()
        compose.waitUntil(180000) {
            compose.activity.model.screen.value.conversionStatus?.startsWith("HLS ready") == true &&
                compose.activity.model.screen.value.videoReady
        }
        compose.runOnUiThread { compose.activity.model.chooseQuality("360p") }
        compose.waitUntil(30000) { compose.activity.model.screen.value.videoReady }
        compose.runOnUiThread {
            assertTrue(
                compose.activity.model.player.value!!
                    .currentMediaItem!!
                    .localConfiguration!!
                    .uri
                    .toString()
                    .endsWith("/360p.m3u8")
            )
            assertTrue(compose.activity.model.player.value!!.currentPosition >= 2900)
        }
        compose.runOnUiThread { compose.activity.model.chooseQuality("Auto") }
        compose.waitUntil(30000) { compose.activity.model.screen.value.videoReady }
        compose.runOnUiThread {
            assertTrue(
                compose.activity.model.player.value!!
                    .currentMediaItem!!
                    .localConfiguration!!
                    .uri
                    .toString()
                    .endsWith("/master.m3u8")
            )
        }
        compose.activityRule.scenario.recreate()
        compose.waitUntil(10000) { compose.activity.model.screen.value.videoReady }
        val context = compose.activity.applicationContext
        val file = java.io.File(context.noBackupFilesDir, "session-v1")
        val vault = KeystoreVault(context)
        assertTrue(file.exists())
        assertFalse(file.readBytes().toString(Charsets.UTF_8).contains("MediaServer.Session"))
        val saved = vault.read()!!
        vault.write(saved)
        val one = file.readBytes()
        vault.write(saved)
        assertFalse(one.contentEquals(file.readBytes()))
        val keys = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        assertNull(keys.getKey(KeystoreVault.ALIAS, null).encoded)
        val controlVault =
            object : SessionVault {
                override fun read(): String? = null

                override fun write(value: String) {}

                override fun clear() {}
            }
        val control = ServerConnection("http://127.0.0.1:5050".toHttpUrl(), controlVault, false)
        runBlocking {
            control.api.login(
                control.token(),
                LoginRequest(
                    "android-fixture",
                    "android-fixture-password",
                    "instrumentation-control",
                ),
            )
            compose.runOnUiThread { compose.activity.model.player.value!!.pause() }
            // Let the pause-triggered write settle before a competing device changes the revision.
            compose.waitUntil(10000) { compose.activity.model.pendingSaves.value == 0 }
            var updated = false
            repeat(10) {
                if (!updated) {
                    val progress = control.api.progress(1)
                    try {
                        control.api.sync(
                            1,
                            control.token(),
                            ProgressUpdate(15.0, progress.revision),
                        )
                        updated = true
                    } catch (e: HttpException) {
                        if (e.code() != 409) throw e
                    }
                }
            }
            assertTrue(updated)
            compose.runOnUiThread { compose.activity.model.save() }
            compose.waitUntil(10000) { compose.activity.model.screen.value.syncError != null }
            assertEquals(15.0, control.api.progress(1).seconds, 0.01)
            compose.runOnUiThread { compose.activity.model.back() }
            compose.onNodeWithText("Play / resume").performScrollTo().performClick()
            compose.waitUntil(30000) { compose.activity.model.screen.value.videoReady }
            compose.runOnUiThread {
                assertTrue(compose.activity.model.player.value!!.currentPosition >= 14900)
            }
            val phone = control.api.sessions().first { it.deviceName == "Android phone" }
            control.api.revoke(phone.id, control.token())
            compose.runOnUiThread { compose.activity.model.refresh() }
            compose.waitUntil(10000) { compose.activity.model.screen.value.phase == "connect" }
            assertNull(compose.activity.model.player.value)
            assertNull(vault.read())
            assertTrue(compose.activity.model.screen.value.items.isEmpty())
        }
        control.close()
    }
}
