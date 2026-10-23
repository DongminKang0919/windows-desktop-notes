# Synthetic, non-executable fixtures. Does not install or extract anything.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
$artifactRoot = Join-Path $projectRoot 'artifacts'
for ($current = $artifactRoot; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Linked test output is not supported.'
    }
}
$testRoot = Join-Path $artifactRoot ('archive-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$packageRoot = "WindowsMemo-$version-win-x64-framework-dependent"
$payload = [ordered]@{}
foreach ($name in @('WindowsMemo.exe', 'WindowsMemo.dll', 'WindowsMemo.deps.json', 'WindowsMemo.runtimeconfig.json', 'START-HERE.txt', 'Velopack.dll')) {
    $payload[$name] = [Text.Encoding]::UTF8.GetBytes('synthetic, never executed: ' + $name)
}
function Hash-Bytes([byte[]]$bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}
$manifest = [ordered]@{
    formatVersion = 1; product = 'WindowsMemo'; version = $version; stage = 'prototype'
    runtimeIdentifier = 'win-x64'; selfContained = $false; sourceCommit = 'synthetic'; sourceDirty = $false
    files = @($payload.Keys | ForEach-Object { @{ path = $_; bytes = $payload[$_].Length; sha256 = (Hash-Bytes $payload[$_]) } })
}
$manifestBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 6))
function Write-Archive([string]$path, [object[]]$items) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $items) {
            $entry = $archive.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::Optimal)
            if ($item.ContainsKey('Attributes')) { $entry.ExternalAttributes = $item.Attributes }
            $target = $entry.Open()
            try {
                if ($item.ContainsKey('RepeatBytes')) {
                    $buffer = New-Object byte[] 65536
                    for ([long]$written = 0; $written -lt $item.RepeatBytes; $written += $buffer.Length) { $target.Write($buffer, 0, $buffer.Length) }
                } elseif ($item.ContainsKey('Data')) {
                    [byte[]]$bytes = $item.Data; $target.Write($bytes, 0, $bytes.Length)
                }
            } finally { $target.Dispose() }
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
function New-Fixture([string]$folder, [string]$kind, [object[]]$extra) {
    New-Item -ItemType Directory -Path $folder | Out-Null
    $prefix = if ($kind -eq 'ZIP') { $packageRoot + '/' } else { 'lib/app/' }
    $items = @($payload.Keys | ForEach-Object { @{ Name = $prefix + $_; Data = $payload[$_] } })
    $items += @{ Name = $prefix + 'package-manifest.json'; Data = $manifestBytes }
    if ($kind -eq 'Installer') {
        $xml = "<package><metadata><id>WindowsMemoApp</id><version>$version</version></metadata></package>"
        $items += @{ Name = 'WindowsMemoApp.nuspec'; Data = [Text.Encoding]::UTF8.GetBytes($xml) }
    }
    foreach ($item in $extra) {
        $copy = @{} + $item
        $copy.Name = if ($item.ContainsKey('Rooted')) { $item.Name } else { $prefix + $item.Name }
        $items += $copy
    }
    if ($kind -eq 'ZIP') {
        $path = Join-Path $folder ($packageRoot + '.zip'); Write-Archive $path $items
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($path))" | Set-Content -LiteralPath ($path + '.sha256') -Encoding ASCII
        return $path
    }
    $packageName = "WindowsMemoApp-$version-full.nupkg"
    $path = Join-Path $folder $packageName; Write-Archive $path $items
    $asset = @{ PackageId = 'WindowsMemoApp'; Version = $version; Type = 'Full'; FileName = $packageName
        Size = (Get-Item -LiteralPath $path).Length; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        SHA1 = (Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash }
    @{ Assets = @($asset) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $folder 'releases.win.json') -Encoding UTF8
    foreach ($file in @('WindowsMemoApp-win-Setup.exe', 'assets.win.json', 'RELEASES')) {
        [IO.File]::WriteAllText((Join-Path $folder $file), 'synthetic, never executed')
    }
    $files = @(Get-ChildItem -LiteralPath $folder -File | ForEach-Object {
        @{ name = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ product = 'WindowsMemoApp'; version = $version; stage = 'prototype'; signed = $false; velopackVersion = '1.2.161'
        installRoot = '%LOCALAPPDATA%\WindowsMemoApp'; dataRoot = '%LOCALAPPDATA%\WindowsMemo\Notes'; files = $files
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $folder 'installer-manifest.json') -Encoding UTF8
    return $folder
}
$cases = @(@{ Label = 'valid directory'; Extra = @(@{ Name = 'empty/' }); Expected = '' })
foreach ($name in @('../escape/', '../escape.txt', 'dir\name.txt', 'dir//child.txt', 'dir/./child.txt', 'dir/../child.txt',
    'dir/NUL.txt', 'dir/COM1.txt', ('dir/COM' + [char]0xB2 + '.txt'), 'dir/LPT9', 'dir/name.', 'dir/name ', 'dir/data:stream', 'dir/?bad')) {
    $cases += @{ Label = 'unsafe path ' + $name; Extra = @(@{ Name = $name }); Expected = 'Unsafe archive relative path' }
}
$cases += @{ Label = 'absolute path'; Extra = @(@{ Name = '/absolute'; Rooted = $true }); Expected = 'Unsafe archive relative path' }
$cases += @{ Label = 'case collision'; Extra = @(@{ Name = 'dup.bin' }, @{ Name = 'DUP.BIN' }); Expected = 'Duplicate archive path' }
$cases += @{ Label = 'file as directory'; Extra = @(@{ Name = 'node' }, @{ Name = 'node/child' }); Expected = 'file/directory path conflict' }
$cases += @{ Label = 'file/directory collision'; Extra = @(@{ Name = 'node/' }, @{ Name = 'node' }); Expected = 'Duplicate archive path' }
foreach ($attributes in @((0xA1FF -shl 16), (0x61A4 -shl 16), ([int][IO.FileAttributes]::ReparsePoint), (0x41ED -shl 16))) {
    $cases += @{ Label = 'special attributes ' + $attributes; Extra = @(@{ Name = 'special'; Attributes = $attributes }); Expected = 'links or special files' }
}
$cases += @{ Label = 'directory count limit'; Extra = @(1..1001 | ForEach-Object { @{ Name = "directory-$_/" } }); Expected = 'ZIP entry count limit' }
$cases += @{ Label = 'expanded size limit'; Extra = @(1..17 | ForEach-Object { @{ Name = "large-$_.bin"; RepeatBytes = 16MB } }); Expected = 'expanded size limit' }
$passed = 0; $index = 0
foreach ($case in $cases) {
    foreach ($kind in @('ZIP', 'Installer')) {
        $index++
        $target = New-Fixture (Join-Path $testRoot ("case-$index")) $kind $case.Extra
        $folder = if ($kind -eq 'ZIP') { Split-Path -Parent $target } else { $target }
        $before = @(Get-ChildItem -LiteralPath $folder -File | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
        $failure = ''
        try {
            if ($kind -eq 'ZIP') { & (Join-Path $PSScriptRoot 'verify-package.ps1') -ZipPath $target -ExpectedVersion $version | Out-Null }
            else { & (Join-Path $PSScriptRoot 'verify-installer.ps1') -ReleaseDirectory $target -ExpectedVersion $version | Out-Null }
        } catch { $failure = $_.Exception.Message }
        if (($case.Expected -and -not $failure.Contains($case.Expected)) -or (-not $case.Expected -and $failure)) {
            throw "FAIL $kind / $($case.Label): $failure"
        }
        $after = @(Get-ChildItem -LiteralPath $folder -File | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
        if (Compare-Object $before $after) { throw 'Verifier changed fixture files.' }
        $passed++; Write-Output "PASS $kind / $($case.Label)"
    }
}
Write-Output "PASS: $passed archive verifier checks. Synthetic fixtures: $testRoot"
