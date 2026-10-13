param([Parameter(Mandatory = $true)][string]$ZipPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ZipPath = (Get-Item -LiteralPath $ZipPath).FullName
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path (Join-Path $projectRoot 'artifacts') ('verification-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$verify = Join-Path $PSScriptRoot 'verify-package.ps1'
function Copy-TestZip([string]$name) {
    $folder = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path $folder | Out-Null
    $target = Join-Path $folder ([IO.Path]::GetFileName($ZipPath))
    Copy-Item -LiteralPath $ZipPath -Destination $target
    Copy-Item -LiteralPath ($ZipPath + '.sha256') -Destination ($target + '.sha256')
    return $target
}
function Set-Checksum([string]$target) {
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($target))" | Set-Content -LiteralPath ($target + '.sha256') -Encoding ASCII
}
function Assert-Rejected([scriptblock]$action, [string]$message) {
    $rejected = $false
    try { & $action | Out-Null }
    catch {
        if (-not $_.Exception.Message.Contains($message)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw '거부해야 하는 패키지를 허용했습니다.' }
}
& $verify -ZipPath $ZipPath | Out-Null
Write-Output 'PASS: valid package'
$badChecksum = Copy-TestZip 'checksum'
('0' * 64 + '  ' + [IO.Path]::GetFileName($badChecksum)) | Set-Content -LiteralPath ($badChecksum + '.sha256') -Encoding ASCII
Assert-Rejected { & $verify -ZipPath $badChecksum } 'ZIP SHA256'
Write-Output 'PASS: wrong ZIP checksum rejected'
$tampered = Copy-TestZip 'tampered'
$archive = [IO.Compression.ZipFile]::Open($tampered, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = @($archive.Entries | Where-Object { $_.FullName.EndsWith('/START-HERE.txt') })[0]
    $name = $entry.FullName; $length = [int]$entry.Length
    $entry.Delete()
    $replacement = $archive.CreateEntry($name)
    $stream = $replacement.Open()
    try { $stream.Write((New-Object byte[] $length), 0, $length) } finally { $stream.Dispose() }
}
finally { $archive.Dispose() }
Set-Checksum $tampered
Assert-Rejected { & $verify -ZipPath $tampered } '파일 해시 불일치'
Write-Output 'PASS: changed file rejected after recomputing archive checksum'
$traversal = Copy-TestZip 'traversal'
$archive = [IO.Compression.ZipFile]::Open($traversal, [IO.Compression.ZipArchiveMode]::Update)
try { [void]$archive.CreateEntry('../outside.txt') } finally { $archive.Dispose() }
Set-Checksum $traversal
Assert-Rejected { & $verify -ZipPath $traversal } '안전하지 않은 상대 경로'
Write-Output 'PASS: ZIP traversal path rejected'
Assert-Rejected { & $verify -ZipPath $ZipPath -ExpectedVersion '99.0.0' } 'manifest가 없거나'
Write-Output 'PASS: wrong product version rejected'
Assert-Rejected { & (Join-Path $PSScriptRoot 'package.ps1') -OutputDirectory '.' } 'artifacts 폴더 안'
Write-Output 'PASS: output outside artifacts rejected before build'
Write-Output "PASS: 6 package checks. Synthetic test files: $testRoot"
