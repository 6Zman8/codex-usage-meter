package io.github.sixzman8.codexremote.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.onLongClick
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.sixzman8.codexremote.AppViewModel
import io.github.sixzman8.codexremote.BuildConfig
import io.github.sixzman8.codexremote.Korean
import io.github.sixzman8.codexremote.Link
import io.github.sixzman8.codexremote.Message
import io.github.sixzman8.codexremote.Pc
import io.github.sixzman8.codexremote.PcList
import io.github.sixzman8.codexremote.RemoteAction
import io.github.sixzman8.codexremote.Tone
import io.github.sixzman8.codexremote.UpdateState

/** What only the activity can do: the QR scanner, the nearby devices permission, and the system installer. */
class AppActions(
    val scanQr: () -> Unit,
    val requestLocalNetwork: () -> Unit,
    val openAppSettings: () -> Unit,
    val install: () -> Unit,
)

private enum class Screen { Main, Manage }

@Composable
fun RemoteApp(vm: AppViewModel, actions: AppActions) {
    val pcs by vm.pcs.collectAsStateWithLifecycle()
    val selectedId by vm.selectedId.collectAsStateWithLifecycle()
    val links by vm.links.collectAsStateWithLifecycle()
    val rechecking by vm.rechecking.collectAsStateWithLifecycle()
    val message by vm.message.collectAsStateWithLifecycle()
    val update by vm.update.collectAsStateWithLifecycle()
    val updateDismissed by vm.updateDismissed.collectAsStateWithLifecycle()
    val localNetworkAllowed by vm.localNetworkAllowed.collectAsStateWithLifecycle()
    var screen by rememberSaveable { mutableStateOf(Screen.Main) }

    BackHandler(enabled = screen == Screen.Manage) {
        screen = Screen.Main
        vm.clearMessage()
    }

    val banner: @Composable () -> Unit = {
        UpdateBanner(update, updateDismissed, onUpdate = vm::downloadUpdate, onLater = vm::dismissUpdate, onInstall = actions.install)
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Palette.Bg)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(start = 14.dp, end = 14.dp, top = 18.dp, bottom = 16.dp),
    ) {
        val selected = pcs.firstOrNull { it.id == selectedId } ?: pcs.firstOrNull()
        when {
            screen == Screen.Manage -> ManageScreen(
                pcs = pcs,
                links = links,
                localNetworkAllowed = localNetworkAllowed,
                message = message,
                update = update,
                onBack = {
                    screen = Screen.Main
                    vm.clearMessage()
                },
                onScan = actions.scanQr,
                onRename = vm::rename,
                onMove = vm::move,
                onDelete = vm::remove,
                onCheckUpdate = { vm.checkForUpdate(manual = true) },
                onDownloadUpdate = vm::downloadUpdate,
                onInstall = actions.install,
            )
            selected == null -> EmptyScreen(message = message, banner = banner, onScan = actions.scanQr)
            else -> MainScreen(
                pcs = pcs,
                selected = selected,
                links = links,
                rechecking = selected.id in rechecking,
                localNetworkAllowed = localNetworkAllowed,
                message = message,
                banner = banner,
                onSelect = vm::select,
                onManage = {
                    screen = Screen.Manage
                    vm.clearMessage()
                },
                onRecheck = vm::recheck,
                onScan = actions.scanQr,
                onAllow = actions.requestLocalNetwork,
                onOpenSettings = actions.openAppSettings,
                onSend = vm::send,
            )
        }
    }
}

// ---- A · first run ----

@Composable
private fun EmptyScreen(message: Message?, banner: @Composable () -> Unit, onScan: () -> Unit) {
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        TopBar("PC 리모컨")
        banner()
        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = maxHeight)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 8.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp, Alignment.CenterVertically),
            ) {
                Text("아직 등록한 PC가 없습니다", color = Palette.Text, fontSize = 22.sp, fontWeight = FontWeight.Bold, lineHeight = 1.35.em)
                Column {
                    Step(1, "PC 미터기에서 ", "⚙ 설정 → 폰 리모컨", "을 켭니다.")
                    Step(2, "아래 버튼을 누르고 PC 화면의 ", "PC 등록 QR", "을 찍습니다.")
                }
                PlainButton("QR 찍어서 PC 추가", onScan, Modifier.fillMaxWidth(), primary = true)
                Text(
                    "처음 한 번 '근처 기기(같은 와이파이) 접근'을 물으면 허용을 눌러 주세요.",
                    color = Palette.Muted,
                    fontSize = 13.sp,
                    lineHeight = 1.5.em,
                    modifier = Modifier.padding(start = 4.dp, end = 4.dp, top = 2.dp),
                )
                if (message != null) StatusLine(message.text, message.tone)
            }
        }
    }
}

