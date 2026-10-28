package org.splortch.mediaserver.core

import java.io.IOException
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.CookieJar
import okhttp3.HttpUrl

@Serializable private data class SavedCookies(val origin: String, val cookies: List<String>)

class ProtectedCookies(
    private val origin: HttpUrl,
    private val vault: SessionVault,
    private val remember: Boolean,
) : CookieJar {
    private val cookies = linkedMapOf<String, Cookie>()
    private var closed = false

    private fun auth(name: String) = name.matches(Regex("MediaServer\\.Session(?:C[0-9]+)?"))

    private fun allowed(c: Cookie) =
        c.hostOnly &&
            c.domain == origin.host &&
            c.path == "/" &&
            c.value.length <= 8192 &&
            (auth(c.name) || c.name.startsWith(".AspNetCore.Antiforgery."))

    fun sameOrigin(url: HttpUrl) =
        origin.scheme == url.scheme && origin.host == url.host && origin.port == url.port

    @Synchronized
    fun restore() {
        try {
            val saved = vault.read()?.let { Json.decodeFromString<SavedCookies>(it) } ?: return
            require(saved.origin == origin.toString() && saved.cookies.size <= 32)
            val restored = saved.cookies.map { Cookie.parse(origin, it) ?: error("Invalid cookie") }
            require(restored.all { allowed(it) && auth(it.name) })
            restored
                .filter { it.expiresAt > System.currentTimeMillis() }
                .forEach { cookies[it.name] = it }
        } catch (e: Exception) {
            vault.clear()
            throw IOException("Saved session cannot be restored; please sign in again.", e)
        }
    }

    @Synchronized
    override fun saveFromResponse(url: HttpUrl, cookies: List<Cookie>) {
        if (closed || !sameOrigin(url)) return
        cookies.filter(::allowed).forEach {
            if (it.expiresAt <= System.currentTimeMillis()) this.cookies.remove(it.name)
            else this.cookies[it.name] = it
        }
        if (this.cookies.size > 32 || this.cookies.values.sumOf { it.toString().length } > 65536) {
            close()
            throw IOException("Server sent too many cookies.")
        }
        if (remember)
            vault.write(
                Json.encodeToString(
                    SavedCookies(
                        origin.toString(),
                        this.cookies.values.filter { auth(it.name) }.map { it.toString() },
                    )
                )
            )
    }

    @Synchronized
    override fun loadForRequest(url: HttpUrl): List<Cookie> =
        if (closed || !sameOrigin(url)) emptyList()
        else cookies.values.filter { it.expiresAt > System.currentTimeMillis() && it.matches(url) }

    @Synchronized
    fun close(clearSaved: Boolean = true) {
        closed = true
        cookies.clear()
        if (clearSaved) vault.clear()
    }
}
