package io.github.sixzman8.codexremote.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp

/** The approved mockup's palette (design/mockups/phone-app/index.html). */
object Palette {
    val Bg = Color(0xFF151515)
    val Card = Color(0xFF1B1B1B)
    val Line = Color(0xFF343434)
    val Text = Color(0xFFF4F4F5)
    val Muted = Color(0xFFA1A1AA)
    val Off = Color(0xFF38BDF8)
    val OffSoft = Color(0x3D38BDF8)
    val Sleep = Color(0xFFFBBF24)
    val SleepSoft = Color(0x3DFBBF24)
    val Good = Color(0xFF8DE0C4)
    val Bad = Color(0xFFFCA5A5)
    val TabSelected = Color(0xFF232325)
    val Button = Color(0xFF262628)
    val OnPrimary = Color(0xFF111111)
    val NoticeBorder = Color(0xFF5A4A1E)
    val NoticeBg = Color(0xFF221D10)
    val NoticeBadBorder = Color(0xFF5B2B2B)
    val NoticeBadBg = Color(0xFF241414)
}

@Composable
fun RemoteTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Palette.Text,
            onPrimary = Palette.OnPrimary,
            background = Palette.Bg,
            onBackground = Palette.Text,
            surface = Palette.Card,
            onSurface = Palette.Text,
            onSurfaceVariant = Palette.Muted,
            surfaceContainer = Palette.Card,
            surfaceContainerHigh = Palette.Card,
            surfaceContainerHighest = Palette.Button,
            outline = Palette.Line,
            error = Palette.Bad,
        ),
        content = content,
    )
}

object Icons {
    val Monitor = strokeIcon("M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-8a2 2 0 0 1 2 -2z M8 20h8 M12 16v4")
    val Moon = strokeIcon("M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z")
    val Menu = strokeIcon("M4 7h16 M4 12h16 M4 17h16")
    val Back = strokeIcon("M15 5l-7 7 7 7")

    // The mockup's 24-unit stroke icons, drawn with a 1.8 stroke and round ends.
    private fun strokeIcon(path: String) = ImageVector.Builder(defaultWidth = 24.dp, defaultHeight = 24.dp, viewportWidth = 24f, viewportHeight = 24f)
        .addPath(
            pathData = PathParser().parsePathString(path).toNodes(),
            fill = null,
            stroke = SolidColor(Color.White),
            strokeLineWidth = 1.8f,
            strokeLineCap = StrokeCap.Round,
            strokeLineJoin = StrokeJoin.Round,
        )
        .build()
}

@Composable
fun TopBar(title: String, leading: (@Composable () -> Unit)? = null, trailing: (@Composable () -> Unit)? = null) {
    Row(
        modifier = Modifier.fillMaxWidth().defaultMinSize(minHeight = 44.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        leading?.invoke()
        Text(
            text = title,
            color = Palette.Text,
            fontSize = 17.sp,
            fontWeight = FontWeight(650),
            modifier = Modifier.weight(1f).padding(start = 4.dp),
        )
        trailing?.invoke()
    }
}

@Composable
fun IconSquareButton(icon: ImageVector, label: String, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .size(44.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(Palette.Card)
            .border(1.dp, Palette.Line, RoundedCornerShape(14.dp))
            .clickable(role = Role.Button, onClickLabel = label, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = label, tint = Palette.Muted, modifier = Modifier.size(22.dp))
    }
}

/** The mockup's `.btn`: 44dp tall, 12dp corners; [primary] is the light filled variant. */
@Composable
fun PlainButton(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, primary: Boolean = false, enabled: Boolean = true) {
    val shape = RoundedCornerShape(12.dp)
    Box(
        modifier = modifier
            .defaultMinSize(minHeight = 44.dp)
            .clip(shape)
            .background(if (primary) Palette.Text else Palette.Button)
            .border(1.dp, if (primary) Palette.Text else Palette.Line, shape)
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = 16.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = text,
            color = (if (primary) Palette.OnPrimary else Palette.Text).copy(alpha = if (enabled) 1f else 0.5f),
            fontSize = 14.5.sp,
            fontWeight = FontWeight.SemiBold,
            textAlign = TextAlign.Center,
        )
    }
}

/** The mockup's `.notice` box: amber for news, red ([bad]) for a problem, with a row of buttons under the text. */
@Composable
fun Notice(text: String, bad: Boolean, buttons: (@Composable RowScope.() -> Unit)? = null) {
    val shape = RoundedCornerShape(16.dp)
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(if (bad) Palette.NoticeBadBg else Palette.NoticeBg)
            .border(1.dp, if (bad) Palette.NoticeBadBorder else Palette.NoticeBorder, shape)
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        Text(text = text, color = Palette.Text, style = BodyStyle)
        if (buttons != null) {
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), content = buttons)
        }
    }
}

@Composable
fun StatusLine(text: String?, tone: io.github.sixzman8.codexremote.Tone, modifier: Modifier = Modifier) {
    Text(
        text = text.orEmpty(),
        color = when (tone) {
            io.github.sixzman8.codexremote.Tone.Good -> Palette.Good
            io.github.sixzman8.codexremote.Tone.Bad -> Palette.Bad
            io.github.sixzman8.codexremote.Tone.Neutral -> Palette.Muted
        },
        style = BodyStyle,
        textAlign = TextAlign.Center,
        modifier = modifier.fillMaxWidth().defaultMinSize(minHeight = 42.dp).padding(horizontal = 4.dp),
    )
}

val BodyStyle = TextStyle(fontSize = 14.sp, lineHeight = 1.45.em)
