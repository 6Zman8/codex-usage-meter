@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "buildExit=%errorlevel%"
echo.
if "%buildExit%"=="0" (
    echo Build complete. Open bin\CodexUsageMeter.exe to run the app.
) else (
    echo Build failed. See the error above and docs\CROSS_PC.md.
)
if /i not "%~1"=="--no-pause" pause
exit /b %buildExit%
