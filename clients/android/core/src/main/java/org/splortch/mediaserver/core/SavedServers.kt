package org.splortch.mediaserver.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Serializable data class SavedServer(val address: String, val name: String)

/** Device-local address book. Contains no cookies, passwords, API keys or user data. */
object SavedServers {
    private val json = Json { ignoreUnknownKeys = true }

    fun read(text: String?, emulator: Boolean = false): List<SavedServer> {
        if (text == null || text.length > 32768) return emptyList()
        return try {
            json
                .decodeFromString<List<SavedServer>>(text)
                .take(20)
                .mapNotNull { saved ->
                    try {
                        entry(saved.address, saved.name, emulator)
                    } catch (_: IllegalArgumentException) {
                        null
                    }
                }
                .distinctBy { it.address }
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun remember(
        servers: List<SavedServer>,
        address: String,
        name: String,
        emulator: Boolean = false,
    ): List<SavedServer> {
        val server = entry(address, name, emulator)
        return (listOf(server) + servers.filter { it.address != server.address }).take(20)
    }

    fun encode(servers: List<SavedServer>): String = json.encodeToString(servers.take(20))

    private fun entry(address: String, name: String, emulator: Boolean): SavedServer {
        val origin = ServerAddress.parse(address, emulator)
        return SavedServer(origin.toString(), name.trim().take(80).ifEmpty { origin.host })
    }
}
