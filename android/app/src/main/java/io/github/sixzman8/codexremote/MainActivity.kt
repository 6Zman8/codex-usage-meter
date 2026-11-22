package io.github.sixzman8.codexremote

import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.google.android.gms.common.moduleinstall.InstallStatusListener
import com.google.android.gms.common.moduleinstall.ModuleInstall
import com.google.android.gms.common.moduleinstall.ModuleInstallClient
import com.google.android.gms.common.moduleinstall.ModuleInstallRequest
import com.google.android.gms.common.moduleinstall.ModuleInstallStatusUpdate
import com.google.mlkit.common.MlKitException
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScanner
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import io.github.sixzman8.codexremote.ui.AppActions
import io.github.sixzman8.codexremote.ui.RemoteApp
import io.github.sixzman8.codexremote.ui.RemoteTheme
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    /** Set while the user is in the "install unknown apps" settings, so the installer opens on return. */
    private var installWhenAllowed = false

    /** Set from a tap until the QR scanner opened or finally failed, so repeated taps do not start it twice. */
    private var scannerPreparing = false

    private val localNetworkRequest = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        vm.setLocalNetworkAllowed(hasLocalNetworkAccess())
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        vm.setLocalNetworkAllowed(hasLocalNetworkAccess())
        if (savedInstanceState == null) {
            if (!handlePairingIntent(intent) && vm.pcs.value.isNotEmpty()) askLocalNetworkIfNeeded()
        }
        lifecycleScope.launch { repeatOnLifecycle(Lifecycle.State.STARTED) { vm.pollWhileVisible() } }
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                vm.update.collect { state ->
                    // Open the installer once by itself after a verified download, also when it finished in the background
                    // or the activity was recreated; the banner's 설치 button repeats it.
                    if (state is UpdateState.Ready && vm.autoInstalled != state.info) {
                        vm.autoInstalled = state.info
                        installUpdate()
                    }
                }
            }
        }
        setContent {
            RemoteTheme {
                RemoteApp(
                    vm,
                    AppActions(
                        scanQr = ::scanQr,
                        requestLocalNetwork = { localNetworkRequest.launch(LOCAL_NETWORK) },
                        openAppSettings = ::openAppSettings,
                        install = { lifecycleScope.launch { installUpdate() } },
                    ),
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handlePairingIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        vm.setLocalNetworkAllowed(hasLocalNetworkAccess())
        if (installWhenAllowed && packageManager.canRequestPackageInstalls()) {
            installWhenAllowed = false
            lifecycleScope.launch { installUpdate() }
        }
    }

    /** A `codexmeter://pair` link opened from the camera app or a browser. */
    private fun handlePairingIntent(intent: Intent?): Boolean {
        if (intent?.action != Intent.ACTION_VIEW) return false
        val link = intent.data?.toString() ?: return false
        if (vm.pair(link)) askLocalNetworkIfNeeded()
        return true
    }

    /**
     * Google's QR scanner is a Play services module. Play Store installs fetch it with the app (the manifest's
     * DEPENDENCIES entry), but this app is installed from a link, so the first tap asks Play services for it.
     */
    private fun scanQr() {
        if (scannerPreparing) return
        // Set before the first Play services call: a second tap during that round trip would open the scanner twice.
        scannerPreparing = true
        vm.clearMessage()
        val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).enableAutoZoom().build()
        val scanner = GmsBarcodeScanning.getClient(this, options)
        val installer = ModuleInstall.getClient(this)
        installer.areModulesAvailable(scanner)
            .addOnSuccessListener {
                when {
                    !onScreen() -> scannerPreparing = false
                    it.areModulesAvailable() -> startScan(scanner)
                    else -> installScanner(installer, scanner)
                }
            }
            .addOnFailureListener { error ->
                Log.w(TAG, "QR scanner availability check failed", error)
                if (onScreen()) startScan(scanner) else scannerPreparing = false
            }
    }

    private fun onScreen() = !isDestroyed && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)

    private fun installScanner(installer: ModuleInstallClient, scanner: GmsBarcodeScanner) {
        vm.say(SCANNER_DOWNLOADING, Tone.Neutral)
        var done = false
        lateinit var watchdog: Job
        val listener = object : InstallStatusListener {
            override fun onInstallStatusUpdated(update: ModuleInstallStatusUpdate) {
                if (done) return
                val progress = update.progressInfo
                when (update.installState) {
                    ModuleInstallStatusUpdate.InstallState.STATE_COMPLETED -> finish(true)
                    ModuleInstallStatusUpdate.InstallState.STATE_FAILED, ModuleInstallStatusUpdate.InstallState.STATE_CANCELED -> {
                        Log.w(TAG, "QR scanner download ended in state ${update.installState}, error ${update.errorCode}")
                        finish(false)
                    }
                    else -> if (progress != null && progress.totalBytesToDownload > 0) {
                        vm.say("$SCANNER_DOWNLOADING ${progress.bytesDownloaded * 100 / progress.totalBytesToDownload}%", Tone.Neutral)
                    }
                }
            }

            fun finish(installed: Boolean) {
                if (done) return
                done = true
                installer.unregisterListener(this)
                watchdog.cancel()
                scannerReady(installed, scanner)
            }
        }
        // Play services can leave a request pending (no network): stop waiting after a while so the button works
        // again, and drop the listener when this activity goes away so it does not keep it alive.
        watchdog = lifecycleScope.launch {
            try {
                delay(SCANNER_DOWNLOAD_TIMEOUT_MS)
                Log.w(TAG, "QR scanner download did not finish in ${SCANNER_DOWNLOAD_TIMEOUT_MS / 1000} s")
                listener.finish(false)
            } finally {
                if (!done) {
                    done = true
                    installer.unregisterListener(listener)
                    vm.say(SCANNER_RETAP, Tone.Neutral)
                }
            }
        }
        installer.installModules(ModuleInstallRequest.newBuilder().addApi(scanner).setListener(listener).build())
            .addOnSuccessListener { if (it.areModulesAlreadyInstalled()) listener.finish(true) }
            .addOnFailureListener { error ->
                Log.w(TAG, "QR scanner download could not start", error)
                listener.finish(false)
            }
    }

    /**
     * Opens the scanner once the download ended, unless the user left the screen meanwhile. A reported failure is
     * not final: Play services kept downloading after one and the scanner opened some seconds later, so the
     * scanner's own retry loop below waits for it.
     */
    private fun scannerReady(installed: Boolean, scanner: GmsBarcodeScanner) {
        if (!onScreen()) {
            scannerPreparing = false
            vm.say(if (installed) SCANNER_READY_RETAP else SCANNER_RETAP, Tone.Neutral)
            return
        }
        if (installed) vm.clearMessage() else vm.say(SCANNER_PREPARING, Tone.Neutral)
        startScan(scanner)
    }

    private fun startScan(scanner: GmsBarcodeScanner, attempt: Int = 1) {
        scannerPreparing = true
        scanner.startScan()
            // This activity may have been recreated while the scanner was open; a destroyed one cannot ask, and the
            // new one shows the 허용하기 notice instead.
            .addOnSuccessListener { barcode ->
                scannerPreparing = false
                if (vm.pair(barcode.rawValue.orEmpty()) && !isDestroyed) askLocalNetworkIfNeeded()
            }
            .addOnCanceledListener { scannerClosed() }
            .addOnFailureListener { error ->
                val code = (error as? MlKitException)?.errorCode
                Log.w(TAG, "QR scanner failed (attempt $attempt, code $code)", error)
                if (code == MlKitException.CODE_SCANNER_CANCELLED) {
                    scannerClosed()
                    return@addOnFailureListener
                }
                // Right after Play services reports the scanner as installed, or after a reported download failure
                // while it keeps downloading, the scanner answers "Waiting for the Barcode UI module to be
                // downloaded" (CODE_SCANNER_UNAVAILABLE) for some seconds. Retry by itself.
                if (code == MlKitException.CODE_SCANNER_UNAVAILABLE && attempt < SCAN_ATTEMPTS) {
                    vm.say(SCANNER_PREPARING, Tone.Neutral)
                    lifecycleScope.launch {
                        var handedOn = false
                        try {
                            delay(SCAN_RETRY_MS)
                            handedOn = true
                            if (onScreen()) {
                                startScan(scanner, attempt + 1)
                            } else {
                                scannerPreparing = false
                                vm.say(SCANNER_RETAP, Tone.Neutral)
                            }
                        } finally {
                            // Destroyed while waiting: the next screen must not keep showing "준비하는 중".
                            if (!handedOn) vm.say(SCANNER_RETAP, Tone.Neutral)
                        }
                    }
                    return@addOnFailureListener
                }
                scannerPreparing = false
                vm.say(
                    when (code) {
                        MlKitException.CODE_SCANNER_UNAVAILABLE, MlKitException.UNAVAILABLE -> "$SCANNER_DOWNLOAD_FAILED $CAMERA_APP_FALLBACK"
                        MlKitException.CODE_SCANNER_GOOGLE_PLAY_SERVICES_VERSION_TOO_OLD ->
                            "QR 스캐너를 쓰려면 Google Play 서비스를 업데이트해야 합니다. Play 스토어에서 업데이트한 뒤 다시 눌러 주세요. $CAMERA_APP_FALLBACK"
                        MlKitException.CODE_SCANNER_CAMERA_PERMISSION_NOT_GRANTED ->
                            "QR 스캐너가 카메라를 쓰지 못했습니다. 폰 설정 → 애플리케이션 → Google Play 서비스 → 권한에서 카메라를 허용한 뒤 다시 눌러 주세요."
                        else -> "QR 스캐너를 열지 못했습니다. Google Play 서비스를 업데이트한 뒤 다시 눌러 주세요. $CAMERA_APP_FALLBACK"
                    },
                    Tone.Bad,
                )
            }
    }

    /** The user closed the scanner without a code: leave no "준비하는 중" line behind. */
    private fun scannerClosed() {
        scannerPreparing = false
        vm.clearMessage()
    }

    private fun hasLocalNetworkAccess(): Boolean =
        Build.VERSION.SDK_INT < LOCAL_NETWORK_SDK ||
            ContextCompat.checkSelfPermission(this, LOCAL_NETWORK) == PackageManager.PERMISSION_GRANTED

    private fun askLocalNetworkIfNeeded() {
        if (!hasLocalNetworkAccess()) localNetworkRequest.launch(LOCAL_NETWORK)
    }

    private fun openAppSettings() {
        startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
    }

    /** Hands the downloaded APK to the system installer only after its hash is checked again. */
    private suspend fun installUpdate() {
        val apk = vm.verifiedApk() ?: return
        if (!packageManager.canRequestPackageInstalls()) {
            installWhenAllowed = true
            startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
            return
        }
        val uri = FileProvider.getUriForFile(this, "$packageName.updates", apk)
        startActivity(
            Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
        )
    }

    private companion object {
        /** Android 17 (API 37) gates home-network connections behind this nearby devices permission. */
        const val LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"
        const val LOCAL_NETWORK_SDK = 37
        const val TAG = "CodexRemote"
        /** About a minute of 2 s retries: a fresh download took up to 55 s in the Play emulator checks. */
        const val SCAN_ATTEMPTS = 30
        const val SCAN_RETRY_MS = 2000L
        const val SCANNER_DOWNLOAD_TIMEOUT_MS = 180_000L
        const val SCANNER_DOWNLOADING = "QR 스캐너를 처음 한 번 내려받는 중입니다…"
        const val SCANNER_PREPARING = "QR 스캐너를 준비하는 중입니다…"
        const val SCANNER_READY_RETAP = "QR 스캐너가 준비됐습니다. 'QR 찍어서 PC 추가'를 다시 눌러 주세요."
        const val SCANNER_RETAP = "QR 스캐너가 아직 준비 중입니다. 잠시 뒤 'QR 찍어서 PC 추가'를 다시 눌러 주세요."
        const val SCANNER_DOWNLOAD_FAILED = "QR 스캐너를 내려받지 못했습니다. 인터넷 연결과 Google Play 서비스 업데이트를 확인한 뒤 다시 눌러 주세요."
        /** The codexmeter://pair link opens this app from any QR reader that offers to open it. */
        const val CAMERA_APP_FALLBACK = "폰 기본 카메라로 PC 등록 QR을 찍었을 때 '열기'가 나오면 눌러도 등록됩니다."
    }
}
