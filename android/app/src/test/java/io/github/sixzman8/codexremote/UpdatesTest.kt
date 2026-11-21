package io.github.sixzman8.codexremote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UpdatesTest {
    @get:Rule
    val folder = TemporaryFolder()

    // SHA-256("abc") from FIPS 180-2, appendix B.1.
    private val abcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

    @Test
    fun readsTheReleaseManifest() {
        assertEquals(UpdateInfo(2, "1.1.0", abcHash), Updates.parse("""{"versionCode":2,"versionName":"1.1.0","sha256":"$abcHash"}"""))
        assertEquals(UpdateInfo(3, "1.2.0", abcHash), Updates.parse("""{"versionCode":3,"versionName":"1.2.0","sha256":"${abcHash.uppercase()}","notes":"x"}"""))
    }

    @Test
    fun rejectsIncompleteOrMalformedManifests() {
        listOf(
            "",
            "[]",
            """{"versionName":"1.1.0","sha256":"$abcHash"}""",
            """{"versionCode":"2","versionName":"1.1.0","sha256":"$abcHash"}""",
            """{"versionCode":2.5,"versionName":"1.1.0","sha256":"$abcHash"}""",
            """{"versionCode":0,"versionName":"1.1.0","sha256":"$abcHash"}""",
            """{"versionCode":2,"versionName":" ","sha256":"$abcHash"}""",
            """{"versionCode":2,"versionName":"1.1.0","sha256":"${abcHash.dropLast(1)}"}""",
            """{"versionCode":2,"versionName":"1.1.0","sha256":"${abcHash.dropLast(1)}z"}""",
        ).forEach { assertNull(it, Updates.parse(it)) }
    }

    @Test
    fun onlyAHigherVersionCodeIsAnUpdate() {
        val info = UpdateInfo(5, "1.4.0", abcHash)
        assertTrue(Updates.isNewer(info, 4))
        assertFalse(Updates.isNewer(info, 5))
        assertFalse(Updates.isNewer(info, 6))
    }

    @Test
    fun hashesStreams() {
        assertEquals(abcHash, Updates.sha256Hex("abc".byteInputStream()))
        // SHA-256 of empty input, FIPS 180-2.
        assertEquals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Updates.sha256Hex(ByteArray(0).inputStream()))
    }

    @Test
    fun acceptsADownloadOnlyWhenItsHashMatches() {
        val info = UpdateInfo(2, "1.1.0", abcHash)
        val apk = folder.newFile("CodexMeterRemote.apk")
        apk.writeText("abc")
        assertTrue(Updates.matches(apk, info))
        apk.writeText("abd")
        assertFalse(Updates.matches(apk, info))
        assertFalse(Updates.matches(folder.root.resolve("missing.apk"), info))
    }
}
