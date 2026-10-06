$ErrorActionPreference = 'Stop'
$webViewVersion = '1.0.4258.31'
$webViewHash = '56F7F4B8BF9AEE4B8EFEFBBDD4F67D5F74EBD1B100ED0806DA71BF76AF481AA9'
$webViewRoot = Join-Path $PSScriptRoot ('obj\webview2-' + $webViewVersion)
New-Item -ItemType Directory -Path $webViewRoot -Force | Out-Null
$package = Join-Path $webViewRoot 'webview2.nupkg'
if (-not (Test-Path -LiteralPath $package)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$webViewVersion/microsoft.web.webview2.$webViewVersion.nupkg" -OutFile $package
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $webViewHash) { throw 'WebView2 package SHA-256 mismatch' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($name in @('lib/net462/Microsoft.Web.WebView2.Core.dll', 'runtimes/win-x64/native/WebView2Loader.dll', 'runtimes/win-x86/native/WebView2Loader.dll', 'runtimes/win-arm64/native/WebView2Loader.dll', 'LICENSE.txt')) {
        $entry = $zip.GetEntry($name)
        if ($null -eq $entry) { throw "WebView2 component missing: $name" }
        $target = Join-Path $webViewRoot $name
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $zip.Dispose() }
$webViewRoot
