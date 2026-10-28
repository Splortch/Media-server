package org.splortch.mediaserver.core

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

object ServerAddress {
    fun parse(text: String, emulator: Boolean = false): HttpUrl {
        require(text.length <= 512) { "Server address is too long." }
        val url = text.trim().toHttpUrlOrNull() ?: throw IllegalArgumentException("Enter a valid HTTPS server address.")
        require(url.username.isEmpty() && url.password.isEmpty() && url.encodedPath == "/" && url.query == null && url.fragment == null) {
            "Use a server origin without credentials, path, query or fragment."
        }
        require(url.isHttps || url.host in setOf("localhost", "127.0.0.1") || (emulator && url.host == "10.0.2.2")) {
            "Remote servers require HTTPS with a trusted certificate. For USB testing use adb reverse and http://127.0.0.1:5050."
        }
        return url
    }
}
