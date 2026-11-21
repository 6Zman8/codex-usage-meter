package io.github.sixzman8.codexremote.ui

import android.content.Context
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.MotionDurationScale
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

private const val HOLD_MS = 1200
private const val DRAIN_MS = HOLD_MS / 4
private const val RESET_AFTER_SEND_MS = 1500L

private enum class Phase { Idle, Holding, Draining, Sending }

/** The 1.2 s hold is a safety delay, so it ignores "remove animations" / animator duration scale (0 would fire at once). */
private object RealTime : MotionDurationScale {
    override val scaleFactor = 1f
}

/**
 * The big hold-to-run button of the old Chrome remote: holding fills the gauge from the bottom over 1.2 s,
 * letting go (or sliding off) drains it, and a full gauge vibrates and runs [onFire].
 */
@Composable
fun HoldButton(
    icon: ImageVector,
    title: String,
    tone: Color,
    soft: Color,
    enabled: Boolean,
    onFire: suspend () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val progress = remember { Animatable(0f) }
    var phase by remember { mutableStateOf(Phase.Idle) }
    var active by remember { mutableStateOf(false) }
    val currentEnabled by rememberUpdatedState(enabled)
    val currentOnFire by rememberUpdatedState(onFire)
    val shape = RoundedCornerShape(24.dp)
    val hint = when {
        !enabled && phase != Phase.Sending -> "연결되면 누를 수 있습니다"
        phase == Phase.Holding -> "계속 누르세요"
        phase == Phase.Sending -> "보내는 중…"
        else -> "꾹 누르기"
    }

    Column(
        modifier = modifier
            .clip(shape)
            .background(Palette.Card)
            .drawBehind {
                val p = progress.value
                if (p > 0f) {
                    val top = size.height * (1f - p)
                    drawRect(soft, topLeft = Offset(0f, top), size = Size(size.width, size.height - top))
                    drawRect(tone, topLeft = Offset(0f, top), size = Size(size.width, minOf(3.dp.toPx(), size.height - top)))
                }
                val stroke = 1.dp.toPx()
                val color = if (active) tone else Palette.Line
                val radius = 24.dp.toPx() - stroke / 2
                drawRoundRect(
                    color = color,
                    topLeft = Offset(stroke / 2, stroke / 2),
                    size = Size(size.width - stroke, size.height - stroke),
                    cornerRadius = CornerRadius(radius, radius),
                    style = Stroke(
                        width = stroke,
                        pathEffect = if (enabled || active) null else PathEffect.dashPathEffect(floatArrayOf(3.dp.toPx(), 3.dp.toPx())),
                    ),
                )
            }
            .semantics {
                role = Role.Button
                contentDescription = title
                stateDescription = hint
            }
            .pointerInput(Unit) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    if (!currentEnabled || (phase != Phase.Idle && phase != Phase.Draining)) return@awaitEachGesture
                    down.consume()
                    phase = Phase.Holding
                    active = true
                    val holdJob = scope.launch(RealTime) {
                        val from = progress.value
                        progress.animateTo(1f, tween(((1f - from) * HOLD_MS).roundToInt(), easing = LinearEasing))
                        phase = Phase.Sending
                        vibrate(context)
                        try {
                            currentOnFire()
                        } finally {
                            delay(RESET_AFTER_SEND_MS)
                            progress.snapTo(0f)
                            phase = Phase.Idle
                            active = false
                        }
                    }
                    while (true) {
                        val event = awaitPointerEvent()
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) break
                        val p = change.position
                        if (p.x < 0 || p.y < 0 || p.x > size.width || p.y > size.height) break
                        change.consume()
                    }
                    if (phase == Phase.Holding) {
                        holdJob.cancel()
                        phase = Phase.Draining
                        scope.launch(RealTime) {
                            val from = progress.value
                            progress.animateTo(0f, tween((from * DRAIN_MS).roundToInt(), easing = LinearEasing))
                            if (phase == Phase.Draining) {
                                phase = Phase.Idle
                                active = false
                            }
                        }
                    }
                }
            },
        verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        val dim = if (enabled || phase == Phase.Sending) 1f else 0.35f
        Icon(icon, contentDescription = null, tint = tone, modifier = Modifier.size(58.dp).alpha(dim))
        Text(title, color = Palette.Text, fontSize = 30.sp, fontWeight = FontWeight.Bold, modifier = Modifier.alpha(dim))
        Text(hint, color = if (active) Palette.Text else Palette.Muted, fontSize = 15.sp)
    }
}

private fun vibrate(context: Context) {
    val vibrator = if (Build.VERSION.SDK_INT >= 31) {
        context.getSystemService(VibratorManager::class.java)?.defaultVibrator
    } else {
        @Suppress("DEPRECATION")
        context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
    }
    vibrator?.vibrate(VibrationEffect.createOneShot(40, VibrationEffect.DEFAULT_AMPLITUDE))
}
