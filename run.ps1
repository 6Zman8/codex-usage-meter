[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$executable = Join-Path $projectRoot 'bin\CodexUsageMeter.exe'

if (-not (Test-Path -LiteralPath $executable)) {
    & (Join-Path $projectRoot 'build.ps1')
}

Start-Process -FilePath $executable
