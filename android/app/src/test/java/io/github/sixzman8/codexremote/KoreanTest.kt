package io.github.sixzman8.codexremote

import org.junit.Assert.assertEquals
import org.junit.Test

class KoreanTest {
    @Test
    fun picksTheParticleFromTheSpokenEnding() {
        assertEquals("데스크톱이", Korean.iGa("데스크톱"))
        assertEquals("거실 PC가", Korean.iGa("거실 PC"))
        assertEquals("1.1.0이", Korean.iGa("1.1.0"))
        assertEquals("1.1.2가", Korean.iGa("1.1.2"))
        assertEquals("노트북을", Korean.eulReul("노트북"))
        assertEquals("서재를", Korean.eulReul("서재"))
        assertEquals("★이(가)", Korean.iGa("★"))
    }
}
