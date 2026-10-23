param([Parameter(Mandatory=$true)][string]$ReleaseDirectory, [Parameter(Mandatory=$true)][string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw '잘못된 버전입니다.' }
$manifest = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'installer-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.product -ne 'WindowsMemoApp' -or $manifest.version -ne $ExpectedVersion -or $manifest.stage -ne 'prototype' -or $manifest.signed -ne $false -or $manifest.velopackVersion -ne '1.2.161' -or $manifest.installRoot -ne '%LOCALAPPDATA%\WindowsMemoApp' -or $manifest.dataRoot -ne '%LOCALAPPDATA%\WindowsMemo\Notes') { throw '설치 메타데이터 불일치입니다.' }
$listed = @{}
foreach ($file in $manifest.files) {
    if ($file.name -notmatch '^[A-Za-z0-9._-]+$' -or $listed.ContainsKey($file.name)) { throw '위험하거나 중복된 파일명입니다.' }
    $listed[$file.name] = $true
    $target = Join-Path $ReleaseDirectory $file.name
    $item = Get-Item -LiteralPath $target
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw '설치 산출물 크기 또는 해시 불일치입니다.' }
}
$actualFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Force)
if ($actualFiles.Count -ne $listed.Count + 1) { throw 'manifest에 없는 설치 산출물이 있습니다.' }
foreach ($required in @('WindowsMemoApp-win-Setup.exe', ('WindowsMemoApp-' + $ExpectedVersion + '-full.nupkg'), 'releases.win.json', 'assets.win.json', 'RELEASES')) {
    if (-not $listed.ContainsKey($required)) { throw '필수 설치 산출물이 없습니다.' }
}
$packageName = 'WindowsMemoApp-' + $ExpectedVersion + '-full.nupkg'
$package = Join-Path $ReleaseDirectory $packageName
$feed = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'releases.win.json') -Raw | ConvertFrom-Json
$assets = @($feed.Assets)
if ($assets.Count -ne 1) { throw '예상하지 못한 업데이트 목록입니다.' }
$asset = $assets[0]
if ($asset.PackageId -ne 'WindowsMemoApp' -or $asset.Version -ne $ExpectedVersion -or $asset.Type -ne 'Full' -or $asset.FileName -ne $packageName -or $asset.Size -ne (Get-Item -LiteralPath $package).Length -or $asset.SHA256 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -or $asset.SHA1 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA1).Hash) { throw '업데이트 목록과 패키지가 일치하지 않습니다.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'archive-validation.ps1')
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    Assert-ArchiveLayout $archive
    $names = @{}
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        $names[$name] = $entry
        if ($name -match 'WindowsMemo\.Tests|\.(pdb|rtf|tmp|bak|cs|xaml)$|\.damaged-|(^|/)(Notes|Diagnostic|storage-tests|workspace-tests)/') { throw '개발 또는 메모 데이터가 포함되었습니다.' }
    }
    function Read-EntryText([string]$name) {
        if (-not $names.ContainsKey($name) -or $names[$name].Length -gt 1MB) { throw '필수 메타데이터 누락 또는 크기 초과입니다.' }
        $reader = New-Object IO.StreamReader($names[$name].Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    [xml]$nuspec = Read-EntryText 'WindowsMemoApp.nuspec'
    if ($nuspec.package.metadata.id -ne 'WindowsMemoApp' -or $nuspec.package.metadata.version -ne $ExpectedVersion) { throw 'NuGet 패키지 버전 불일치입니다.' }
    $payload = Read-EntryText 'lib/app/package-manifest.json' | ConvertFrom-Json
    if ($payload.version -ne $ExpectedVersion -or $payload.product -ne 'WindowsMemo' -or $payload.runtimeIdentifier -ne 'win-x64' -or $payload.selfContained -ne $false) { throw '앱 payload 메타데이터 불일치입니다.' }
    foreach ($file in $payload.files) {
        if ($file.path.Contains('\') -or $file.path.StartsWith('/') -or $file.path.Contains(':') -or $file.path -match '(^|/)\.\.?(/|$)') { throw '위험한 payload 경로입니다.' }
        $name = 'lib/app/' + $file.path
        if (-not $names.ContainsKey($name) -or $names[$name].Length -ne $file.bytes) { throw '앱 payload 누락 또는 크기 불일치입니다.' }
        $stream = $names[$name].Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') } finally { $algorithm.Dispose(); $stream.Dispose() }
        if ($hash -ne $file.sha256) { throw '앱 payload 해시 불일치입니다.' }
    }
    if (-not $names.ContainsKey('lib/app/WindowsMemo.exe') -or -not $names.ContainsKey('lib/app/Velopack.dll')) { throw '앱 또는 설치 시작 훅이 없습니다.' }
}
finally { $archive.Dispose() }
Write-Host 'PASS installer metadata, feed, package layout and payload hashes'
