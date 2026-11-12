#requires -Version 5.1
<#
.SYNOPSIS
Downloads a file through RdpProxy with optional resume, retries and rate limiting.
.DESCRIPTION
Uses the current session's proxy export or an explicit ProxyConfigFile.
Existing output is overwritten unless Resume is specified. Credentials and
curl configuration are passed through UTF-8 stdin, not process arguments.
.PARAMETER Url
The URL to download. Required.
.PARAMETER Output
Output file; default .\test__.zip. Relative paths use the current PowerShell directory.
.PARAMETER Resume
Continue the same file from its current length. The remote server must support resume.
.PARAMETER Retry
Number of retries after the first attempt; default 3. Zero disables retries.
.PARAMETER RetryDelaySec
Seconds between retries; default 2. Zero uses curl's exponential backoff.
.PARAMETER RetryAllErrors
Retry all curl errors, including transfer interruptions. Requires curl 7.71 or later.
.PARAMETER ConnectTimeoutSec
Connection establishment timeout in seconds; default 30. Zero uses curl's default.
.PARAMETER MaxTimeSec
Maximum duration of each attempt; default 0 (unlimited). Alias: TimeoutSec.
.PARAMETER RetryMaxTimeSec
Time budget for starting retries; default 0 (unlimited). Does not stop an active attempt.
.PARAMETER LimitRate
Download rate in bytes per second, optionally with K/M/G suffix, for example 2M.
.PARAMETER Quiet
Hide the progress meter while keeping curl errors visible.
.PARAMETER CreateDirectories
Create missing directories in Output.
.PARAMETER Mode
ServerProxy reads session-ID.json (default); ClientProxy reads client-session-ID.json.
.PARAMETER ProxyConfigFile
Explicit session export containing Host, Port, Username and Password; overrides Mode's path.
.EXAMPLE
.\Download-ProxyFile.ps1 -Url 'https://example.com/file.iso'
.EXAMPLE
.\Download-ProxyFile.ps1 -Url 'https://example.com/file.iso' -Output '.\ubuntu.iso' -Resume -Retry 5 -LimitRate 2M
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Url,
    [Parameter(Position = 1)]
    [Alias('OutFile')]
    [ValidateNotNullOrEmpty()]
    [string]$Output = '.\test__.zip',
    [switch]$Resume,
    [ValidateRange(0, 100)]
    [int]$Retry = 3,
    [ValidateRange(0, 3600)]
    [int]$RetryDelaySec = 2,
    [switch]$RetryAllErrors,
    [ValidateRange(0, 2147483647)]
    [int]$ConnectTimeoutSec = 30,
    [Alias('TimeoutSec')]
    [ValidateRange(0, 2147483647)]
    [int]$MaxTimeSec = 0,
    [ValidateRange(0, 2147483647)]
    [int]$RetryMaxTimeSec = 0,
    [ValidatePattern('^[0-9]+[kKmMgG]?$')]
    [string]$LimitRate,
    [switch]$Quiet,
    [switch]$CreateDirectories,
    [ValidateSet('ServerProxy', 'ClientProxy')]
    [string]$Mode = 'ServerProxy',
    [ValidateNotNullOrEmpty()]
    [string]$ProxyConfigFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RetryAllErrors -and $Retry -eq 0) { throw 'RetryAllErrors requires Retry greater than zero.' }
$outputProvider = $null
$outputDrive = $null
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $Output, [ref]$outputProvider, [ref]$outputDrive)
if ($outputProvider.Name -ne 'FileSystem') { throw 'Output must be a filesystem path.' }
if (Test-Path -LiteralPath $outputPath -PathType Container) { throw 'Output must name a file, not a directory.' }

function ConvertTo-CurlConfigValue([string]$Text) {
    $escaped = $Text.Replace('\', '\\').Replace('"', '\"')
    $escaped = $escaped.Replace("`t", '\t').Replace("`r", '\r').Replace("`n", '\n').Replace("`v", '\v')
    '"' + $escaped + '"'
}

# Get-Command may return several curl.exe installations (for example Windows and MSYS).
$curlExe = Get-Command curl.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
if ($PSBoundParameters.ContainsKey('ProxyConfigFile')) {
    $file = (Resolve-Path -LiteralPath $ProxyConfigFile -ErrorAction Stop).ProviderPath
}
else {
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $prefix = if ($Mode -eq 'ClientProxy') { 'client-session-' } else { 'session-' }
    $file = Join-Path $env:LOCALAPPDATA ('RdpProxy\' + $prefix + $sessionId + '.json')
}
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw ('Proxy settings not found: ' + $file + '. Start the SOCKS listener in this session or specify ProxyConfigFile.')
}
try {
    $proxy = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    # JSON parser errors may include input text containing the proxy password.
    throw 'Cannot read proxy settings JSON. Check file permissions and JSON syntax.'
}
foreach ($field in @('Host', 'Port', 'Username', 'Password')) {
    if ($null -eq $proxy -or $null -eq $proxy.PSObject.Properties[$field] -or
        [string]::IsNullOrEmpty([string]$proxy.$field)) {
        throw ('Proxy config must contain a nonempty ' + $field + ' field.')
    }
}
$proxyPort = 0
if (-not [int]::TryParse([string]$proxy.Port, [ref]$proxyPort) -or $proxyPort -lt 1 -or $proxyPort -gt 65535) {
    throw 'Proxy config Port must be between 1 and 65535.'
}
$proxyHost = [string]$proxy.Host
if ($proxyHost.Contains(':') -and -not $proxyHost.StartsWith('[')) { $proxyHost = '[' + $proxyHost + ']' }
$curlConfig = @(
    ('socks5-hostname = ' + (ConvertTo-CurlConfigValue ($proxyHost + ':' + $proxyPort)))
    ('proxy-user = ' + (ConvertTo-CurlConfigValue ($proxy.Username + ':' + $proxy.Password)))
    'noproxy = ""'
    ('url = ' + (ConvertTo-CurlConfigValue $Url))
    ('output = ' + (ConvertTo-CurlConfigValue $outputPath))
    ('retry = ' + $Retry)
    ('retry-delay = ' + $RetryDelaySec)
    ('connect-timeout = ' + $ConnectTimeoutSec)
    ('max-time = ' + $MaxTimeSec)
    ('retry-max-time = ' + $RetryMaxTimeSec)
)
if ($Resume) { $curlConfig += 'continue-at = "-"' }
if ($PSBoundParameters.ContainsKey('LimitRate')) { $curlConfig += 'limit-rate = ' + (ConvertTo-CurlConfigValue $LimitRate) }
if ($Quiet) { $curlConfig += @('silent', 'show-error') }
if ($CreateDirectories) { $curlConfig += 'create-dirs' }
if ($RetryAllErrors) { $curlConfig += 'retry-all-errors' }

$previousEncoding = $OutputEncoding
try {
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    # Disable implicit curlrc settings; use exactly this URL and session proxy.
    $curlConfig | & $curlExe.Source --disable --config - --globoff --location --fail
    if ($LASTEXITCODE -ne 0) { throw ('curl.exe failed: ' + $LASTEXITCODE) }
}
finally {
    $OutputEncoding = $previousEncoding
    Remove-Variable curlConfig, proxy -ErrorAction SilentlyContinue
}