@Composable
private fun Step(number: Int, before: String, bold: String, after: String) {
    Row {
        Text("$number.", color = Palette.Muted, fontSize = 14.5.sp, lineHeight = 1.6.em, modifier = Modifier.width(20.dp))
        Text(
            buildAnnotatedString {
                append(before)
                withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(bold) }
                append(after)
            },
            color = Palette.Muted,
            fontSize = 14.5.sp,
            lineHeight = 1.6.em,
        )
    }
}

// ---- B, C, D · the remote ----

@Composable
private fun MainScreen(
    pcs: List<Pc>,
    selected: Pc,
    links: Map<String, Link>,
    rechecking: Boolean,
    localNetworkAllowed: Boolean,
    message: Message?,
    banner: @Composable () -> Unit,
    onSelect: (String) -> Unit,
    onManage: () -> Unit,
    onRecheck: (String) -> Unit,
    onScan: () -> Unit,
    onAllow: () -> Unit,
    onOpenSettings: () -> Unit,
    onSend: suspend (Pc, RemoteAction) -> Unit,
) {
    val link = if (localNetworkAllowed) links[selected.id] ?: Link.Checking else Link.Offline
    val reachable = link == Link.Online || link == Link.Checking
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        TopBar("PC 리모컨", trailing = { IconSquareButton(Icons.Menu, "PC 관리", onManage) })
        banner()
        PcTabs(pcs, selected.id, links, localNetworkAllowed, onSelect)
        val recheckButtons: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit = {
            PlainButton(if (rechecking) "확인 중…" else "다시 확인", { onRecheck(selected.id) }, enabled = !rechecking)
            PlainButton("QR 다시 찍기", onScan)
        }
        when {
            !localNetworkAllowed -> Notice("같은 와이파이의 PC에 연결하려면 '근처 기기' 접근을 허용해야 합니다.", bad = true) {
                PlainButton("허용하기", onAllow, primary = true)
                PlainButton("설정 열기", onOpenSettings)
            }
            link == Link.LinkChanged -> Notice(
                "${selected.name}의 연결 정보가 바뀌었습니다. PC 미터기의 ⚙ 설정 → 폰 리모컨에서 PC 등록 QR을 다시 찍어 주세요.",
                bad = true,
                buttons = recheckButtons,
            )
            link == Link.Offline -> Notice(
                "${selected.name}에 연결할 수 없습니다. ${Korean.iGa(selected.name)} 켜져 있고 같은 와이파이에 연결돼 있는지, 미터기의 폰 리모컨이 켜져 있는지 확인하세요.",
                bad = true,
                buttons = recheckButtons,
            )
        }
        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
            // A new PC gets fresh buttons, so a hold never carries over to another PC.
            androidx.compose.runtime.key(selected.id) {
                val off: @Composable (Modifier) -> Unit = { m ->
                    HoldButton(Icons.Monitor, "화면 끄기", Palette.Off, Palette.OffSoft, reachable, { onSend(selected, RemoteAction.ScreenOff) }, m)
                }
                val sleep: @Composable (Modifier) -> Unit = { m ->
                    HoldButton(Icons.Moon, "절전", Palette.Sleep, Palette.SleepSoft, reachable, { onSend(selected, RemoteAction.Sleep) }, m)
                }
                if (maxWidth > maxHeight) {
                    Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        off(Modifier.weight(1f).fillMaxHeight())
                        sleep(Modifier.weight(1f).fillMaxHeight())
                    }
                } else {
                    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        off(Modifier.weight(1f).fillMaxWidth())
                        sleep(Modifier.weight(1f).fillMaxWidth())
                    }
                }
            }
        }
        StatusLine(message?.text, message?.tone ?: Tone.Neutral)
    }
}

@Composable
private fun PcTabs(pcs: List<Pc>, selectedId: String, links: Map<String, Link>, localNetworkAllowed: Boolean, onSelect: (String) -> Unit) {
    val scroll = pcs.size > 3
    Row(
        modifier = Modifier.fillMaxWidth().then(if (scroll) Modifier.horizontalScroll(rememberScrollState()) else Modifier),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        pcs.forEach { pc ->
            val link = if (localNetworkAllowed) links[pc.id] ?: Link.Checking else Link.Offline
            PcTab(pc, pc.id == selectedId, link, { onSelect(pc.id) }, if (scroll) Modifier.width(132.dp) else Modifier.weight(1f))
        }
    }
}

