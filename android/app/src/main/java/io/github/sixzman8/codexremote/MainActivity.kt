package io.github.sixzman8.codexremote

import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
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
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import io.github.sixzman8.codexremote.ui.AppActions
import io.github.sixzman8.codexremote.ui.RemoteApp
import io.github.sixzman8.codexremote.ui.RemoteTheme
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    /** Set while the user is in the "install unknown apps" settings, so the installer opens on return. */
    private var installWhenAllowed = false

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

    private fun scanQr() {
        val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).enableAutoZoom().build()
        GmsBarcodeScanning.getClient(this, options).startScan()
            // This activity may have been recreated while the scanner was open; a destroyed one cannot ask, and the
            // new one shows the 허용하기 notice instead.
            .addOnSuccessListener { barcode -> if (vm.pair(barcode.rawValue.orEmpty()) && !isDestroyed) askLocalNetworkIfNeeded() }
            .addOnFailureListener {
                vm.say("QR 스캐너를 열지 못했습니다. 폰의 기본 카메라 앱으로 PC 등록 QR을 찍어도 이 앱으로 등록됩니다.", Tone.Bad)
            }
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
    }
}
