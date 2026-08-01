plugins {
    // Bumped from 8.4.2 — Kotlin 2.4.10's Gradle plugin refuses to apply on
    // anything below AGP 8.5.2. 8.6.1 (not higher) deliberately, to stay
    // within the installed Gradle 8.7's supported range (AGP 8.7+ needs
    // Gradle 8.9+, which isn't installed on the release build machine).
    id("com.android.application") version "8.6.1" apply false
    // Bumped from 1.9.24 for Compose: the Compose Compiler is now a Kotlin
    // Gradle plugin (org.jetbrains.kotlin.plugin.compose) shipped in lockstep
    // with Kotlin itself since 2.0.
    id("org.jetbrains.kotlin.android") version "2.4.10" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.4.10" apply false
    id("org.jetbrains.kotlin.plugin.serialization") version "2.4.10" apply false
    // KSP (needed for Room) deliberately not added yet — nothing in Phase 1
    // uses Room; it lands in Phase 2-3 alongside the correct KSP/AGP pairing
    // for whatever toolchain versions are current then.
}
