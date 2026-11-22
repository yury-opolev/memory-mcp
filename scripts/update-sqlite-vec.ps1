<#
.SYNOPSIS
    Vendors the official sqlite-vec loadable extension (vec0) into MemoryMcp.Core.

.DESCRIPTION
    Downloads the loadable-extension archives of one sqlite-vec release from
    https://github.com/asg017/sqlite-vec/releases with the GitHub CLI, verifies every
    archive against the release's checksums.txt AND the digest GitHub recorded for
    the asset, extracts vec0 into src/MemoryMcp.Core/runtimes/<rid>/native/, copies
    the upstream licenses, and writes src/MemoryMcp.Core/runtimes/SQLITE-VEC-VERSION.txt
    (version + archive and file sha256s). Requires `gh` (signed in) and `tar`.

.EXAMPLE
    pwsh scripts/update-sqlite-vec.ps1 -Version 0.1.9
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$runtimesDir = Join-Path $repoRoot 'src/MemoryMcp.Core/runtimes'
$work = Join-Path $repoRoot '.sqlite-vec-download'
$tag = "v$Version"
$upstream = 'asg017/sqlite-vec'

# release asset platform -> .NET runtime identifier and library file name
$targets = [ordered]@{
    'linux-x86_64'   = @{ Rid = 'linux-x64'; File = 'vec0.so' }
    'linux-aarch64'  = @{ Rid = 'linux-arm64'; File = 'vec0.so' }
    'windows-x86_64' = @{ Rid = 'win-x64'; File = 'vec0.dll' }
    'macos-x86_64'   = @{ Rid = 'osx-x64'; File = 'vec0.dylib' }
    'macos-aarch64'  = @{ Rid = 'osx-arm64'; File = 'vec0.dylib' }
}

function Get-Sha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $patterns = @('checksums.txt') + ($targets.Keys | ForEach-Object { "sqlite-vec-$Version-loadable-$_.tar.gz" })
    $ghArgs = @('release', 'download', $tag, '-R', $upstream, '-D', $work)
    foreach ($p in $patterns) { $ghArgs += @('-p', $p) }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release download failed for $tag" }

    $checksums = @{}
    foreach ($line in Get-Content (Join-Path $work 'checksums.txt')) {
        $parts = $line -split '\s+'
        if ($parts.Count -ge 2) { $checksums[$parts[0]] = $parts[1].ToLowerInvariant() }
    }

    $digestJson = & gh release view $tag -R $upstream --json assets
    if ($LASTEXITCODE -ne 0) { throw "gh release view failed for $tag" }
    $digests = @{}
    foreach ($asset in ($digestJson | ConvertFrom-Json).assets) {
        if ($asset.digest) { $digests[$asset.name] = ($asset.digest -replace '^sha256:', '').ToLowerInvariant() }
    }

    $manifest = [System.Collections.Generic.List[string]]::new()
    $manifest.Add("sqlite-vec $tag (https://github.com/$upstream/releases/tag/$tag)")
    $manifest.Add('Vendored by scripts/update-sqlite-vec.ps1. Do not edit by hand.')

    foreach ($platform in $targets.Keys) {
        $archive = "sqlite-vec-$Version-loadable-$platform.tar.gz"
        $archivePath = Join-Path $work $archive
        $actual = Get-Sha256 $archivePath
        if ($checksums[$archive] -ne $actual) { throw "checksums.txt mismatch for $archive (got $actual)" }
        if ($digests[$archive] -ne $actual) { throw "GitHub asset digest mismatch for $archive (got $actual)" }

        $target = $targets[$platform]
        $destDir = Join-Path $runtimesDir "$($target.Rid)/native"
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        & tar -xzf $archivePath -C $destDir $target.File
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }

        $fileHash = Get-Sha256 (Join-Path $destDir $target.File)
        $manifest.Add("archive $actual  $archive")
        $manifest.Add("file    $fileHash  $($target.Rid)/native/$($target.File)")
    }

    foreach ($license in 'LICENSE-MIT', 'LICENSE-APACHE') {
        $text = & gh api "repos/$upstream/contents/$($license)?ref=$tag" -H 'Accept: application/vnd.github.raw'
        if ($LASTEXITCODE -ne 0) { throw "could not fetch $license at $tag" }
        Set-Content -LiteralPath (Join-Path $runtimesDir "sqlite-vec-$license.txt") -Value $text -Encoding utf8NoBOM
    }

    Set-Content -LiteralPath (Join-Path $runtimesDir 'SQLITE-VEC-VERSION.txt') -Value $manifest -Encoding utf8NoBOM
    Write-Host "Vendored sqlite-vec $tag into $runtimesDir" -ForegroundColor Green
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
