[CmdletBinding()]
param(
    [string]$DotNetPath = '',
    [string]$LogDirectory = '',
    [switch]$NoDownload
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'windows-widget'
$toolRoot = Join-Path $projectRoot 'obj\widget-tools'
$packageRoot = Join-Path $projectRoot 'bin\windows-widget\package'
$outputZip = Join-Path $projectRoot 'bin\windows-widget\CodexUsageMeter.WindowsWidget.zip'
if (-not $LogDirectory) { $LogDirectory = Join-Path $projectRoot 'work\v1.8.2\widget-provider' }
New-Item -ItemType Directory -Path $toolRoot,$packageRoot,$LogDirectory -Force | Out-Null

if (-not $DotNetPath) {
    $DotNetPath = Join-Path $toolRoot 'dotnet-8.0.425\dotnet.exe'
    if (-not (Test-Path -LiteralPath $DotNetPath)) {
        $installed = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($installed) {
            $sdks = @(& $installed.Source --list-sdks)
            if ($sdks | Where-Object { $_ -match '^8\.0\.' }) { $DotNetPath = $installed.Source }
        }
    }
}
if (-not (Test-Path -LiteralPath $DotNetPath)) {
    if ($NoDownload) { throw 'An installed .NET 8 SDK or the project portable SDK is required.' }
    $archive = Join-Path $toolRoot 'dotnet-sdk-8.0.425-win-x64.zip'
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.425/dotnet-sdk-8.0.425-win-x64.zip' -OutFile $archive
    }
    $expected = 'f0b6f15bf6f1a0507205c0cb102ab99e1dee875c4682c8ed94665be1d580186a06b21455e83b3a01a0ff7f4cd887b67420f2e2fe09ed985534a4cea488ae1af9'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected) { throw 'Portable .NET SDK SHA512 mismatch.' }
    $sdkDirectory = Join-Path $toolRoot 'dotnet-8.0.425'
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory -Force
    $DotNetPath = Join-Path $sdkDirectory 'dotnet.exe'
}

$env:DOTNET_CLI_HOME = Join-Path $toolRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'obj\widget-nuget'
$buildLog = Join-Path $LogDirectory 'publish.log'
& $DotNetPath publish (Join-Path $sourceRoot 'CodexUsageMeter.WidgetProvider.csproj') -c Release -r win-x64 --self-contained true -o $packageRoot *> $buildLog
if ($LASTEXITCODE -ne 0) { throw "Widget publish failed. See $buildLog" }

# Self-contained WinRT components need both their native payload and activation declarations.
# Preserve Microsoft's complete package fragment; copying only EXE/DLL is insufficient.
[xml]$manifest = Get-Content -LiteralPath (Join-Path $sourceRoot 'AppxManifest.xml') -Raw -Encoding UTF8
$fragmentPath = Join-Path $env:NUGET_PACKAGES 'microsoft.windowsappsdk.widgets\2.0.5\runtimes-framework\package.appxfragment'
[xml]$fragment = Get-Content -LiteralPath $fragmentPath -Raw -Encoding UTF8
$extensions = $manifest.CreateElement('Extensions', $manifest.DocumentElement.NamespaceURI)
foreach ($extension in $fragment.DocumentElement.Extensions.ChildNodes) {
    if ($extension.NodeType -eq [Xml.XmlNodeType]::Element) { [void]$extensions.AppendChild($manifest.ImportNode($extension,$true)) }
}
$capabilities = $manifest.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'Capabilities' }
[void]$manifest.DocumentElement.InsertBefore($extensions,$capabilities)
$manifest.Save((Join-Path $packageRoot 'AppxManifest.xml'))

