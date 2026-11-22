import java.security.MessageDigest
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

val appVersion = Properties().apply { rootProject.file("version.properties").inputStream().use { load(it) } }
val appVersionCode = appVersion.getProperty("versionCode").trim().toInt()
val appVersionName = appVersion.getProperty("versionName").trim()

// Release APKs are signed only with the key from these variables; without them the release build stops (see checkReleaseSigning).
val signingVariables = listOf("ANDROID_KEYSTORE_PATH", "ANDROID_KEYSTORE_PASSWORD", "ANDROID_KEY_ALIAS", "ANDROID_KEY_PASSWORD")
val signingValues = signingVariables.associateWith { providers.environmentVariable(it).orNull?.takeIf(String::isNotBlank) }
val releaseKeystore = signingValues["ANDROID_KEYSTORE_PATH"]?.let { file(it) }

android {
    namespace = "io.github.sixzman8.codexremote"
    // Compose 1.13 needs the Android 37.1+ APIs at compile time.
    compileSdk {
        version = release(37) {
            minorApiLevel = 2
        }
    }

    defaultConfig {
        applicationId = "io.github.sixzman8.codexremote"
        minSdk = 26
        targetSdk = 37
        versionCode = appVersionCode
        versionName = appVersionName
        buildConfigField("String", "UPDATE_BASE_URL", "\"https://github.com/6Zman8/codex-usage-meter/releases/latest/download/\"")
    }

    signingConfigs {
        create("release") {
            if (signingValues.values.all { it != null }) {
                storeFile = releaseKeystore
                storePassword = signingValues["ANDROID_KEYSTORE_PASSWORD"]
                keyAlias = signingValues["ANDROID_KEY_ALIAS"]
                keyPassword = signingValues["ANDROID_KEY_PASSWORD"]
            }
        }
    }

    buildTypes {
        debug {
            // Lets the emulator check serve the update JSON and APK from a local stub server.
            providers.gradleProperty("updateBaseUrl").orNull?.let { buildConfigField("String", "UPDATE_BASE_URL", "\"$it\"") }
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.getByName("release")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

base {
    archivesName.set("CodexMeterRemote")
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2026.11.01")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.11.1")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.11.1")
    implementation("androidx.core:core-ktx:1.19.1")
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
    // Play services still pull fragment 1.0.0, whose activity-result handling is broken; lint stops release builds on it.
    implementation("androidx.fragment:fragment:1.9.1")

    testImplementation("junit:junit:4.13.2")
    // The JVM unit tests need a real org.json; Android's copy only exists on devices.
    testImplementation("org.json:json:20260814")
}

val checkReleaseSigning = tasks.register("checkReleaseSigning") {
    description = "Stops a release build that has no signing key, so no unsigned or debug-signed release APK is produced."
    val missing = signingValues.filterValues { it == null }.keys.toList()
    val keystore = releaseKeystore
    doLast {
        if (missing.isNotEmpty()) {
            throw GradleException(
                "Release signing is not configured. Set the environment variables ${missing.joinToString()} " +
                    "(keystore file path, keystore password, key alias, key password) and build again. " +
                    "Debug builds (assembleDebug) need no key."
            )
        }
        if (keystore == null || !keystore.isFile) throw GradleException("ANDROID_KEYSTORE_PATH does not point to a keystore file: $keystore")
    }
}

tasks.matching { it.name in setOf("validateSigningRelease", "packageRelease", "packageReleaseBundle", "signReleaseBundle") }
    .configureEach { dependsOn(checkReleaseSigning) }

/**
 * Whether a class's block of the R8 mapping keeps a real `<init>()`. R8 lists an inlined frame right before the
 * method it was inlined into, with the same minified range and name, and notes a changed signature on the comment
 * line after a method; neither counts as the constructor reflection calls.
 */
fun hasNoArgConstructor(block: List<String>): Boolean {
    val member = Regex("""^\s+(?:(\d+:\d+):)?(.+) -> (\S+)$""")
    val members = block.withIndex().filter { !it.value.trimStart().startsWith("#") }
    return members.withIndex().any { (position, line) ->
        val match = member.matchEntire(line.value) ?: return@any false
        val (range, original, minified) = match.destructured
        if (minified != "<init>" || !Regex("""void <init>\(\)(?::\d+)*""").matches(original)) return@any false
        val comment = block.getOrNull(line.index + 1)?.trimStart()
        if (comment != null && comment.startsWith("#") && "residualsignature" in comment && "\"()V\"" !in comment) return@any false
        val next = members.getOrNull(position + 1)?.let { member.matchEntire(it.value) }
        !(range.isNotEmpty() && next != null && next.groupValues[1] == range && next.groupValues[3] == minified)
    }
}

// The two files every meter release uploads: the signed APK and the JSON the app reads to update (docs/PHONE_APP.md).
tasks.register("releaseAssets") {
    description = "Builds the signed release APK and writes CodexMeterRemote.apk and CodexMeterRemote.json to build/release-assets."
    dependsOn("assembleRelease")
    val apkDir = layout.buildDirectory.dir("outputs/apk/release")
    val outDir = layout.buildDirectory.dir("release-assets")
    val manifest = layout.buildDirectory.file("intermediates/packaged_manifests/release/processReleaseManifestForPackage/AndroidManifest.xml")
    val mapping = layout.buildDirectory.file("outputs/mapping/release/mapping.txt")
    val code = appVersionCode
    val name = appVersionName
    inputs.dir(apkDir)
    outputs.dir(outDir)
    doLast {
        // Every component registrar the manifest names is created by reflection; one without its no-argument
        // constructor in the shrunk app crashes the QR scanner (v1.0.0), so such a build is never published.
        val manifestFile = manifest.get().asFile
        val mappingFile = mapping.get().asFile
        if (!manifestFile.isFile || !mappingFile.isFile) throw GradleException("Merged manifest or R8 mapping not found: $manifestFile, $mappingFile")
        val registrars = Regex("""com\.google\.firebase\.components:([\w.$]+)""").findAll(manifestFile.readText()).map { it.groupValues[1] }.toSet()
        if (registrars.isEmpty()) throw GradleException("No component registrars found in $manifestFile")
        val lines = mappingFile.readLines()
        val missing = registrars.filter { registrar ->
            val start = lines.indexOfFirst { it.startsWith("$registrar -> $registrar:") }
            start < 0 || !hasNoArgConstructor(lines.drop(start + 1).takeWhile { it.startsWith(" ") || it.startsWith("#") })
        }
        if (missing.isNotEmpty()) throw GradleException("R8 removed or renamed the no-argument constructor of $missing; keep it in proguard-rules.pro")
        val apk = apkDir.get().asFile.listFiles { f -> f.extension == "apk" }!!.single()
        val target = outDir.get().file("CodexMeterRemote.apk").asFile
        target.parentFile.mkdirs()
        apk.copyTo(target, overwrite = true)
        val sha = MessageDigest.getInstance("SHA-256").digest(target.readBytes()).joinToString("") { "%02x".format(it) }
        outDir.get().file("CodexMeterRemote.json").asFile
            .writeText("{\"versionCode\":$code,\"versionName\":\"$name\",\"sha256\":\"$sha\"}\n")
    }
}
