package io.github.sixzman8.codexremote

import org.junit.Assert.assertEquals
import org.junit.Test

class PairingTest {
    private val id = "AbC_d-12345"
    private val token = "q1W2e3R4t5Y6u7I8o9P0_-"

    @Test
    fun readsTheMeterPairingLink() {
        val parsed = Pairing.parse("codexmeter://pair?v=1&id=$id&host=192.168.0.107&port=43130&token=$token")
        assertEquals(PairParse.Ok(PairingInfo(id, "192.168.0.107", 43130, token)), parsed)
    }

    @Test
    fun ignoresUnknownParametersIncludingAnOldName() {
        val parsed = Pairing.parse("codexmeter://pair?v=1&id=$id&name=%EB%85%B8%ED%8A%B8%EB%B6%81&host=10.0.2.2&port=8080&token=$token&extra=1&flag")
        assertEquals(PairParse.Ok(PairingInfo(id, "10.0.2.2", 8080, token)), parsed)
    }

    @Test
    fun decodesPercentEncodedValues() {
        val parsed = Pairing.parse("codexmeter://pair?v=1&id=$id&host=192%2E168%2E0%2E25&port=43130&token=$token")
        assertEquals(PairParse.Ok(PairingInfo(id, "192.168.0.25", 43130, token)), parsed)
    }

    @Test
    fun usesTheDefaultPortWhenTheLinkHasNone() {
        val parsed = Pairing.parse("codexmeter://pair?v=1&id=$id&host=192.168.0.25&token=$token")
        assertEquals(PairParse.Ok(PairingInfo(id, "192.168.0.25", Pairing.DEFAULT_PORT, token)), parsed)
    }

    @Test
    fun asksForAMeterUpdateWhenTheVersionIsNotOne() {
        assertEquals(PairParse.UnsupportedVersion, Pairing.parse("codexmeter://pair?v=2&id=$id&host=192.168.0.25&port=1&token=$token"))
        assertEquals(PairParse.UnsupportedVersion, Pairing.parse("codexmeter://pair?id=$id&host=192.168.0.25&port=1&token=$token"))
    }

    @Test
    fun rejectsOtherCodes() {
        assertEquals(PairParse.NotPairing, Pairing.parse("https://example.com/?v=1"))
        assertEquals(PairParse.NotPairing, Pairing.parse("codexmeter://other?v=1&id=$id"))
        assertEquals(PairParse.NotPairing, Pairing.parse("just some text with spaces"))
    }

    @Test
    fun rejectsMissingOrMalformedValues() {
        val base = "codexmeter://pair?v=1"
        listOf(
            "$base&host=192.168.0.25&port=43130&token=$token",
            "$base&id=$id&host=192.168.0.25&port=43130",
            "$base&id=$id&host=pc.local&port=43130&token=$token",
            "$base&id=$id&host=192.168.0.256&port=43130&token=$token",
            "$base&id=$id&host=192.168.0&port=43130&token=$token",
            "$base&id=$id&host=192.168.0.25&port=0&token=$token",
            "$base&id=$id&host=192.168.0.25&port=65536&token=$token",
            "$base&id=$id&host=192.168.0.25&port=abc&token=$token",
            "$base&id=$id&host=192.168.0.25&port=43130&token=ab%2Fcd%2Fefgh",
            "$base&id=$id&host=192.168.0.25&port=43130&token=short",
        ).forEach { assertEquals(it, PairParse.Invalid, Pairing.parse(it)) }
    }

    @Test
    fun pairsOnlyHomeNetworkAddresses() {
        listOf("10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.0.107", "127.0.0.1").forEach {
            assertEquals(it, PairParse.Ok(PairingInfo(id, it, 43130, token)), Pairing.parse("codexmeter://pair?v=1&id=$id&host=$it&port=43130&token=$token"))
        }
        listOf("8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.0.1", "169.254.1.1", "100.64.0.1", "0.0.0.0", "255.255.255.255").forEach {
            assertEquals(it, PairParse.Invalid, Pairing.parse("codexmeter://pair?v=1&id=$id&host=$it&port=43130&token=$token"))
        }
    }
}
