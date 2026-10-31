package org.splortch.mediaserver.core

/** Stable preference keys; unknown future/legacy values use the safe dark default. */
enum class AppTheme(val key: String, val label: String) {
    Dark("dark", "Dark"),
    Light("light", "Light");

    companion object {
        fun parse(key: String?): AppTheme = entries.firstOrNull { it.key == key } ?: Dark
    }
}
