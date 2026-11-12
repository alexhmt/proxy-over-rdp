#requires -Version 5.1
<#
.SYNOPSIS
Reads local adapter status without opening network connections or revealing credentials.
.PARAMETER Side
Selects the Client or Server adapter in this user's current desktop session.
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidateSet('Client', 'Server')][string]$Side)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$name = 'RdpProxy-' + $Side + '-' + $sid + '-' + $session
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $name, [System.IO.Pipes.PipeDirection]::In, [System.IO.Pipes.PipeOptions]::Asynchronous)
$deadline = [System.Threading.CancellationTokenSource]::new(5000)
$memory = [System.IO.MemoryStream]::new()
try {
    $pipe.Connect(3000)
    $buffer = New-Object byte[] 4096
    while ($true) {
        $task = $pipe.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token)
        $count = $task.GetAwaiter().GetResult()
        if ($count -eq 0) { break }
        if (($memory.Length + $count) -gt 65536) { throw 'Status response is too large.' }
        $memory.Write($buffer, 0, $count)
    }
    [System.Text.Encoding]::UTF8.GetString($memory.ToArray()) | ConvertFrom-Json
}
finally {
    $memory.Dispose()
    $deadline.Dispose()
    $pipe.Dispose()
}
