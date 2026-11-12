#requires -Version 5.1
<#
.SYNOPSIS
Publishes the Windows x64 client and server in an isolated build directory.
.PARAMETER WorkRoot
Parent directory for a unique, temporary build directory. Defaults to the OS temp directory.
.PARAMETER OutputDirectory
Empty or new destination. Defaults to artifacts\publish\full (or small) in this repository.
.PARAMETER FrameworkDependent
Produces the small variant, requiring .NET 8 Desktop Runtime x64 on the destination.
.PARAMETER Version
SemVer package and assembly informational version. Defaults to 0.1.0.
.PARAMETER SourceRevision
Optional full Git commit SHA to include in the assembly informational version.
.EXAMPLE
.\Build.ps1 -Version 0.1.0 -SourceRevision (git rev-parse HEAD)
#>
[CmdletBinding()]
param(
    [string]$WorkRoot = [System.IO.Path]::GetTempPath(),
    [string]$OutputDirectory,
    [switch]$FrameworkDependent,
    [string]$Version = '0.1.0',
    [string]$SourceRevision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PlainPath {
    param([Parameter(Mandatory = $true)][string]$Path)
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

function Assert-EmptyDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-PlainPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            throw "Output is not a directory: $Path"
        }
        if (@(Get-ChildItem -LiteralPath $Path -Force).Count -ne 0) {
            throw "Output directory must be empty. Choose a new directory: $Path"
        }
    }
}

