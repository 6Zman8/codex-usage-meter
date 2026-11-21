package io.github.sixzman8.codexremote

/** Picks the Korean particle that follows a PC name or version, e.g. 데스크톱이 / 노트북을 / 1.1.0이. */
object Korean {
    fun iGa(word: String) = word + particle(word, "이", "가")
    fun eulReul(word: String) = word + particle(word, "을", "를")

    private fun particle(word: String, afterConsonant: String, afterVowel: String) = when (endsWithBatchim(word)) {
        true -> afterConsonant
        false -> afterVowel
        null -> "$afterConsonant($afterVowel)"
    }

    /** Whether the word's spoken ending has a final consonant; null when it cannot tell. */
    private fun endsWithBatchim(word: String): Boolean? {
        val last = word.trimEnd().lastOrNull { it.isLetterOrDigit() } ?: return null
        return when (last) {
            in '가'..'힣' -> (last - '가') % 28 != 0
            // 영 일 삼 육 칠 팔 end in a consonant; 이 사 오 구 do not.
            in '0'..'9' -> last in "013678"
            // Letters as spelled out: 엘 엠 엔 알 end in a consonant.
            in 'a'..'z', in 'A'..'Z' -> last.lowercaseChar() in "lmnr"
            else -> null
        }
    }
}
