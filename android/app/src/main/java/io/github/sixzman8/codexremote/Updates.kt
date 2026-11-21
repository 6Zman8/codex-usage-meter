package io.github.sixzman8.codexremote

import org.json.JSONObject
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

/** `CodexMeterRemote.json` from the latest meter release: `{"versionCode":N,"versionName":"x.y.z","sha256":"<hex>"}`. */
data class UpdateInfo(val versionCode: Int, val versionName: String, val sha256: String)

object Updates {
    const val MANIFEST_FILE = "CodexMeterRemote.json"
    const val APK_FILE = "CodexMeterRemote.apk"
    private val hex64 = Regex("[0-9a-f]{64}")

    /** The release manifest, or null when it is not one; the hash is kept in lowercase hex. */
    fun parse(json: String): UpdateInfo? {
        val o = try {
            JSONObject(json)
        } catch (_: Exception) {
            return null
        }
        val code = o.opt("versionCode") as? Number ?: return null
        if (code.toDouble() != code.toInt().toDouble() || code.toInt() <= 0) return null
        val name = o.optString("versionName").trim().takeIf { it.isNotEmpty() } ?: return null
        val sha = o.optString("sha256").trim().lowercase().takeIf { hex64.matches(it) } ?: return null
        return UpdateInfo(code.toInt(), name, sha)
    }

    fun isNewer(info: UpdateInfo, installedVersionCode: Int): Boolean = info.versionCode > installedVersionCode

    fun sha256Hex(input: InputStream): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(64 * 1024)
        while (true) {
            val read = input.read(buffer)
            if (read < 0) break
            digest.update(buffer, 0, read)
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    /** True only when [file] exists and its SHA-256 equals the release manifest's. */
    fun matches(file: File, info: UpdateInfo): Boolean =
        file.isFile && file.inputStream().use { sha256Hex(it) } == info.sha256
}
