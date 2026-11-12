#requires -Version 5.1
<#
.SYNOPSIS
Removes RdpProxy registration from both current-user COM views (x64/x86).
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\win-x64\RdpProxy.Client.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Client EXE is missing.' }
if ($PSCmdlet.ShouldProcess('HKCU RdpProxy registration', 'Remove')) {
    $unregistration = Start-Process -FilePath $exe -ArgumentList '/unregister' -Wait -PassThru
    if ($unregistration.ExitCode -ne 0) { throw ('Client unregistration failed: ' + $unregistration.ExitCode) }
    # Older client binaries remove only their native COM view.
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $root.DeleteSubKeyTree('Software\Microsoft\Terminal Server Client\Default\AddIns\RdpProxy', $false)
            $root.DeleteSubKeyTree('Software\Classes\CLSID\{6f75c6ef-33f1-4f21-ae88-9eb09f65b8f1}', $false)
        }
        finally { $root.Dispose() }
    }

}
