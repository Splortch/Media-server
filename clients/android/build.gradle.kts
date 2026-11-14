buildscript {
    dependencies {
        // AGP's built-in Kotlin compiler must match Compose and serialization.
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.4.21")
    }
}

plugins {
    id("com.android.application") version "9.5.1" apply false
    id("com.android.library") version "9.5.1" apply false
    kotlin("plugin.compose") version "2.4.21" apply false
    kotlin("plugin.serialization") version "2.4.21" apply false
}

apply(from = "dependency-locks.gradle.kts")
