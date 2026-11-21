package io.github.sixzman8.codexremote

import java.net.URI
import java.net.URISyntaxException
import java.net.URLDecoder

/** What the meter's pairing QR code carries: `codexmeter://pair?v=1&id=&host=&port=&token=` (docs/PHONE_APP.md). */
data class PairingInfo(val id: String, val host: String, val port: Int, val token: String)

sealed interface PairParse {
    data class Ok(val info: PairingInfo) : PairParse
    /** Not a meter pairing link at all, e.g. another QR code. */
    data object NotPairing : PairParse
    /** A pairing link of a protocol version this app does not speak; the meter needs updating. */
    data object UnsupportedVersion : PairParse
    /** A pairing link with a missing or malformed value. */
    data object Invalid : PairParse
}

object Pairing {
    const val DEFAULT_PORT = 43130
    private val base64Url = Regex("[A-Za-z0-9_-]+")

    fun parse(text: String): PairParse {
        val uri = try {
            URI(text.trim())
        } catch (_: URISyntaxException) {
            return PairParse.NotPairing
        }
        if (!uri.scheme.equals("codexmeter", ignoreCase = true) || !uri.host.equals("pair", ignoreCase = true)) return PairParse.NotPairing
        val query = query(uri.rawQuery ?: "") ?: return PairParse.Invalid
        if (query["v"] != "1") return PairParse.UnsupportedVersion
        val id = query["id"]?.takeIf { it.length in 1..64 && base64Url.matches(it) } ?: return PairParse.Invalid
        val token = query["token"]?.takeIf { it.length in 8..128 && base64Url.matches(it) } ?: return PairParse.Invalid
        // The app allows plain HTTP only because the meter sits on the home network, so only those addresses pair.
        val host = query["host"]?.let(::ipv4)?.takeIf(::isHomeNetwork) ?: return PairParse.Invalid
        val port = query["port"]?.let { it.toIntOrNull()?.takeIf { p -> p in 1..65535 } ?: return PairParse.Invalid } ?: DEFAULT_PORT
        return PairParse.Ok(PairingInfo(id, host, port, token))
    }

    // First value wins; unknown parameters are kept but never read, so newer meters may add fields.
    private fun query(raw: String): Map<String, String>? {
        val values = LinkedHashMap<String, String>()
        for (part in raw.split('&')) {
            if (part.isEmpty()) continue
            val eq = part.indexOf('=')
            val key = if (eq < 0) part else part.substring(0, eq)
            val value = if (eq < 0) "" else part.substring(eq + 1)
            val decoded = try {
                URLDecoder.decode(value, "UTF-8")
            } catch (_: IllegalArgumentException) {
                return null
            }
            values.putIfAbsent(key, decoded)
        }
        return values
    }

    /** The canonical dotted form of an IPv4 literal, or null when [text] is not one. */
    fun ipv4(text: String): String? {
        val parts = text.split('.')
        if (parts.size != 4) return null
        val bytes = parts.map { part ->
            if (part.isEmpty() || part.length > 3 || !part.all { it in '0'..'9' }) return null
            part.toInt().takeIf { it <= 255 } ?: return null
        }
        return bytes.joinToString(".")
    }

    /** The private ranges the meter puts in its QR code (10/8, 172.16/12, 192.168/16), plus loopback for tests. */
    fun isHomeNetwork(ipv4: String): Boolean {
        val (a, b) = ipv4.split('.').map { it.toInt() }
        return a == 10 || (a == 172 && b in 16..31) || (a == 192 && b == 168) || a == 127
    }
}
