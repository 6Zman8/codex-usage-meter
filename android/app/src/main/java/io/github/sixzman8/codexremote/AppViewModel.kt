package io.github.sixzman8.codexremote

import android.app.Application
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException

enum class Link { Checking, Online, Offline, LinkChanged }

object LinkRules {
    /**
     * What a `/status` answer shows for the PC, or null to keep 연결됨: one slow Wi-Fi answer in a background
     * poll should not flash 연결 안 됨 and disable the buttons; the next poll, 다시 확인 or a failed action still reports it.
     */
    fun afterStatus(pcId: String, result: RemoteResult, shown: Link?, fromPoll: Boolean, missedBefore: Boolean): Link? =
        when (result) {
            is RemoteResult.Ok -> if (result.id.isEmpty() || result.id == pcId) Link.Online else Link.Offline
            is RemoteResult.Refused -> if (result.code == 404) Link.LinkChanged else Link.Offline
            RemoteResult.Unreachable -> if (fromPoll && shown == Link.Online && !missedBefore) null else Link.Offline
        }
}

enum class Tone { Neutral, Good, Bad }

data class Message(val text: String, val tone: Tone)

sealed interface UpdateState {
    data object Idle : UpdateState
    data object Checking : UpdateState
    data object UpToDate : UpdateState
    data object CheckFailed : UpdateState
    data class Available(val info: UpdateInfo) : UpdateState
    data class Downloading(val info: UpdateInfo, val percent: Int) : UpdateState
    data class Ready(val info: UpdateInfo, val apk: File) : UpdateState
    data class Failed(val info: UpdateInfo) : UpdateState
}

class AppViewModel(application: Application) : AndroidViewModel(application) {
    private val prefs = application.getSharedPreferences("remote", Context.MODE_PRIVATE)

    private val _pcs = MutableStateFlow(PcList.fromJson(prefs.getString(KEY_PCS, null)))
    val pcs: StateFlow<List<Pc>> = _pcs.asStateFlow()

    private val _selectedId = MutableStateFlow(prefs.getString(KEY_SELECTED, null))
    val selectedId: StateFlow<String?> = _selectedId.asStateFlow()

    private val _links = MutableStateFlow<Map<String, Link>>(emptyMap())
    val links: StateFlow<Map<String, Link>> = _links.asStateFlow()

    /** PCs whose last background poll went unanswered while they were shown as 연결됨. */
    private val missedOnce = java.util.Collections.synchronizedSet(mutableSetOf<String>())

    /** PCs whose "다시 확인" check is running. */
    private val _rechecking = MutableStateFlow<Set<String>>(emptySet())
    val rechecking: StateFlow<Set<String>> = _rechecking.asStateFlow()

    /** The one-line result under the buttons (and under the add button on the PC list). */
    private val _message = MutableStateFlow<Message?>(null)
    val message: StateFlow<Message?> = _message.asStateFlow()

    private val _update = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val update: StateFlow<UpdateState> = _update.asStateFlow()

    /** "나중에" hides the banner until the next app start or a manual check. */
    private val _updateDismissed = MutableStateFlow(false)
    val updateDismissed: StateFlow<Boolean> = _updateDismissed.asStateFlow()

    /** False while Android 17+ blocks home-network connections because nearby devices access is not allowed. */
    private val _localNetworkAllowed = MutableStateFlow(true)
    val localNetworkAllowed: StateFlow<Boolean> = _localNetworkAllowed.asStateFlow()

    private var updateJob: Job? = null

    /** The verified update the installer was already opened for by itself; kept here so a recreated activity does not reopen it. */
    var autoInstalled: UpdateInfo? = null

    init {
        checkForUpdate(manual = false)
    }

    fun setLocalNetworkAllowed(allowed: Boolean) {
        val changed = _localNetworkAllowed.value != allowed
        _localNetworkAllowed.value = allowed
        if (allowed && changed) viewModelScope.launch { checkAll() }
    }

    fun select(id: String) {
        _selectedId.value = id
        prefs.edit().putString(KEY_SELECTED, id).apply()
    }

    fun clearMessage() {
        _message.value = null
    }

    fun say(text: String, tone: Tone) {
        _message.value = Message(text, tone)
    }

    // ---- Pairing and the PC list ----

