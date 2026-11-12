#requires -Version 5.1
<#
.SYNOPSIS
Validates a fresh publish directory and creates a runtime-only release ZIP and SHA-256 sidecar.
.PARAMETER PublishDirectory
Fresh output from Build.ps1 for the selected variant.
.PARAMETER OutputDirectory
Directory for the ZIP and its .zip.sha256 sidecar. Existing files are never overwritten.
.PARAMETER Version
SemVer version used for the build, without a v prefix.
.PARAMETER Variant
full bundles .NET; small requires .NET 8 Desktop Runtime x64.
.PARAMETER SourceRevision
Full Git commit SHA from this public source repository, also stamped into the assemblies.
.PARAMETER Repository
HTTPS source repository URL recorded in DIST-INFO.txt.
.PARAMETER RunUrl
Optional HTTPS build run URL recorded in DIST-INFO.txt.
.PARAMETER SdkVersion
Exact SDK version used for the build; must match global.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('full', 'small')][string]$Variant,
    [Parameter(Mandatory = $true)][string]$SourceRevision,
    [string]$Repository = 'https://github.com/alexhmt/proxy-over-rdp',
    [string]$RunUrl,
    [Parameter(Mandatory = $true)][string]$SdkVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PlainPath {
    param([string]$Path)
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not supported: $current"
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($current.TrimEnd('\', '/'))
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-PlainFiles {
    param([string]$Path)
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not supported: $($item.FullName)"
        }
        if ($item.PSIsContainer) { Get-PlainFiles -Path $item.FullName }
        else { $item }
    }
}

function Assert-GuiExecutable {
    param([string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid DOS header: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -lt 64 -or ([long]$peOffset + 94) -gt $stream.Length) { throw "Invalid PE offset: $Path" }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Expected a Windows x64 PE executable: $Path"
        }
        $stream.Position = $peOffset + 24
        if ($reader.ReadUInt16() -ne 0x20B) { throw "Expected a PE32+ executable: $Path" }
        $stream.Position = $peOffset + 92
        if ($reader.ReadUInt16() -ne 2) { throw "Expected Windows GUI subsystem 2: $Path" }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Get-FrameworkVersions {
    param([string]$Path, [string]$Deployment)
    $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $options = $config.runtimeOptions
    if ($options.tfm -cne 'net8.0') { throw "Expected net8.0 runtime options: $Path" }
    $frameworkProperty = if ($Deployment -eq 'full') { 'includedFrameworks' } else { 'frameworks' }
    $property = $options.PSObject.Properties[$frameworkProperty]
    if ($null -eq $property) { throw "Missing $frameworkProperty in $Path" }
    $frameworks = @($property.Value)
    $opposite = if ($Deployment -eq 'full') { @('framework', 'frameworks') } else { @('includedFrameworks') }
    foreach ($name in $opposite) {
        if ($null -ne $options.PSObject.Properties[$name]) { throw "Unexpected $name in $Deployment runtime config: $Path" }
    }
    $versions = @{}
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $frameworkMatches = @($frameworks | Where-Object { $_.name -ceq $name })
        if ($frameworkMatches.Count -ne 1 -or [string]$frameworkMatches[0].version -cnotmatch '^8\.[0-9]+\.[0-9]+$') {
            throw "Expected exactly one $name version 8.x in $Path"
        }
        $versions[$name] = [string]$frameworkMatches[0].version
    }
    if ($frameworks.Count -ne 2) { throw "Unexpected framework list: $Path" }
    return $versions
}

$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z'
if ($Version -cnotmatch $semVerPattern) { throw 'Version must be a SemVer 2.0 version without a v prefix.' }
if ($SourceRevision -cnotmatch '^[0-9a-fA-F]{40}\z') { throw 'SourceRevision must be a full, 40-character Git commit SHA.' }
$SourceRevision = $SourceRevision.ToLowerInvariant()
$Variant = $Variant.ToLowerInvariant()
foreach ($value in @($Repository, $RunUrl)) {
    if ($value -and ($value -match '[\r\n]' -or $value -notmatch '^https://[^\s]+$')) {
        throw 'Repository and RunUrl must be HTTPS URLs without whitespace.'
    }
}
if ([string]::IsNullOrWhiteSpace($Repository)) { throw 'Repository must not be empty.' }
$Repository = $Repository.TrimEnd('/')
$sdkConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'global.json') -Raw | ConvertFrom-Json
if ($SdkVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $SdkVersion -cne $sdkConfig.sdk.version) {
    throw 'SdkVersion must match the SDK pinned in global.json.'
}
$publish = (Get-Item -LiteralPath $PublishDirectory -Force).FullName.TrimEnd('\', '/')
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $publish -PathType Container)) { throw 'PublishDirectory must be a directory.' }
Assert-PlainPath -Path $publish
Assert-PlainPath -Path $output
if ($output.Equals($publish, [System.StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($publish + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside PublishDirectory.'
}
$archiveName = 'RdpProxy-' + $Version + '-win-x64-' + $Variant + '.zip'
$archivePath = Join-Path $output $archiveName
$hashPath = $archivePath + '.sha256'
foreach ($path in @($archivePath, $hashPath)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" }
}

$requiredFiles = @()
foreach ($side in @('Client', 'Server')) {
    foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
        $requiredFiles += 'RdpProxy.' + $side + '.' + $extension
    }
}
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $name) -PathType Leaf)) { throw "Missing publish file: $name" }
}
$publishFiles = @(Get-PlainFiles -Path $publish)
$runtimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'System.Windows.Forms.dll')
if ($Variant -eq 'small') {
    foreach ($file in $publishFiles) {
        $relative = $file.FullName.Substring($publish.Length + 1)
        if ($requiredFiles -cnotcontains $relative) { throw "Unexpected file in small publish output: $relative" }
    }
}
else {
    foreach ($file in $publishFiles) {
        $relative = $file.FullName.Substring($publish.Length + 1)
        if ($relative -match '(^|[\\/])(src|obj|\.git|\.github|graphify-out|logs?)([\\/]|$)' -or
            $file.Extension -in @('.cs', '.csproj', '.sln', '.slnx', '.props', '.targets', '.ps1', '.psm1', '.psd1', '.key', '.pfx', '.p12', '.log', '.dmp') -or
            $file.Name -match '^(auth.*\.json|credentials.*|global\.json|VERIFICATION.*|AGENTS\.md|DIST-INFO\.txt|SHA256SUMS\.txt)$') {
            throw "Publish output contains a source, credential, log, or stale metadata file: $relative"
        }
    }
    foreach ($name in $runtimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $name) -PathType Leaf)) { throw "Missing bundled runtime file: $name" }
    }
}
$expectedProductVersion = if ($Version.Contains('+')) { $Version + '.' + $SourceRevision } else { $Version + '+' + $SourceRevision }
$frameworkVersions = $null
foreach ($side in @('Client', 'Server')) {
    Assert-GuiExecutable -Path (Join-Path $publish ('RdpProxy.' + $side + '.exe'))
    $assembly = Join-Path $publish ('RdpProxy.' + $side + '.dll')
    $actualProductVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion
    if ($actualProductVersion -cne $expectedProductVersion) {
        throw "Version/commit mismatch in $assembly. Expected $expectedProductVersion; found $actualProductVersion."
    }
    $versions = Get-FrameworkVersions -Path (Join-Path $publish ('RdpProxy.' + $side + '.runtimeconfig.json')) -Deployment $Variant
    if ($null -ne $frameworkVersions) {
        foreach ($name in $versions.Keys) {
            if ($versions[$name] -cne $frameworkVersions[$name]) { throw "Client/server framework version mismatch: $name" }
        }
    }
    $frameworkVersions = $versions
}