$assets = Join-Path $packageRoot 'Assets'
New-Item -ItemType Directory -Path $assets,(Join-Path $packageRoot 'Public') -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$sourceImage = [Drawing.Image]::FromFile((Join-Path $projectRoot 'assets\app-icon.png'))
try {
    foreach ($asset in @(@('StoreLogo.png',50),@('Square150x150Logo.png',150),@('Square44x44Logo.png',44),@('WidgetIcon.png',128))) {
        $bitmap = New-Object Drawing.Bitmap([int]$asset[1],[int]$asset[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.DrawImage($sourceImage,0,0,[int]$asset[1],[int]$asset[1])
            $bitmap.Save((Join-Path $assets $asset[0]),[Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceImage.Dispose() }

# Picker illustration, generated with the same public sample values as the CLI preview.
$preview = New-Object Drawing.Bitmap(600,400)
$canvas = [Drawing.Graphics]::FromImage($preview)
$font = New-Object Drawing.Font('Malgun Gothic',20)
$smallFont = New-Object Drawing.Font('Malgun Gothic',15)
$ink = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(236,239,245))
$muted = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(164,174,190))
try {
    $canvas.Clear([Drawing.Color]::FromArgb(25,31,43))
    $canvas.DrawString('Codex 사용량',$font,$ink,24,22)
    $canvas.DrawString('계정 1  Plus     5시간 73% · 주간 41%',$smallFont,$ink,24,92)
    $canvas.DrawString('계정 2            조회 실패',$smallFont,$muted,24,146)
    $canvas.DrawString('계정 3            연결 안 됨',$smallFont,$muted,24,200)
    $canvas.DrawString('계정 4  Pro      주간 0%',$smallFont,$ink,24,254)
    $canvas.DrawString('열기                    새로고침',$smallFont,$ink,24,340)
    $preview.Save((Join-Path $assets 'WidgetScreenshot.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $canvas.Dispose();$preview.Dispose();$font.Dispose();$smallFont.Dispose();$ink.Dispose();$muted.Dispose() }

Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination (Join-Path $packageRoot 'README.txt') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'THIRD-PARTY-NOTICES.txt') -Destination $packageRoot -Force
$license = Join-Path $env:NUGET_PACKAGES 'microsoft.windowsappsdk.widgets\2.0.5\license.txt'
Copy-Item -LiteralPath $license -Destination (Join-Path $packageRoot 'WindowsAppSDK-LICENSE.txt') -Force

$executable = Join-Path $packageRoot 'CodexUsageMeter.WidgetProvider.exe'
$testResult = Join-Path $LogDirectory 'self-test.json'
$test = Start-Process -FilePath $executable -ArgumentList @('--self-test',('"'+$testResult+'"')) -PassThru -WindowStyle Hidden
if (-not $test.WaitForExit(60000)) { $test.Kill(); throw 'Widget self-test timed out.' }
if ($test.ExitCode -ne 0) { throw "Widget self-test failed ($($test.ExitCode)). See $testResult" }
$result = Get-Content -LiteralPath $testResult -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $result.passed) { throw "Widget self-test did not pass. See $testResult" }

$runtimeResult = Join-Path $LogDirectory 'runtime-probe.json'
$runtimeTest = Start-Process -FilePath $executable -ArgumentList @('--runtime-probe',('"'+$runtimeResult+'"')) -PassThru -WindowStyle Hidden
if (-not $runtimeTest.WaitForExit(60000)) { $runtimeTest.Kill(); throw 'Widget runtime probe timed out.' }
if ($runtimeTest.ExitCode -ne 0) { throw "Widget runtime probe failed ($($runtimeTest.ExitCode)). See $runtimeResult" }

# A harmless unpackaged child verifies quoting, explicit breakaway and nonzero-exit reporting.
$fixture = Join-Path $LogDirectory 'Launch Context Probe.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 ('/out:'+$fixture) ('/r:'+(Join-Path $framework 'System.Web.Extensions.dll')) (Join-Path $sourceRoot 'tests\LaunchContextProbe.cs') *> (Join-Path $LogDirectory 'launch-probe-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Launch probe compilation failed.' }
$launchResult = Join-Path $LogDirectory 'launch-context-unpackaged.json'
$launchTest = Start-Process -FilePath $executable -ArgumentList @('--launch-context-test',('"'+$fixture+'"'),('"'+$launchResult+'"')) -PassThru -WindowStyle Hidden
if (-not $launchTest.WaitForExit(30000)) { $launchTest.Kill(); throw 'Launch context probe timed out.' }
if ($launchTest.ExitCode -ne 0) { throw "Launch context probe failed. See $launchResult" }

$makeAppx = Join-Path $env:NUGET_PACKAGES 'microsoft.windows.sdk.buildtools\10.0.26100.4654\bin\10.0.26100.0\x64\makeappx.exe'
if (-not (Test-Path -LiteralPath $makeAppx)) { throw 'Microsoft MakeAppx validation tool was not restored.' }
$packageLog = Join-Path $LogDirectory 'makeappx.log'
& $makeAppx pack /d $packageRoot /p (Join-Path $LogDirectory 'validation.msix') /o *> $packageLog
if ($LASTEXITCODE -ne 0) { throw "MSIX package validation failed. See $packageLog" }

$required = @('AppxManifest.xml','CodexUsageMeter.WidgetProvider.exe','Microsoft.Windows.Widgets.dll','Microsoft.Windows.Widgets.Projection.dll','coreclr.dll','Assets\WidgetIcon.png','Assets\WidgetScreenshot.png')
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relative))) { throw "Missing package file: $relative" } }
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $outputZip) {
    # Atomic replacement keeps an existing delivery zip intact until the new archive is complete.
    $zipTemporary = $outputZip + '.new'
} else { $zipTemporary = $outputZip }
if (Test-Path -LiteralPath $zipTemporary) { throw "Archive output already exists: $zipTemporary" }
[IO.Compression.ZipFile]::CreateFromDirectory($packageRoot,$zipTemporary,[IO.Compression.CompressionLevel]::Optimal,$false)
if ($zipTemporary -ne $outputZip) {
    $oldZip = Join-Path $LogDirectory ('previous-widget-'+[Guid]::NewGuid().ToString('N')+'.zip')
    [IO.File]::Replace($zipTemporary,$outputZip,$oldZip)
}
$summary = [ordered]@{ Version='1.8.2.0'; Architecture='x64'; PackageFolder=$packageRoot; Zip=$outputZip; Sha256=(Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash; Bytes=(Get-Item -LiteralPath $outputZip).Length; Tests=$result.total; NativeWinRtAndComProbe=$true; UnpackagedLaunchProbe=$true; MakeAppxValidated=$true; Registered=$false }
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LogDirectory 'build-summary.json') -Encoding UTF8
$summary | ConvertTo-Json -Compress