function Copy-SourceTree {
    param([string]$Source, [string]$Destination)
    $null = [System.IO.Directory]::CreateDirectory($Destination)
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.PSIsContainer -and $item.Name -in @('bin', 'obj', '.git', '.vs')) { continue }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source contains a reparse point: $($item.FullName)"
        }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            Copy-SourceTree -Source $item.FullName -Destination $target
        }
        else {
            [System.IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Merge-PublishTree {
    param([string]$Source, [string]$Destination)
    $null = [System.IO.Directory]::CreateDirectory($Destination)
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish output contains a reparse point: $($item.FullName)"
        }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            Merge-PublishTree -Source $item.FullName -Destination $target
        }
        elseif (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                throw "Publish file/directory collision: $target"
            }
            $sourceHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($sourceHash -cne $targetHash) { throw "Conflicting publish files: $target" }
        }
        else {
            [System.IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Copy-RuntimeNotices {
    param([string]$WorkDirectory, [string]$PublishDirectory)
    $runtimePackages = [ordered]@{
        'Microsoft.NETCore.App' = 'Microsoft.NETCore.App.Runtime.win-x64'
        'Microsoft.WindowsDesktop.App' = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
    }
    $frameworkVersions = @{}
    $cacheRoots = @()
    foreach ($side in @('Client', 'Server')) {
        $configPath = Join-Path $PublishDirectory ('RdpProxy.' + $side + '.runtimeconfig.json')
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $frameworkProperty = $config.runtimeOptions.PSObject.Properties['includedFrameworks']
        if ($null -eq $frameworkProperty) { throw "Missing includedFrameworks in $configPath" }
        $frameworks = @($frameworkProperty.Value)
        foreach ($name in $runtimePackages.Keys) {
            $frameworkMatches = @($frameworks | Where-Object { $_.name -ceq $name })
            if ($frameworkMatches.Count -ne 1 -or [string]$frameworkMatches[0].version -cnotmatch '^8\.[0-9]+\.[0-9]+\z') {
                throw "Expected exactly one $name version 8.x in $configPath"
            }
            $runtimeVersion = [string]$frameworkMatches[0].version
            if ($frameworkVersions.ContainsKey($name) -and $frameworkVersions[$name] -cne $runtimeVersion) {
                throw "Client/server framework version mismatch: $name"
            }
            $frameworkVersions[$name] = $runtimeVersion
        }
        $assetsPath = Join-Path $WorkDirectory ('src\' + $side + '\obj\project.assets.json')
        if (Test-Path -LiteralPath $assetsPath -PathType Leaf) {
            $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
            $foldersProperty = $assets.PSObject.Properties['packageFolders']
            if ($null -ne $foldersProperty) {
                foreach ($folder in $foldersProperty.Value.PSObject.Properties) { $cacheRoots += $folder.Name }
            }
        }
    }
    if ($env:NUGET_PACKAGES) {
        $environmentCache = $env:NUGET_PACKAGES
        if (-not [System.IO.Path]::IsPathRooted($environmentCache)) {
            $environmentCache = Join-Path $WorkDirectory $environmentCache
        }
        $cacheRoots += $environmentCache
    }
    $userProfilePath = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ($userProfilePath) { $cacheRoots += Join-Path $userProfilePath '.nuget\packages' }
    $cacheRoots = @($cacheRoots | Select-Object -Unique)
    foreach ($name in $runtimePackages.Keys) {
        $packageId = $runtimePackages[$name]
        $packagePath = $packageId.ToLowerInvariant() + '\' + $frameworkVersions[$name]
        $packageDirectory = $null
        $noticeSources = $null
        foreach ($cacheRoot in $cacheRoots) {
            $candidate = Join-Path $cacheRoot $packagePath
            $licenseName = $null
            foreach ($candidateName in @('LICENSE.TXT', 'LICENSE')) {
                if (Test-Path -LiteralPath (Join-Path $candidate $candidateName) -PathType Leaf) {
                    $licenseName = $candidateName
                    break
                }
            }
            if (-not $licenseName) { continue }
            $hasThirdPartyNotices = Test-Path -LiteralPath (Join-Path $candidate 'THIRD-PARTY-NOTICES.TXT') -PathType Leaf
            if ($name -ceq 'Microsoft.NETCore.App' -and -not $hasThirdPartyNotices) { continue }
            $packageDirectory = $candidate
            $noticeSources = [ordered]@{ 'LICENSE.txt' = $licenseName }
            if ($hasThirdPartyNotices) { $noticeSources['THIRD-PARTY-NOTICES.txt'] = 'THIRD-PARTY-NOTICES.TXT' }
            break
        }
        if (-not $packageDirectory) {
            throw "Missing license/notices for restored runtime package $packageId/$($frameworkVersions[$name]). Restore that exact package and retry."
        }
        $destination = Join-Path $PublishDirectory ('runtime-licenses\' + $packageId.ToLowerInvariant())
        $null = [System.IO.Directory]::CreateDirectory($destination)
        foreach ($noticeName in $noticeSources.Keys) {
            $noticePath = Join-Path $packageDirectory $noticeSources[$noticeName]
            Assert-PlainPath -Path $noticePath
            if ((Get-Item -LiteralPath $noticePath).Length -eq 0) { throw "Empty runtime notice: $noticePath" }
            [System.IO.File]::Copy($noticePath, (Join-Path $destination $noticeName), $false)
        }
    }
}

$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z'
if ($Version -cnotmatch $semVerPattern) { throw 'Version must be a SemVer 2.0 version without a v prefix.' }
if ($SourceRevision -and $SourceRevision -cnotmatch '^[0-9a-fA-F]{40}\z') {
    throw 'SourceRevision must be a full, 40-character Git commit SHA.'
}
if ($SourceRevision) { $SourceRevision = $SourceRevision.ToLowerInvariant() }
if ($env:OS -ne 'Windows_NT') { throw 'Build on Windows with the SDK pinned in global.json.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$variant = if ($FrameworkDependent) { 'small' } else { 'full' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('artifacts\publish\' + $variant) }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$workParent = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
$source = Join-Path $PSScriptRoot 'src'
if ($workParent.Equals($source, [System.StringComparison]::OrdinalIgnoreCase) -or
    $workParent.StartsWith($source + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkRoot must be outside the source tree.'
}
Assert-EmptyDirectory -Path $output
Assert-PlainPath -Path $workParent
$work = Join-Path $workParent ('RdpProxy-build-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
try {
    Assert-PlainPath -Path $source
    Copy-SourceTree -Source $source -Destination (Join-Path $work 'src')
    foreach ($name in @('Directory.Build.props', 'global.json')) {
        $path = Join-Path $PSScriptRoot $name
        Assert-PlainPath -Path $path
        [System.IO.File]::Copy($path, (Join-Path $work $name), $false)
    }
    $sdkConfig = Get-Content -LiteralPath (Join-Path $work 'global.json') -Raw | ConvertFrom-Json
    $expectedSdk = [string]$sdkConfig.sdk.version
    if ($expectedSdk -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $sdkConfig.sdk.rollForward -cne 'disable') {
        throw 'global.json must pin a stable SDK with rollForward set to disable.'
    }
    Push-Location -LiteralPath $work
    try {
        $sdkOutput = @(& $dotnet --version)
        if ($LASTEXITCODE -ne 0) { throw "The pinned .NET SDK $expectedSdk is unavailable." }
        $actualSdk = ($sdkOutput -join "`n").Trim()
        if ($actualSdk -cne $expectedSdk) { throw "Expected SDK $expectedSdk; dotnet selected $actualSdk." }
        Write-Verbose "Building with SDK $actualSdk in $work"
        $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
        foreach ($side in @('Client', 'Server')) {
            $project = Join-Path $work ('src\' + $side + '\RdpProxy.' + $side + '.csproj')
            $publishArguments = @(
                'publish', $project, '-c', 'Release', '-r', 'win-x64',
                '--self-contained', $selfContained,
                '-o', (Join-Path $work ('publish\' + $side)), '--disable-build-servers',
                ('-p:Version=' + $Version),
                '-p:EnableNETAnalyzers=false',
                '-p:RunAnalyzers=false', '-p:RunAnalyzersDuringBuild=false'
            )
            if ($SourceRevision) {
                $publishArguments += @(('-p:SourceRevisionId=' + $SourceRevision), '-p:IncludeSourceRevisionInInformationalVersion=true')
            }
            else {
                # A work directory inside another checkout must not contribute its commit SHA.
                $publishArguments += '-p:IncludeSourceRevisionInInformationalVersion=false'
            }
            & $dotnet @publishArguments | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $side (exit $LASTEXITCODE)." }
        }
    }
    finally { Pop-Location }

    $merged = Join-Path $work 'merged'
    foreach ($side in @('Client', 'Server')) {
        Merge-PublishTree -Source (Join-Path $work ('publish\' + $side)) -Destination $merged
    }
    if (-not $FrameworkDependent) {
        Copy-RuntimeNotices -WorkDirectory $work -PublishDirectory $merged
    }
    Assert-EmptyDirectory -Path $output
    $null = [System.IO.Directory]::CreateDirectory($output)
    # Copy without overwrite even if another process creates a file after the empty check.
    Copy-SourceTree -Source $merged -Destination $output
    Write-Verbose "Published $variant artifacts to $output"
    Get-Item -LiteralPath (Join-Path $output 'RdpProxy.Client.exe'), (Join-Path $output 'RdpProxy.Server.exe')
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