@Composable
private fun PcTab(pc: Pc, selected: Boolean, link: Link, onClick: () -> Unit, modifier: Modifier) {
    val shape = RoundedCornerShape(16.dp)
    val color = if (selected) Palette.Text else Palette.Muted
    Column(
        modifier = modifier
            .defaultMinSize(minHeight = 52.dp)
            .clip(shape)
            .background(if (selected) Palette.TabSelected else Palette.Card)
            .border(1.dp, if (selected) Palette.Text else Palette.Line, shape)
            .selectable(selected = selected, role = Role.Tab, onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text(pc.name, color = color, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(5.dp)) {
            Box(Modifier.size(7.dp).clip(CircleShape).background(dotColor(link)))
            Text(linkText(link), color = color, fontSize = 12.sp, maxLines = 1)
        }
    }
}

private fun dotColor(link: Link): Color = when (link) {
    Link.Online -> Palette.Good
    Link.Checking -> Palette.Muted
    Link.Offline, Link.LinkChanged -> Palette.Bad
}

private fun linkText(link: Link) = when (link) {
    Link.Online -> "연결됨"
    Link.Checking -> "확인 중"
    Link.Offline, Link.LinkChanged -> "연결 안 됨"
}

// ---- F · update banner ----

@Composable
private fun UpdateBanner(state: UpdateState, dismissed: Boolean, onUpdate: () -> Unit, onLater: () -> Unit, onInstall: () -> Unit) {
    when (state) {
        is UpdateState.Available -> if (!dismissed) Notice("새 버전 ${Korean.iGa(state.info.versionName)} 나왔습니다.", bad = false) {
            PlainButton("업데이트", onUpdate, primary = true)
            PlainButton("나중에", onLater)
        }
        is UpdateState.Downloading -> Notice("새 버전 ${Korean.eulReul(state.info.versionName)} 받는 중입니다… ${state.percent}%", bad = false)
        is UpdateState.Ready -> if (!dismissed) Notice("새 버전 ${Korean.eulReul(state.info.versionName)} 받았습니다. 설치 화면에서 '업데이트'를 눌러 주세요.", bad = false) {
            PlainButton("설치", onInstall, primary = true)
            PlainButton("나중에", onLater)
        }
        is UpdateState.Failed -> if (!dismissed) Notice("새 버전을 받지 못했거나 파일 확인에 실패해 설치하지 않았습니다. 잠시 뒤 다시 시도해 주세요.", bad = true) {
            PlainButton("다시 시도", onUpdate)
            PlainButton("나중에", onLater)
        }
        else -> Unit
    }
}

// ---- E · PC list and app info ----

@Composable
private fun ManageScreen(
    pcs: List<Pc>,
    links: Map<String, Link>,
    localNetworkAllowed: Boolean,
    message: Message?,
    update: UpdateState,
    onBack: () -> Unit,
    onScan: () -> Unit,
    onRename: (String, String) -> Unit,
    onMove: (String, Int) -> Unit,
    onDelete: (String) -> Unit,
    onCheckUpdate: () -> Unit,
    onDownloadUpdate: () -> Unit,
    onInstall: () -> Unit,
) {
    var renaming by remember { mutableStateOf<Pc?>(null) }
    var deleting by remember { mutableStateOf<Pc?>(null) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        TopBar("PC 관리", leading = { IconSquareButton(Icons.Back, "뒤로", onBack) })
        if (pcs.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                pcs.forEachIndexed { index, pc ->
                    val link = if (localNetworkAllowed) links[pc.id] ?: Link.Checking else Link.Offline
                    PcItem(
                        pc = pc,
                        detail = "${pc.host} · ${linkText(link)}",
                        canMoveUp = index > 0,
                        canMoveDown = index < pcs.lastIndex,
                        onRename = { renaming = pc },
                        onMove = { onMove(pc.id, it) },
                        onDelete = { deleting = pc },
                    )
                }
            }
        }
        PlainButton("QR 찍어서 PC 추가", onScan, Modifier.fillMaxWidth(), primary = true)
        if (message != null) StatusLine(message.text, message.tone)
        Text(
            "PC를 길게 누르면 순서 바꾸기·삭제가 나옵니다. 같은 PC의 QR을 다시 찍으면 새로 추가되지 않고 주소만 고쳐집니다.",
            color = Palette.Muted,
            fontSize = 13.sp,
            lineHeight = 1.5.em,
            modifier = Modifier.padding(start = 4.dp, end = 4.dp, top = 2.dp),
        )
        Text("앱 정보", color = Palette.Muted, fontSize = 13.sp, modifier = Modifier.padding(start = 4.dp, end = 4.dp, top = 6.dp))
        val (detail, button) = when (update) {
            UpdateState.Idle, UpdateState.Checking -> "확인 중…" to null
            UpdateState.UpToDate -> "최신 버전입니다" to ("업데이트 확인" to onCheckUpdate)
            UpdateState.CheckFailed -> "업데이트를 확인하지 못했습니다" to ("업데이트 확인" to onCheckUpdate)
            is UpdateState.Available -> "새 버전 ${Korean.iGa(update.info.versionName)} 나왔습니다" to ("업데이트" to onDownloadUpdate)
            is UpdateState.Downloading -> "새 버전을 받는 중… ${update.percent}%" to null
            is UpdateState.Ready -> "새 버전을 받았습니다" to ("설치" to onInstall)
            is UpdateState.Failed -> "새 버전을 받지 못했습니다" to ("다시 시도" to onDownloadUpdate)
        }
        Item(title = "버전 ${BuildConfig.VERSION_NAME}", detail = detail) {
            PlainButton(button?.first ?: "업데이트 확인", button?.second ?: {}, enabled = button != null)
        }
    }

    renaming?.let { pc ->
        RenameDialog(pc, onDismiss = { renaming = null }, onSave = {
            onRename(pc.id, it)
            renaming = null
        })
    }
    deleting?.let { pc ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text("PC 삭제") },
            text = { Text("${Korean.eulReul(pc.name)} 목록에서 지울까요? 다시 쓰려면 PC 등록 QR을 다시 찍으면 됩니다.") },
            confirmButton = {
                TextButton(onClick = {
                    onDelete(pc.id)
                    deleting = null
                }) { Text("삭제", color = Palette.Bad) }
            },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text("취소", color = Palette.Text) } },
            containerColor = Palette.Card,
            titleContentColor = Palette.Text,
            textContentColor = Palette.Muted,
        )
    }
}

