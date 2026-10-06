[CmdletBinding()]
param(
    [string]$OutputName = 'CodexUsageMeter.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$assetsRoot = Join-Path $projectRoot 'assets'
$outputRoot = Join-Path $projectRoot 'bin'
$objectRoot = Join-Path $projectRoot 'obj'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$wpfRoot = Join-Path $frameworkRoot 'WPF'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler를 찾을 수 없습니다.'
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $objectRoot -Force | Out-Null

if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or
    -not $OutputName.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputName은 .exe 확장자를 가진 파일 이름이어야 합니다.'
}
$outputPath = Join-Path $outputRoot $OutputName
$iconPath = Join-Path $assetsRoot 'app-icon.ico'
$iconPngPath = Join-Path $assetsRoot 'app-icon.png'
$updaterPath = Join-Path $objectRoot 'CodexUsageMeter.Updater.exe'
$webViewRoot = & (Join-Path $projectRoot 'build-webview2.ps1')

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "앱 아이콘을 찾을 수 없습니다: $iconPath"
}
if (-not (Test-Path -LiteralPath $iconPngPath)) {
    throw "앱 아이콘 PNG를 찾을 수 없습니다: $iconPngPath"
}

$references = @(
    (Join-Path $frameworkRoot 'System.dll'),
    (Join-Path $frameworkRoot 'System.Core.dll'),
    (Join-Path $frameworkRoot 'System.Management.dll'),
    (Join-Path $frameworkRoot 'System.Web.Extensions.dll'),
    (Join-Path $frameworkRoot 'System.Drawing.dll'),
    (Join-Path $frameworkRoot 'System.Windows.Forms.dll'),
    (Join-Path $wpfRoot 'WindowsBase.dll'),
    (Join-Path $wpfRoot 'PresentationCore.dll'),
    (Join-Path $wpfRoot 'PresentationFramework.dll'),
    (Join-Path $frameworkRoot 'System.Xaml.dll')
    (Join-Path $webViewRoot 'lib\net462\Microsoft.Web.WebView2.Core.dll')
)

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "필수 어셈블리를 찾을 수 없습니다: $reference"
    }
}

$updaterArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/utf8output',
    ('/out:' + $updaterPath),
    ('/reference:' + (Join-Path $frameworkRoot 'System.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Core.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Windows.Forms.dll')),
    (Join-Path $sourceRoot 'Updater.cs'),
    (Join-Path $sourceRoot 'IndependentProcess.cs')
)

& $compiler $updaterArguments
if ($LASTEXITCODE -ne 0) {
    throw "업데이트 교체 프로그램 컴파일에 실패했습니다. 종료 코드: $LASTEXITCODE"
}

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/utf8output',
    ('/out:' + $outputPath),
    ('/win32icon:' + $iconPath),
    ('/resource:' + (Join-Path $sourceRoot 'Dashboard.xaml') + ',CodexUsageMeter.Dashboard.xaml'),
    ('/resource:' + (Join-Path $sourceRoot 'DarkTheme.xaml') + ',CodexUsageMeter.DarkTheme.xaml'),
    ('/resource:' + $iconPath + ',CodexUsageMeter.AppIcon.ico'),
    ('/resource:' + $iconPngPath + ',CodexUsageMeter.AppIcon.png'),
    ('/resource:' + $updaterPath + ',CodexUsageMeter.Updater.exe'),
    ('/resource:' + (Join-Path $webViewRoot 'lib\net462\Microsoft.Web.WebView2.Core.dll') + ',CodexUsageMeter.WebView2.Microsoft.Web.WebView2.Core.dll'),
    ('/resource:' + (Join-Path $webViewRoot 'runtimes\win-x64\native\WebView2Loader.dll') + ',CodexUsageMeter.WebView2.x64.WebView2Loader.dll'),
    ('/resource:' + (Join-Path $webViewRoot 'runtimes\win-x86\native\WebView2Loader.dll') + ',CodexUsageMeter.WebView2.x86.WebView2Loader.dll'),
    ('/resource:' + (Join-Path $webViewRoot 'runtimes\win-arm64\native\WebView2Loader.dll') + ',CodexUsageMeter.WebView2.arm64.WebView2Loader.dll'),
    ('/resource:' + (Join-Path $webViewRoot 'LICENSE.txt') + ',CodexUsageMeter.WebView2.LICENSE.txt')
)

foreach ($reference in $references) {
    $arguments += '/reference:' + $reference
}

$arguments += @(
    (Join-Path $sourceRoot 'Program.cs'),
    (Join-Path $sourceRoot 'AccountSwitcher.cs'),
    (Join-Path $sourceRoot 'AccountSwitcherSelfTest.cs'),
    (Join-Path $sourceRoot 'AccountSwitchRegressionTests.cs'),
    (Join-Path $sourceRoot 'UpdateUiRegressionTests.cs'),
    (Join-Path $sourceRoot 'RateLimitRegressionTests.cs'),
    (Join-Path $sourceRoot 'LayoutRegressionTests.cs'),
    (Join-Path $sourceRoot 'LayoutSettings.cs'),
    (Join-Path $sourceRoot 'LayoutEditor.cs'),
    (Join-Path $sourceRoot 'DashboardLayout.cs'),
    (Join-Path $sourceRoot 'DarkTheme.cs'),
    (Join-Path $sourceRoot 'SubscriptionCard.cs'),
    (Join-Path $sourceRoot 'SubscriptionRegressionTests.cs'),
    (Join-Path $sourceRoot 'AccountSubscription.cs'),
    (Join-Path $sourceRoot 'AccountSubscriptionRegressionTests.cs'),
    (Join-Path $sourceRoot 'WebSubscriptionStore.cs'),
    (Join-Path $sourceRoot 'WebSubscriptionWindow.cs'),
    (Join-Path $sourceRoot 'WebViewRuntime.cs'),
    (Join-Path $sourceRoot 'WebSubscriptionTests.cs'),
    (Join-Path $sourceRoot 'IndependentProcess.cs'),
    (Join-Path $sourceRoot 'ProcessLifetimeTests.cs'),
    (Join-Path $sourceRoot 'CodexClient.cs'),
    (Join-Path $sourceRoot 'SystemMonitor.cs'),
    (Join-Path $sourceRoot 'UpdateClient.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "컴파일에 실패했습니다. 종료 코드: $LASTEXITCODE"
}

Write-Host "빌드 완료: $outputPath"
