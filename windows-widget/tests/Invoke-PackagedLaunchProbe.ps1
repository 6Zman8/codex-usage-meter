[CmdletBinding()]
param([string]$OutputPath='',[string]$FixturePath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$logs=Join-Path $root 'work\v1.6.0\widget-provider'
if(-not $OutputPath){$OutputPath=Join-Path $logs 'launch-context-packaged.json'}
if(-not $FixturePath){$FixturePath=Join-Path $logs 'Launch Context Probe.exe'}
$package=Get-AppxPackage -Name 'CodexUsageMeter.WindowsWidget' | Select-Object -First 1
if(-not $package){throw 'Register the reviewed widget package before running this read-only launch test.'}
if(-not (Test-Path -LiteralPath $FixturePath)){throw 'Build the harmless child fixture first.'}
if(Test-Path -LiteralPath $OutputPath){throw 'Choose a fresh OutputPath to preserve the previous result.'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace WidgetLaunchCheck {
 [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 interface IActivationManager {
  [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string app,[MarshalAs(UnmanagedType.LPWStr)]string arguments,uint options,out uint processId);
  [PreserveSig] int ActivateForFile(IntPtr app,IntPtr items,IntPtr verb,out uint processId);
  [PreserveSig] int ActivateForProtocol(IntPtr app,IntPtr items,out uint processId);
 }
 public static class Activation {
  public static uint Run(string app,string arguments){object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));try{uint id;int hr=((IActivationManager)instance).ActivateApplication(app,arguments,0,out id);Marshal.ThrowExceptionForHR(hr);return id;}finally{Marshal.ReleaseComObject(instance);}}
 }
}
'@
$arguments='--launch-context-test "'+[IO.Path]::GetFullPath($FixturePath)+'" "'+[IO.Path]::GetFullPath($OutputPath)+'"'
$processId=[WidgetLaunchCheck.Activation]::Run(($package.PackageFamilyName+'!WidgetProvider'),$arguments)
$deadline=[DateTime]::UtcNow.AddSeconds(30)
while(-not (Test-Path -LiteralPath $OutputPath) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 200}
if(-not (Test-Path -LiteralPath $OutputPath)){throw 'Packaged launch probe produced no report within 30 seconds.'}
$result=Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
if(-not $result.passed -or $result.providerPackageError -ne 0){throw ('Packaged launch probe failed. See '+$OutputPath)}
[ordered]@{Passed=$true;ProviderPackage=$result.providerPackageFullName;ChildPackageError=$result.child.packageError;ChildExitCode=$result.childExitCode;FailureNoticeDetected=$result.failureNoticeDetected;Result=$OutputPath} | ConvertTo-Json -Compress