    /** Registers the PC of a scanned pairing link; true when it was one. */
    fun pair(text: String): Boolean {
        when (val parsed = Pairing.parse(text)) {
            is PairParse.Ok -> {
                val result = PcList.pair(_pcs.value, parsed.info)
                save(result.list)
                select(parsed.info.id)
                _links.update { it + (parsed.info.id to Link.Checking) }
                say(if (result.added) "새 PC를 등록했습니다." else "이미 등록된 PC라 주소만 고쳤습니다.", Tone.Good)
                viewModelScope.launch { check(result.list.first { it.id == parsed.info.id }) }
                return true
            }
            PairParse.NotPairing -> say("PC 등록 QR이 아닙니다. PC 미터기의 ⚙ 설정 → 폰 리모컨에 있는 PC 등록 QR을 찍어 주세요.", Tone.Bad)
            PairParse.UnsupportedVersion -> say("이 앱이 모르는 형식의 QR입니다. PC의 미터기를 최신 버전으로 업데이트한 뒤 QR을 다시 찍어 주세요.", Tone.Bad)
            PairParse.Invalid -> say("PC 등록 QR을 읽지 못했습니다. 미터기에서 QR을 다시 띄워 찍어 주세요.", Tone.Bad)
        }
        return false
    }

    fun rename(id: String, name: String) = save(PcList.rename(_pcs.value, id, name))

    fun move(id: String, delta: Int) = save(PcList.move(_pcs.value, id, delta))

    fun remove(id: String) {
        save(PcList.remove(_pcs.value, id))
        _links.update { it - id }
        missedOnce -= id
        if (_selectedId.value == id) _pcs.value.firstOrNull()?.let { select(it.id) }
    }

    private fun save(list: List<Pc>) {
        _pcs.value = list
        prefs.edit().putString(KEY_PCS, PcList.toJson(list)).apply()
    }

    // ---- Status ----

    /** Checks every PC every few seconds; the caller runs it only while the app is in the foreground. */
    suspend fun pollWhileVisible() {
        while (true) {
            checkAll()
            delay(POLL_INTERVAL_MS)
        }
    }

    private suspend fun checkAll() = coroutineScope {
        _pcs.value.map { async { check(it, fromPoll = true) } }.awaitAll()
    }

    fun recheck(id: String) {
        val pc = _pcs.value.firstOrNull { it.id == id } ?: return
        if (id in _rechecking.value) return
        _rechecking.update { it + id }
        viewModelScope.launch {
            try {
                check(pc)
            } finally {
                _rechecking.update { it - id }
            }
        }
    }

    private suspend fun check(pc: Pc, fromPoll: Boolean = false) {
        if (!_localNetworkAllowed.value) return
        val result = RemoteClient.status(pc)
        // A re-scan or removal while the request was out makes its answer stale.
        val current = _pcs.value.firstOrNull { it.id == pc.id } ?: return
        if (current.host != pc.host || current.port != pc.port || current.token != pc.token) return
        val link = LinkRules.afterStatus(pc.id, result, _links.value[pc.id], fromPoll, pc.id in missedOnce)
        if (link == null) {
            missedOnce += pc.id
            return
        }
        missedOnce -= pc.id
        _links.update { it + (pc.id to link) }
        if (link == Link.Online && result is RemoteResult.Ok) {
            val renamed = PcList.applyMeterName(_pcs.value, pc.id, result.name)
            if (renamed != _pcs.value) save(renamed)
        }
    }

    // ---- Screen off / sleep ----

    /** Sends the action and reports the result line; returns once the meter answered or the request failed. */
    suspend fun send(pc: Pc, action: RemoteAction) {
        viewModelScope.async {
            val result = RemoteClient.send(pc, action)
            val name = _pcs.value.firstOrNull { it.id == pc.id }?.name ?: pc.name
            when (result) {
                is RemoteResult.Ok -> {
                    missedOnce -= pc.id
                    _links.update { it + (pc.id to Link.Online) }
                    say(
                        when (action) {
                            RemoteAction.ScreenOff -> "$name 화면을 껐습니다."
                            RemoteAction.Sleep -> "${Korean.iGa(name)} 절전에 들어갑니다. 다시 켜려면 PC에서 키보드나 마우스를 누르세요."
                        },
                        Tone.Good,
                    )
                }
                is RemoteResult.Refused -> {
                    if (result.code == 404) {
                        _links.update { it + (pc.id to Link.LinkChanged) }
                        say("${name}의 연결 정보가 바뀌었습니다. PC 미터기에서 PC 등록 QR을 다시 찍어 주세요.", Tone.Bad)
                    } else {
                        say("${Korean.iGa(name)} 요청을 받지 않았습니다(HTTP ${result.code}). PC의 미터기를 최신 버전으로 업데이트해 주세요.", Tone.Bad)
                    }
                }
                RemoteResult.Unreachable -> {
                    _links.update { it + (pc.id to Link.Offline) }
                    say("${name}에 연결하지 못했습니다. 폰이 집 와이파이에 연결돼 있는지, PC와 미터기가 켜져 있는지 확인하세요.", Tone.Bad)
                }
            }
        }.await()
    }

