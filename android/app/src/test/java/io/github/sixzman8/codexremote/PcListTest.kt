package io.github.sixzman8.codexremote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PcListTest {
    private val desktop = PairingInfo("desktop0001", "192.168.0.107", 43130, "tokenDesktop0000000001")
    private val laptop = PairingInfo("laptop00001", "192.168.0.25", 43130, "tokenLaptop00000000001")

    private fun twoPcs(): List<Pc> = PcList.pair(PcList.pair(emptyList(), desktop).list, laptop).list

    @Test
    fun aNewPcStartsWithThePlaceholderName() {
        val result = PcList.pair(emptyList(), desktop)
        assertTrue(result.added)
        assertEquals(listOf(Pc(desktop.id, "PC", false, desktop.host, desktop.port, desktop.token)), result.list)
    }

    @Test
    fun rescanningAKnownPcUpdatesItsAddressAndKeepsItsNameAndPlace() {
        val renamed = PcList.rename(twoPcs(), desktop.id, "거실 PC")
        val moved = PairingInfo(desktop.id, "192.168.0.200", 50000, "tokenRenewed0000000001")
        val result = PcList.pair(renamed, moved)
        assertFalse(result.added)
        assertEquals(2, result.list.size)
        assertEquals(Pc(desktop.id, "거실 PC", true, "192.168.0.200", 50000, "tokenRenewed0000000001"), result.list[0])
        assertEquals(renamed[1], result.list[1])
    }

    @Test
    fun theMeterNameFillsThePlaceholderAndFollowsLaterMeterRenames() {
        var list = PcList.applyMeterName(twoPcs(), desktop.id, " 데스크톱 ")
        assertEquals("데스크톱", list[0].name)
        assertFalse(list[0].nameIsCustom)
        list = PcList.applyMeterName(list, desktop.id, "DESKTOP-NEW")
        assertEquals("DESKTOP-NEW", list[0].name)
        assertEquals("PC", list[1].name)
    }

    @Test
    fun theMeterNameNeverReplacesTheUsersName() {
        val list = PcList.rename(twoPcs(), laptop.id, "노트북")
        assertEquals(list, PcList.applyMeterName(list, laptop.id, "LAPTOP-123"))
    }

    @Test
    fun aBlankMeterNameChangesNothing() {
        val list = twoPcs()
        assertEquals(list, PcList.applyMeterName(list, desktop.id, "  "))
        assertEquals(list, PcList.applyMeterName(list, desktop.id, null))
    }

    @Test
    fun renameTrimsAndIgnoresBlankNames() {
        val list = PcList.rename(twoPcs(), desktop.id, "  데스크톱  ")
        assertEquals(Pc(desktop.id, "데스크톱", true, desktop.host, desktop.port, desktop.token), list[0])
        assertEquals(list, PcList.rename(list, desktop.id, "   "))
        assertEquals(PcList.MAX_NAME_LENGTH, PcList.rename(list, desktop.id, "가".repeat(50))[0].name.length)
    }

    @Test
    fun moveAndRemoveKeepTheRestInOrder() {
        val list = twoPcs()
        assertEquals(listOf(laptop.id, desktop.id), PcList.move(list, laptop.id, -1).map { it.id })
        assertEquals(listOf(laptop.id, desktop.id), PcList.move(list, desktop.id, 1).map { it.id })
        assertEquals(list, PcList.move(list, desktop.id, -1))
        assertEquals(list, PcList.move(list, laptop.id, 1))
        assertEquals(listOf(laptop.id), PcList.remove(list, desktop.id).map { it.id })
    }

    @Test
    fun theSavedListReadsBackUnchanged() {
        val list = PcList.rename(twoPcs(), laptop.id, "노트북 \"작업용\"")
        assertEquals(list, PcList.fromJson(PcList.toJson(list)))
    }

    @Test
    fun unreadableSavedDataGivesAnEmptyListAndBadEntriesAreSkipped() {
        assertEquals(emptyList<Pc>(), PcList.fromJson("not json"))
        assertEquals(emptyList<Pc>(), PcList.fromJson(null))
        val mixed = """[{"id":"a","name":"A","host":"10.0.0.1","port":43130,"token":"tokenAAAA"},{"id":"","host":"x","port":1,"token":"t"},{"id":"b","host":"10.0.0.2","port":0,"token":"tokenBBBB"}]"""
        assertEquals(listOf(Pc("a", "A", false, "10.0.0.1", 43130, "tokenAAAA")), PcList.fromJson(mixed))
    }
}
