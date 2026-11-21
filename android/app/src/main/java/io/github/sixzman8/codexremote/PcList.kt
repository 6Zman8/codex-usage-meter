package io.github.sixzman8.codexremote

import org.json.JSONArray
import org.json.JSONObject

/**
 * A paired PC. [nameIsCustom] is set once the user renames it in the app; from then on the meter's name never replaces it.
 */
data class Pc(
    val id: String,
    val name: String,
    val nameIsCustom: Boolean,
    val host: String,
    val port: Int,
    val token: String,
)

/** Pure edits of the ordered PC list; the view model persists the results. */
object PcList {
    /** Shown until the PC's first successful status answer supplies the meter's name. */
    const val PLACEHOLDER_NAME = "PC"
    const val MAX_NAME_LENGTH = 30

    data class PairResult(val list: List<Pc>, val added: Boolean)

    /** Adds a new PC, or for a known id only refreshes its address, port and token (the name stays). */
    fun pair(list: List<Pc>, info: PairingInfo): PairResult {
        val index = list.indexOfFirst { it.id == info.id }
        if (index < 0) return PairResult(list + Pc(info.id, PLACEHOLDER_NAME, false, info.host, info.port, info.token), true)
        val updated = list.toMutableList()
        updated[index] = list[index].copy(host = info.host, port = info.port, token = info.token)
        return PairResult(updated, false)
    }

    /** The user's own name; blank input leaves the list unchanged. */
    fun rename(list: List<Pc>, id: String, name: String): List<Pc> {
        val clean = name.trim().take(MAX_NAME_LENGTH)
        if (clean.isEmpty()) return list
        return list.map { if (it.id == id) it.copy(name = clean, nameIsCustom = true) else it }
    }

    /** Takes the name a meter reports in its status answer unless the user named the PC in the app. */
    fun applyMeterName(list: List<Pc>, id: String, meterName: String?): List<Pc> {
        val clean = meterName?.trim()?.take(MAX_NAME_LENGTH).orEmpty()
        if (clean.isEmpty()) return list
        return list.map { if (it.id == id && !it.nameIsCustom && it.name != clean) it.copy(name = clean) else it }
    }

    fun move(list: List<Pc>, id: String, delta: Int): List<Pc> {
        val from = list.indexOfFirst { it.id == id }
        val to = from + delta
        if (from < 0 || to !in list.indices) return list
        val updated = list.toMutableList()
        updated.add(to, updated.removeAt(from))
        return updated
    }

    fun remove(list: List<Pc>, id: String): List<Pc> = list.filterNot { it.id == id }

    fun toJson(list: List<Pc>): String = JSONArray().apply {
        list.forEach {
            put(
                JSONObject()
                    .put("id", it.id).put("name", it.name).put("nameIsCustom", it.nameIsCustom)
                    .put("host", it.host).put("port", it.port).put("token", it.token)
            )
        }
    }.toString()

    /** Reads what [toJson] wrote; unreadable entries are skipped rather than losing the whole list. */
    fun fromJson(text: String?): List<Pc> {
        if (text.isNullOrBlank()) return emptyList()
        val array = try {
            JSONArray(text)
        } catch (_: Exception) {
            return emptyList()
        }
        return (0 until array.length()).mapNotNull { i ->
            val o = array.optJSONObject(i) ?: return@mapNotNull null
            val id = o.optString("id")
            val host = o.optString("host")
            val token = o.optString("token")
            val port = o.optInt("port", 0)
            if (id.isEmpty() || host.isEmpty() || token.isEmpty() || port !in 1..65535) return@mapNotNull null
            Pc(id, o.optString("name").ifBlank { PLACEHOLDER_NAME }, o.optBoolean("nameIsCustom"), host, port, token)
        }.distinctBy { it.id }
    }
}