# Only these repository files belong in a runtime distribution.
$runtimeAllowlist = @(
    'Start-Client.ps1', 'Start-Server.ps1', 'Register-Client.ps1', 'Unregister-Client.ps1',
    'Get-ProxyStatus.ps1', 'New-ProxyAuth.ps1', 'Download-ProxyFile.ps1',
    'README.md', 'docs\CONFIGURATION.md', 'docs\DIAGNOSTICS.md', 'auth.example.json',
    'LICENSE.txt', 'LICENSE-Microsoft.txt', 'THIRD-PARTY-NOTICES.txt'
)
foreach ($relative in $runtimeAllowlist) {
    $path = Join-Path $PSScriptRoot $relative
    Assert-PlainPath -Path $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime distribution file: $relative" }
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('RdpProxy-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
$archiveCreated = $false
$hashCreated = $false
try {
    foreach ($file in $publishFiles) {
        $relative = $file.FullName.Substring($publish.Length + 1)
        $target = Join-Path $stage ('bin\win-x64\' + $relative)
        $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target))
        [System.IO.File]::Copy($file.FullName, $target, $false)
    }
    foreach ($relative in $runtimeAllowlist) {
        $target = Join-Path $stage $relative
        $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target))
        [System.IO.File]::Copy((Join-Path $PSScriptRoot $relative), $target, $false)
    }
    $deployment = if ($Variant -eq 'full') { 'self-contained' } else { 'framework-dependent' }
    $distInfo = @(
        'Package: RdpProxy', ('Version: ' + $Version), ('Variant: ' + $Variant),
        ('Repository: ' + $Repository), ('SourceCommit: ' + $SourceRevision),
        ('SourceUrl: ' + $Repository + '/tree/' + $SourceRevision), ('SdkVersion: ' + $SdkVersion),
        'TargetFramework: net8.0-windows', 'RuntimeIdentifier: win-x64', ('Deployment: ' + $deployment),
        ('Microsoft.NETCore.App: ' + $frameworkVersions['Microsoft.NETCore.App']),
        ('Microsoft.WindowsDesktop.App: ' + $frameworkVersions['Microsoft.WindowsDesktop.App']),
        ('PackagedUtc: ' + [DateTime]::UtcNow.ToString('o'))
    )
    if ($RunUrl) { $distInfo += 'BuildRunUrl: ' + $RunUrl }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $stage 'DIST-INFO.txt'), (($distInfo -join "`n") + "`n"), $utf8)
    $filesByName = @{}
    foreach ($file in Get-PlainFiles -Path $stage) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $filesByName[$name] = $file.FullName
    }
    [string[]]$names = @($filesByName.Keys)
    [Array]::Sort($names, [System.StringComparer]::Ordinal)
    $hashLines = foreach ($name in $names) {
        (Get-FileHash -LiteralPath $filesByName[$name] -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
    }
    $manifestPath = Join-Path $stage 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllText($manifestPath, (($hashLines -join "`n") + "`n"), $utf8)
    $filesByName['SHA256SUMS.txt'] = $manifestPath
    [string[]]$names = @($filesByName.Keys)
    [Array]::Sort($names, [System.StringComparer]::Ordinal)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $null = [System.IO.Directory]::CreateDirectory($output)
    $zipStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $archiveCreated = $true
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $names) {
                $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $filesByName[$name], $name, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $zipStream.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashStream = [System.IO.File]::Open($hashPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $hashCreated = $true
    try {
        $bytes = $utf8.GetBytes($archiveHash + '  ' + $archiveName + "`n")
        $hashStream.Write($bytes, 0, $bytes.Length)
    }
    finally { $hashStream.Dispose() }
    Get-Item -LiteralPath $archivePath, $hashPath
}
catch {
    if ($hashCreated) { Remove-Item -LiteralPath $hashPath -Force }
    if ($archiveCreated) { Remove-Item -LiteralPath $archivePath -Force }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
