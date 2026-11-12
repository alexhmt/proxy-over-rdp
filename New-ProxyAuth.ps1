#requires -Version 5.1
<#
.SYNOPSIS
Creates a SOCKS5 auth JSON file protected for the current Windows user.
.PARAMETER UserName
SOCKS username. The password is requested with hidden input.
.PARAMETER Path
New auth file path. Existing files are never overwritten.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()][string]$UserName = 'rdp',
    [string]$Path = (Join-Path $env:LOCALAPPDATA 'RdpProxy\auth.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
if (Test-Path -LiteralPath $fullPath) { throw 'Auth file already exists. Choose another path or edit the existing file.' }
$encoding = [System.Text.UTF8Encoding]::new($false, $true)
if ($UserName -match '[\p{Cc}]' -or $encoding.GetByteCount($UserName) -gt 255) {
    throw 'Username must contain 1..255 UTF-8 bytes without control characters.'
}
if (-not $PSCmdlet.ShouldProcess($fullPath, 'Create current-user SOCKS auth file')) { return }
$secret = $null
$created = $false
$written = $false
try {
    $secret = Read-Host -Prompt 'SOCKS5 password' -AsSecureString
    $credential = [System.Management.Automation.PSCredential]::new($UserName, $secret)
    $passwordText = $credential.GetNetworkCredential().Password
    if ([string]::IsNullOrEmpty($passwordText) -or $passwordText -match '[\p{Cc}]' -or $encoding.GetByteCount($passwordText) -gt 255) {
        throw 'Password must contain 1..255 UTF-8 bytes without control characters.'
    }
    $json = @{ Username = $UserName; Password = $passwordText } | ConvertTo-Json
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $parent)) {
        $null = New-Item -ItemType Directory -Path $parent -Force
    }
    $null = New-Item -ItemType File -Path $fullPath -ErrorAction Stop
    $created = $true
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [System.Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner($sid)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid, [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow))
    # Set the ACL while the file is still empty, before writing any password.
    Set-Acl -LiteralPath $fullPath -AclObject $acl
    [System.IO.File]::WriteAllText($fullPath, $json, $encoding)
    $written = $true
    Get-Item -LiteralPath $fullPath
}
finally {
    if ($null -ne $secret) { $secret.Dispose() }
    Remove-Variable passwordText, credential, json -ErrorAction SilentlyContinue
    if ($created -and -not $written) { Remove-Item -LiteralPath $fullPath -Force }
}
