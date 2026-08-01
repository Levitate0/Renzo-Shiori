import java.util.Properties
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

// Release signing: keystore details live in an untracked key.properties
// (keystoreFile / keystorePassword / keyAlias / keyPassword). Without it the
// release build falls back to the debug key so CI/other forks still build.
val keyProps = Properties().apply {
    val f = rootProject.file("key.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

android {
    namespace = "app.renzoshiori.client"
    compileSdk = 35

    defaultConfig {
        applicationId = "app.renzoshiori.client"
        minSdk = 24
        targetSdk = 35
        versionCode = 14
        versionName = "1.3.1"
    }

    signingConfigs {
        if (keyProps.isNotEmpty()) {
            create("release") {
                storeFile = file(keyProps.getProperty("keystoreFile"))
                storePassword = keyProps.getProperty("keystorePassword")
                keyAlias = keyProps.getProperty("keyAlias")
                keyPassword = keyProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (keyProps.isNotEmpty())
                signingConfigs.getByName("release")
            else
                signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
        compose = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    // androidx.webkit / documentfile stay: SAF (Storage Access Framework) folder
    // picking for offline downloads is unchanged by the native-UI rewrite, and
    // webkit is still used transiently for the "open in browser" fallback link
    // on error screens. swiperefreshlayout and appcompat are WebView-shell-era
    // and are dropped — pull-to-refresh and app chrome are native Compose now.
    implementation("androidx.documentfile:documentfile:1.0.1")

    // Compose
    implementation(platform("androidx.compose:compose-bom:2026.06.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    // Not in the newer Compose BOMs — pinned explicitly.
    implementation("androidx.compose.material:material-icons-extended:1.7.8")
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.9.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.0")
    debugImplementation("androidx.compose.ui:ui-tooling")

    // Navigation — staying on 2.x (NavHost/NavController) rather than the very
    // new Navigation3 1.0.1: 2.x is the well-established, widely-documented API.
    implementation("androidx.navigation:navigation-compose:2.9.8")

    // Networking: Retrofit + OkHttp against RenzoBackend's REST API. JWT Bearer
    // auth via an interceptor (added once the auth module lands), kotlinx.serialization
    // for JSON (matches the backend's camelCase DTOs directly, no custom naming needed).
    implementation("com.squareup.retrofit2:retrofit:3.0.0")
    implementation("com.squareup.retrofit2:converter-kotlinx-serialization:3.0.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.squareup.okhttp3:logging-interceptor:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.0")

    // Image loading (page images, covers/thumbnails) — same Bearer interceptor
    // as the REST client attaches to every request. Held at 3.1.0: Coil 3.2+
    // compiles against SDK 36, which would force AGP 8.8+/Gradle 8.9+ — the
    // release build machine has Gradle 8.7 (see BUILD.md), and the whole
    // toolchain shouldn't escalate over an image library.
    implementation("io.coil-kt.coil3:coil-compose:3.1.0")
    implementation("io.coil-kt.coil3:coil-network-okhttp:3.1.0")

    // Room (offline-download manifest) lands in Phase 2-3, not here — nothing
    // in Phase 1 persists anything beyond the encrypted token store below.

    // Encrypted token storage (replaces the old plaintext SharedPreferences
    // bearer-token handling in the WebView bridge).
    implementation("androidx.security:security-crypto:1.1.0")
}
