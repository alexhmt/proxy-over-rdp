#requires -Version 5.1
<#
.SYNOPSIS
Starts the RDP server tray adapter and returns without waiting for it to exit.
.PARAMETER Mode
ServerProxy: SOCKS on the RDP server, network access through the client (default).
ClientProxy: SOCKS on the client PC, network access through the RDP server.
.PARAMETER Port
Loopback SOCKS port, default 1080. Applies only when this side hosts the proxy.
.PARAMETER AuthFile
JSON file with Username and Password. Omit for generated credentials.
Applies only to the side hosting SOCKS5.
#>
[CmdletBinding()]
param(
    [ValidateSet('ServerProxy', 'ClientProxy')][string]$Mode = 'ServerProxy',
    [ValidateRange(1, 65535)][int]$Port = 1080,
    [string]$AuthFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\win-x64\RdpProxy.Server.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Server EXE is missing.' }
$modeArgument = 'server-proxy'
if ($Mode -eq 'ClientProxy') { $modeArgument = 'client-proxy' }
$arguments = @('--mode', $modeArgument)
if ($Mode -eq 'ServerProxy') {
    $arguments += @('--port', [string]$Port)
    if ($PSBoundParameters.ContainsKey('AuthFile')) {
        if ([string]::IsNullOrWhiteSpace($AuthFile)) { throw 'AuthFile must be a nonempty path.' }
        $resolvedAuth = (Resolve-Path -LiteralPath $AuthFile -ErrorAction Stop).ProviderPath
        $arguments += @('--auth-file', ('"' + $resolvedAuth + '"'))
    }
}
elseif ($PSBoundParameters.ContainsKey('Port') -or $PSBoundParameters.ContainsKey('AuthFile')) {
    throw 'Port and AuthFile apply only to the endpoint hosting SOCKS5 in the selected mode.'
}
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -PassThru
Write-Verbose ('Server started, PID ' + $process.Id + '. Use its tray menu for status and exit.')
