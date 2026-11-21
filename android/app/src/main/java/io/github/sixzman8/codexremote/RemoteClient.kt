package io.github.sixzman8.codexremote

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL

enum class RemoteAction(val path: String) { ScreenOff("screen-off"), Sleep("sleep") }

sealed interface RemoteResult {
    /** `/status` answered ok; [id] and [name] are the meter's. Actions answer with empty values. */
    data class Ok(val id: String, val name: String) : RemoteResult
    /** The meter answered with an HTTP error, e.g. 404 after its link was renewed. */
    data class Refused(val code: Int) : RemoteResult
    /** No answer: PC off or asleep, another network, or the meter's remote is off. */
    data object Unreachable : RemoteResult
}

/** The meter's phone API (docs/PHONE_APP.md): `GET /r/<token>/status`, `POST /r/<token>/screen-off|sleep`. */
object RemoteClient {
    private const val STATUS_TIMEOUT_MS = 2500
    private const val ACTION_TIMEOUT_MS = 6000
    private const val MAX_BODY = 16 * 1024

    suspend fun status(pc: Pc): RemoteResult = call(pc, "status", "GET", STATUS_TIMEOUT_MS)

    suspend fun send(pc: Pc, action: RemoteAction): RemoteResult = call(pc, action.path, "POST", ACTION_TIMEOUT_MS)

    private suspend fun call(pc: Pc, resource: String, method: String, timeoutMs: Int): RemoteResult = withContext(Dispatchers.IO) {
        val connection = try {
            URL("http", pc.host, pc.port, "/r/${pc.token}/$resource").openConnection() as HttpURLConnection
        } catch (_: IOException) {
            return@withContext RemoteResult.Unreachable
        }
        try {
            connection.requestMethod = method
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.useCaches = false
            connection.instanceFollowRedirects = false
            connection.setRequestProperty("X-Codex-Remote", "1")
            // The meter answers one request per connection.
            connection.setRequestProperty("Connection", "close")
            if (method == "POST") {
                // The meter rejects request bodies; an explicit zero length keeps the POST body-less.
                connection.doOutput = true
                connection.setFixedLengthStreamingMode(0)
                connection.outputStream.close()
            }
            val code = connection.responseCode
            if (code != HttpURLConnection.HTTP_OK) return@withContext RemoteResult.Refused(code)
            val body = connection.inputStream.use { readLimited(it) }
            val json = try {
                JSONObject(body)
            } catch (_: Exception) {
                return@withContext RemoteResult.Refused(code)
            }
            if (json.optString("status") != "ok") return@withContext RemoteResult.Refused(code)
            RemoteResult.Ok(json.optString("id"), json.optString("name"))
        } catch (_: IOException) {
            RemoteResult.Unreachable
        } catch (_: SecurityException) {
            // Raised instead of a timeout on some builds when local network access is not allowed.
            RemoteResult.Unreachable
        } finally {
            connection.disconnect()
        }
    }

    private fun readLimited(input: InputStream): String {
        val bytes = input.readNBytesCompat(MAX_BODY)
        return bytes.toString(Charsets.UTF_8)
    }

    private fun InputStream.readNBytesCompat(limit: Int): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        val buffer = ByteArray(4096)
        while (out.size() < limit) {
            val read = read(buffer, 0, minOf(buffer.size, limit - out.size()))
            if (read < 0) break
            out.write(buffer, 0, read)
        }
        return out.toByteArray()
    }
}

/** HTTPS downloads of the release manifest and APK. */
object UpdateClient {
    private const val TIMEOUT_MS = 15000
    private const val MAX_APK_BYTES = 200L * 1024 * 1024

    suspend fun fetchManifest(baseUrl: String): UpdateInfo? = withContext(Dispatchers.IO) {
        val connection = open(baseUrl + Updates.MANIFEST_FILE)
        try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) return@withContext null
            Updates.parse(connection.inputStream.use { it.readBytes().toString(Charsets.UTF_8) })
        } finally {
            connection.disconnect()
        }
    }

    /** Streams the APK to [target], reporting 0–100; throws IOException on any failure. */
    suspend fun download(baseUrl: String, target: File, progress: (Int) -> Unit) = withContext(Dispatchers.IO) {
        val connection = open(baseUrl + Updates.APK_FILE)
        try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) throw IOException("HTTP ${connection.responseCode}")
            val total = connection.contentLengthLong
            target.parentFile?.mkdirs()
            connection.inputStream.use { input ->
                target.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    var shown = -1
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        done += read
                        if (done > MAX_APK_BYTES) throw IOException("too large")
                        output.write(buffer, 0, read)
                        if (total > 0) {
                            val percent = (done * 100 / total).toInt().coerceIn(0, 100)
                            if (percent != shown) {
                                shown = percent
                                progress(percent)
                            }
                        }
                    }
                }
            }
        } finally {
            connection.disconnect()
        }
    }

    private fun open(url: String): HttpURLConnection = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = TIMEOUT_MS
        readTimeout = TIMEOUT_MS
        useCaches = false
        instanceFollowRedirects = true
    }
}
