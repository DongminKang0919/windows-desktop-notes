param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $ExpectedVersion) {
    [xml]$props = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props') -Raw
    $ExpectedVersion = [string]$props.Project.PropertyGroup.Version
}
$ZipPath = (Get-Item -LiteralPath $ZipPath -ErrorAction Stop).FullName
$checksum = (Get-Content -LiteralPath ($ZipPath + '.sha256') -Raw).Trim()
$hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($checksum -ne "$hash  $([IO.Path]::GetFileName($ZipPath))") { throw 'ZIP SHA256 또는 파일명이 일치하지 않습니다.' }
if ((Get-Item -LiteralPath $ZipPath).Length -gt 100MB) { throw 'framework-dependent ZIP 크기 한도를 초과했습니다.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'archive-validation.ps1')
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    Assert-ArchiveLayout $archive
    $entries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
    $rootName = "WindowsMemo-$ExpectedVersion-win-x64-framework-dependent"
    $manifestEntry = $archive.GetEntry($rootName + '/package-manifest.json')
    if ($null -eq $manifestEntry -or $manifestEntry.Length -gt 1MB) { throw '패키지 manifest가 없거나 크기 한도를 초과했습니다.' }
    $reader = New-Object IO.StreamReader($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.formatVersion -ne 1 -or $manifest.product -ne 'WindowsMemo' -or $manifest.version -ne $ExpectedVersion -or $manifest.stage -ne 'prototype' -or $manifest.runtimeIdentifier -ne 'win-x64' -or $manifest.selfContained -ne $false) {
        throw '패키지 형식/제품/버전/배포 모드가 일치하지 않습니다.'
    }
    $listed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.files)) {
        $fullName = $rootName + '/' + $file.path
        if (-not $listed.Add($fullName) -or $file.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'manifest 파일 목록 형식이 잘못되었습니다.' }
        $entry = $archive.GetEntry($fullName)
        if ($null -eq $entry -or $entry.Length -ne $file.bytes) { throw "파일 누락/크기 불일치: $($file.path)" }
        if ($file.path -match 'WindowsMemo\.Tests|\.(pdb|rtf|tmp|bak|cs|xaml)$|\.damaged-' -or $file.path -match '(^|/)(Notes|Diagnostic|storage-tests|workspace-tests)/') { throw '개발/메모 데이터가 포함되어 있습니다.' }
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $algorithm.Dispose() }
        if ($actual -ne $file.sha256) { throw "파일 해시 불일치: $($file.path)" }
    }
    if ($entries.Count -ne $listed.Count + 1) { throw 'manifest에 없는 파일이 포함되어 있습니다.' }
    foreach ($required in @('WindowsMemo.exe', 'WindowsMemo.dll', 'WindowsMemo.deps.json', 'WindowsMemo.runtimeconfig.json', 'START-HERE.txt')) {
        if (-not $listed.Contains($rootName + '/' + $required)) { throw "필수 파일 누락: $required" }
    }
    [PSCustomObject]@{ Result = 'PASS'; Version = $manifest.version; Files = $listed.Count; ZIP = $ZipPath; SourceCommit = $manifest.sourceCommit; SourceDirty = $manifest.sourceDirty }
}
finally { $archive.Dispose() }
