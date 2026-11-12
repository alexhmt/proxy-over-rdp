#requires -Version 5.1
<#
.SYNOPSIS
Registers the RdpProxy DVC plugin in both current-user COM views (x64/x86).
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\win-x64\RdpProxy.Client.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Client EXE is missing. Extract the full package first.' }
$registration = Start-Process -FilePath $exe -ArgumentList '/register' -Wait -PassThru
if ($registration.ExitCode -ne 0) { throw ('Client registration failed: ' + $registration.ExitCode) }

# Also support an existing v3 client EXE whose /register only writes its native view.
foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
    $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    $addin = $null
    $com = $null
    try {
        $addin = $root.CreateSubKey('Software\Microsoft\Terminal Server Client\Default\AddIns\RdpProxy')
        $addin.SetValue('Name', '{6f75c6ef-33f1-4f21-ae88-9eb09f65b8f1}', [Microsoft.Win32.RegistryValueKind]::String)
        $com = $root.CreateSubKey('Software\Classes\CLSID\{6f75c6ef-33f1-4f21-ae88-9eb09f65b8f1}\LocalServer32')
        $com.SetValue('', ('"' + $exe + '"'), [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        if ($null -ne $com) { $com.Dispose() }
        if ($null -ne $addin) { $addin.Dispose() }
        $root.Dispose()
    }
}
Write-Verbose 'Registered x86 and x64 COM views. Restart mRemoteNG or mstsc to reload plugins.'
