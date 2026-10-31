package org.splortch.mediaserver.core

import org.junit.Assert.*
import org.junit.Test

class AppThemeTest {
    @Test
    fun darkIsTheDefaultAndUnknownValuesAreSafe() {
        assertEquals(AppTheme.Dark, AppTheme.parse(null))
        assertEquals(AppTheme.Dark, AppTheme.parse("system"))
        assertEquals(AppTheme.Dark, AppTheme.parse("unknown-future-theme"))
        assertEquals(AppTheme.Light, AppTheme.parse("light"))
    }

    @Test
    fun registryKeysRoundTripAndAreUnique() {
        assertEquals(AppTheme.entries.size, AppTheme.entries.map { it.key }.distinct().size)
        AppTheme.entries.forEach { assertEquals(it, AppTheme.parse(it.key)) }
    }
}