@Composable
private fun PcItem(
    pc: Pc,
    detail: String,
    canMoveUp: Boolean,
    canMoveDown: Boolean,
    onRename: () -> Unit,
    onMove: (Int) -> Unit,
    onDelete: () -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    Box {
        Item(
            title = pc.name,
            detail = detail,
            modifier = Modifier
                .pointerInput(Unit) { detectTapGestures(onLongPress = { menu = true }) }
                .semantics { onLongClick(label = "순서 바꾸기·삭제") { menu = true; true } },
        ) {
            PlainButton("이름 바꾸기", onRename)
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }, containerColor = Palette.Button) {
            if (canMoveUp) DropdownMenuItem(text = { Text("위로 옮기기", color = Palette.Text) }, onClick = { menu = false; onMove(-1) })
            if (canMoveDown) DropdownMenuItem(text = { Text("아래로 옮기기", color = Palette.Text) }, onClick = { menu = false; onMove(1) })
            DropdownMenuItem(text = { Text("삭제", color = Palette.Bad) }, onClick = { menu = false; onDelete() })
        }
    }
}

/** The mockup's `.item` card: a title, a small detail line and one button on the right. */
@Composable
private fun Item(title: String, detail: String, modifier: Modifier = Modifier, button: @Composable () -> Unit) {
    val shape = RoundedCornerShape(18.dp)
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(shape)
            .background(Palette.Card)
            .border(1.dp, Palette.Line, shape)
            .padding(start = 14.dp, top = 12.dp, end = 12.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, color = Palette.Text, fontSize = 16.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(detail, color = Palette.Muted, fontSize = 12.5.sp)
        }
        button()
    }
}

@Composable
private fun RenameDialog(pc: Pc, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    var text by remember { mutableStateOf(pc.name) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("이름 바꾸기") },
        text = {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it.take(PcList.MAX_NAME_LENGTH) },
                singleLine = true,
                label = { Text("앱에 보일 이름") },
            )
        },
        confirmButton = { TextButton(onClick = { onSave(text) }, enabled = text.isNotBlank()) { Text("저장") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("취소") } },
        containerColor = Palette.Card,
        titleContentColor = Palette.Text,
        textContentColor = Palette.Text,
    )
}
