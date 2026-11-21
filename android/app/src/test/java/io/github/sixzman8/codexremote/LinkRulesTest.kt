package io.github.sixzman8.codexremote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LinkRulesTest {
    private val id = "desktop0001"

    @Test
    fun aConnectedPcStaysConnectedThroughOneMissedPollButNotTwo() {
        val first = LinkRules.afterStatus(id, RemoteResult.Unreachable, Link.Online, fromPoll = true, missedBefore = false)
        assertNull(first)
        val second = LinkRules.afterStatus(id, RemoteResult.Unreachable, Link.Online, fromPoll = true, missedBefore = true)
        assertEquals(Link.Offline, second)
        // 다시 확인 (not a poll) and PCs that were not connected report the miss at once.
        assertEquals(Link.Offline, LinkRules.afterStatus(id, RemoteResult.Unreachable, Link.Online, fromPoll = false, missedBefore = false))
        assertEquals(Link.Offline, LinkRules.afterStatus(id, RemoteResult.Unreachable, Link.Checking, fromPoll = true, missedBefore = false))
        assertEquals(Link.LinkChanged, LinkRules.afterStatus(id, RemoteResult.Refused(404), Link.Online, fromPoll = true, missedBefore = false))
        assertEquals(Link.Online, LinkRules.afterStatus(id, RemoteResult.Ok(id, "데스크톱"), Link.Offline, fromPoll = true, missedBefore = true))
    }
}
