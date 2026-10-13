param([Parameter(Mandatory=$true)][string]$ReleaseDirectory, [string]$ExpectedVersion = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $ExpectedVersion) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $ExpectedVersion = [string]$props.Project.PropertyGroup.Version
}
$artifactRoot = Join-Path $projectRoot 'artifacts'
for ($current = $artifactRoot; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '링크된 검증 출력 경로는 사용할 수 없습니다.' }
}
$testRoot = Join-Path $projectRoot ('artifacts/installer-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
function New-Case([string]$name) {
    $directory = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path $directory | Out-Null
    Get-ChildItem -LiteralPath $ReleaseDirectory -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $directory }
    return $directory
}
function Check([string]$directory, [bool]$pass, [string]$name, [string]$version = $ExpectedVersion) {
    $accepted = $false
    try { & (Join-Path $PSScriptRoot 'verify-installer.ps1') -ReleaseDirectory $directory -ExpectedVersion $version; $accepted = $true } catch { if ($pass) { throw } }
    if ($accepted -ne $pass) { throw "FAIL $name" }
    Write-Host "PASS $name"
}
function Refresh-Manifest([string]$directory) {
    $file = Join-Path $directory 'installer-manifest.json'
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    foreach ($item in $manifest.files) {
        $target = Join-Path $directory $item.name
        $item.bytes = (Get-Item -LiteralPath $target).Length
        $item.sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $file -Encoding UTF8
}
Check $ReleaseDirectory $true 'valid installer'
$case = New-Case 'setup-tamper'
$file = Join-Path $case 'WindowsMemoApp-win-Setup.exe'
$bytes = [IO.File]::ReadAllBytes($file); $bytes[0] = $bytes[0] -bxor 1; [IO.File]::WriteAllBytes($file, $bytes)
Check $case $false 'changed setup rejected'
$case = New-Case 'feed-version'
$file = Join-Path $case 'releases.win.json'
$feed = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
$feed.Assets[0].Version = '999.0.0'
$feed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $file -Encoding UTF8
Refresh-Manifest $case
Check $case $false 'wrong feed version rejected after manifest refresh'
$case = New-Case 'payload-tamper'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$file = Join-Path $case ('WindowsMemoApp-' + $ExpectedVersion + '-full.nupkg')
$archive = [IO.Compression.ZipFile]::Open($file, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $archive.GetEntry('lib/app/START-HERE.txt')
    $entry.Delete()
    $replacement = $archive.CreateEntry('lib/app/START-HERE.txt')
    $writer = New-Object IO.StreamWriter($replacement.Open())
    try { $writer.Write('changed payload') } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
$feedPath = Join-Path $case 'releases.win.json'
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
$feed.Assets[0].Size = (Get-Item -LiteralPath $file).Length
$feed.Assets[0].SHA256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
$feed.Assets[0].SHA1 = (Get-FileHash -LiteralPath $file -Algorithm SHA1).Hash
$feed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $feedPath -Encoding UTF8
Refresh-Manifest $case
Check $case $false 'changed payload rejected after outer hash refresh'
Check $ReleaseDirectory $false 'wrong expected version rejected' '999.0.0'
$case = New-Case 'undeclared-file'
Set-Content -LiteralPath (Join-Path $case 'extra.txt') -Value 'extra'
Check $case $false 'undeclared file rejected'
Write-Host 'PASS: 6 installer checks. Actual installation/update is not covered.'
