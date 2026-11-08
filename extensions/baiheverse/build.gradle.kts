// Compiles the extension against the sidecar's own jar, which carries the
// extension API, OkHttp, kotlinx.serialization and the Android stubs. Those
// are provided at runtime, so nothing is bundled. build-apk.sh turns the jar
// into a sideloadable APK.
plugins {
    kotlin("jvm") version "2.3.21"
}

repositories { mavenCentral() }

dependencies {
    compileOnly(files("../../RenzoBackend/sidecar/AndroidCompat-1.0-all.jar"))
}

kotlin {
    jvmToolchain(21)
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11)
        freeCompilerArgs.add("-Xno-param-assertions")
    }
}

java { targetCompatibility = JavaVersion.VERSION_11 }
