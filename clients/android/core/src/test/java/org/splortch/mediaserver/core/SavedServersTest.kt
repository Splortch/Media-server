package org.splortch.mediaserver.core

import org.junit.Assert.*
import org.junit.Test

class SavedServersTest {
    @Test
    fun canonicalAddressesRenameAndSurviveReload() {
        val first = SavedServers.remember(emptyList(), "https://HOME.example", "Home")
        val two = SavedServers.remember(first, "https://family.example:8443", "Family")
        val renamed = SavedServers.remember(two, "https://home.example/", "Living room")
        assertEquals(2, renamed.size)
        assertEquals("Living room", renamed.first().name)
        assertEquals(renamed, SavedServers.read(SavedServers.encode(renamed)))
        val removed = renamed.filter { it.address != "https://home.example/" }
        assertEquals("Family", SavedServers.read(SavedServers.encode(removed)).single().name)
    }

    @Test
    fun corruptOrUnsafeSavedResponsesCannotCreateConnections() {
        assertTrue(SavedServers.read("not json").isEmpty())
        assertTrue(SavedServers.read("x".repeat(32769)).isEmpty())
        for (address in
            listOf(
                "http://192.168.1.50",
                "https://user:password@example.org",
                "https://example.org/api",
                "https://example.org?token=secret",
            )) {
            assertTrue(SavedServers.read("""[{"address":"$address","name":"Unsafe"}]""").isEmpty())
        }
        val safe =
            SavedServers.read(
                """[{"address":"https://safe.example","name":"Safe","token":"secret"}]"""
            )
        assertFalse(SavedServers.encode(safe).contains("secret"))
    }

    @Test
    fun catalogSizeAndNamesAreBounded() {
        var servers = emptyList<SavedServer>()
        repeat(25) {
            servers = SavedServers.remember(servers, "https://server$it.example", "x".repeat(100))
        }
        assertEquals(20, servers.size)
        assertEquals(80, servers.first().name.length)
    }
}