    // ---- App update ----

    fun checkForUpdate(manual: Boolean) {
        if (updateJob?.isActive == true) return
        if (manual) _updateDismissed.value = false
        updateJob = viewModelScope.launch {
            _update.value = UpdateState.Checking
            _update.value = try {
                val info = UpdateClient.fetchManifest(BuildConfig.UPDATE_BASE_URL)
                when {
                    info == null -> UpdateState.CheckFailed.also { Log.w(TAG, "update check: no usable release manifest") }
                    Updates.isNewer(info, BuildConfig.VERSION_CODE) -> UpdateState.Available(info)
                    else -> UpdateState.UpToDate
                }
            } catch (e: IOException) {
                Log.w(TAG, "update check failed", e)
                UpdateState.CheckFailed
            }
        }
    }

    fun dismissUpdate() {
        _updateDismissed.value = true
    }

    /**
     * Downloads the APK and accepts it only when its SHA-256, package and version match the release manifest.
     * The manifest is read again first so a retry after a failed check uses the release as it is now.
     */
    fun downloadUpdate() {
        val known = when (val state = _update.value) {
            is UpdateState.Available -> state.info
            is UpdateState.Failed -> state.info
            else -> return
        }
        if (updateJob?.isActive == true) return
        updateJob = viewModelScope.launch {
            val app = getApplication<Application>()
            val apk = File(File(app.cacheDir, "updates"), Updates.APK_FILE)
            apk.delete()
            _update.value = UpdateState.Downloading(known, 0)
            val info = try {
                UpdateClient.fetchManifest(BuildConfig.UPDATE_BASE_URL)
            } catch (e: IOException) {
                Log.w(TAG, "update: release manifest unreadable", e)
                null
            }
            if (info == null || !Updates.isNewer(info, BuildConfig.VERSION_CODE)) {
                Log.w(TAG, "update: release manifest is $info")
                _update.value = if (info == null) UpdateState.Failed(known) else UpdateState.UpToDate
                return@launch
            }
            val verified = try {
                UpdateClient.download(BuildConfig.UPDATE_BASE_URL, apk) { percent -> _update.value = UpdateState.Downloading(info, percent) }
                withContext(Dispatchers.IO) {
                    when {
                        !Updates.matches(apk, info) -> false.also { Log.w(TAG, "update ${info.versionName}: SHA-256 does not match the manifest") }
                        !isThisAppAt(app, apk, info.versionCode) -> false.also { Log.w(TAG, "update ${info.versionName}: not this app at versionCode ${info.versionCode}") }
                        else -> true
                    }
                }
            } catch (e: IOException) {
                Log.w(TAG, "update ${info.versionName}: download failed", e)
                false
            }
            if (verified) {
                _update.value = UpdateState.Ready(info, apk)
            } else {
                apk.delete()
                _update.value = UpdateState.Failed(info)
            }
        }
    }

    private fun isThisAppAt(context: Context, apk: File, versionCode: Int): Boolean {
        val archive = if (Build.VERSION.SDK_INT >= 33) {
            context.packageManager.getPackageArchiveInfo(apk.path, PackageManager.PackageInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            context.packageManager.getPackageArchiveInfo(apk.path, 0)
        } ?: return false
        @Suppress("DEPRECATION")
        val code = if (Build.VERSION.SDK_INT >= 28) archive.longVersionCode else archive.versionCode.toLong()
        return archive.packageName == context.packageName && code == versionCode.toLong()
    }

    /** The verified APK, re-hashed right before it is handed to the installer; null when it no longer matches. */
    suspend fun verifiedApk(): File? {
        val ready = _update.value as? UpdateState.Ready ?: return null
        if (withContext(Dispatchers.IO) { Updates.matches(ready.apk, ready.info) }) return ready.apk
        ready.apk.delete()
        _update.value = UpdateState.Failed(ready.info)
        return null
    }

    companion object {
        private const val TAG = "CodexRemote"
        private const val KEY_PCS = "pcs"
        private const val KEY_SELECTED = "selected"
        private const val POLL_INTERVAL_MS = 4000L
    }
}
