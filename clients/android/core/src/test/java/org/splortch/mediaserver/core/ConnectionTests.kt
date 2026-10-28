package org.splortch.mediaserver.core

import java.io.IOException
import kotlinx.coroutines.runBlocking
import okhttp3.Cookie
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.*
import org.junit.Test
import retrofit2.HttpException

class ConnectionTests {
    private class MemoryVault : SessionVault {
        var value: String? = null

        override fun read() = value

        override fun write(value: String) {
            this.value = value
        }

        override fun clear() {
            value = null
        }
    }

    @Test
    fun serverOrigins() {
        for (bad in
            listOf(
                "",
                "file:///video",
                "https://u:p@example.com",
                "https://example.com/path",
                "https://example.com?q=1",
                "https://example.com/#x",
                "http://192.168.1.2:5050",
                "http://10.0.2.2",
            )) {
            try {
                ServerAddress.parse(bad)
                fail(bad)
            } catch (_: IllegalArgumentException) {}
        }
        assertEquals("https://example.com/", ServerAddress.parse("https://example.com").toString())
        assertEquals("10.0.2.2", ServerAddress.parse("http://10.0.2.2", true).host)
        assertEquals("127.0.0.1", ServerAddress.parse("http://127.0.0.1:5050").host)
    }

    @Test
    fun cookiesStayOnExactOriginAndLogoutCannotBeUndone() {
        val url = "https://example.com/".toHttpUrl()
        val vault = MemoryVault()
        val jar = ProtectedCookies(url, vault, true)
        val cookie = Cookie.parse(url, "MediaServer.Session=secret; Path=/; Secure; HttpOnly")!!
        jar.saveFromResponse(url, listOf(cookie))
        assertEquals(1, jar.loadForRequest(url).size)
        assertTrue(jar.loadForRequest("https://example.com:444/".toHttpUrl()).isEmpty())
        assertTrue(jar.loadForRequest("http://example.com/".toHttpUrl()).isEmpty())
        jar.close(false)
        val restored = ProtectedCookies(url, vault, true).apply { restore() }
        assertEquals(1, restored.loadForRequest(url).size)
        restored.close()
        restored.saveFromResponse(url, listOf(cookie))
        assertNull(vault.value)
        assertTrue(restored.loadForRequest(url).isEmpty())
    }

    @Test
    fun corruptSavedSessionsAreCleared() {
        val vault = MemoryVault().apply { value = "not-json" }
        try {
            ProtectedCookies("https://example.com".toHttpUrl(), vault, true).restore()
            fail()
        } catch (_: IOException) {}
        assertNull(vault.value)
    }

    @Test
    fun incompatibleServerAndRedirectsFail() = runBlocking {
        MockWebServer().use { server ->
            server.enqueue(
                MockResponse()
                    .setHeader("Content-Type", "application/json")
                    .setBody(
                        """{"name":"test","serverVersion":"1","apiVersion":2,"minimumClientApiVersion":2,"setupRequired":false,"features":[]}"""
                    )
            )
            server.enqueue(
                MockResponse().setResponseCode(302).setHeader("Location", "https://example.com")
            )
            val conn = ServerConnection(server.url("/"), MemoryVault(), false)
            try {
                conn.check()
                fail()
            } catch (_: IllegalArgumentException) {}
            try {
                conn.api.info()
                fail()
            } catch (e: HttpException) {
                assertEquals(302, e.code())
            }
            conn.close()
        }
    }

    @Test
    fun unauthorizedClearsCredentialsAndResponsesAreBounded() = runBlocking {
        MockWebServer().use { server ->
            server.enqueue(MockResponse().setResponseCode(401))
            server.enqueue(MockResponse().setBody("x".repeat(2 * 1024 * 1024 + 1)))
            var expired = false
            val vault = MemoryVault()
            val conn = ServerConnection(server.url("/"), vault, true, expired = { expired = true })
            try {
                conn.api.me()
                fail()
            } catch (e: HttpException) {
                assertEquals(401, e.code())
            }
            assertTrue(expired)
            assertNull(vault.value)
            try {
                conn.api.info()
                fail()
            } catch (_: IOException) {}
            conn.close()
        }
    }

    @Test
    fun streamedResponsesAreBoundedAndMediaPathsAreScoped() = runBlocking {
        MockWebServer().use { server ->
            server.enqueue(MockResponse().setChunkedBody("x".repeat(2 * 1024 * 1024 + 1), 8192))
            val conn = ServerConnection(server.url("/"), MemoryVault(), false)
            try {
                conn.api.info()
                fail()
            } catch (_: IOException) {}
            try {
                conn.mediaClient
                    .newCall(okhttp3.Request.Builder().url(server.url("/redirect")).build())
                    .execute()
                fail()
            } catch (_: IOException) {}
            conn.close()
        }
    }

    @Test
    fun disconnectedServerFails() = runBlocking {
        val server = MockWebServer()
        server.start()
        val origin = server.url("/")
        server.close()
        val conn = ServerConnection(origin, MemoryVault(), false)
        try {
            conn.api.info()
            fail()
        } catch (_: IOException) {}
        conn.close()
    }
}
