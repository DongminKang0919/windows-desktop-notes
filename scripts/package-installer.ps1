param([switch]$ShellProbe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkPath = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
Push-Location -LiteralPath $projectRoot
try {
    & $sdkPath tool restore
    if ($LASTEXITCODE -ne 0) { throw '고정된 vpk 도구 복원에 실패했습니다.' }
    $results = @(& (Join-Path $PSScriptRoot 'package.ps1') -ShellProbe:$ShellProbe | ForEach-Object { if ($_ -is [string]) { Write-Host $_ } else { $_ } })
    $package = @($results | Where-Object { $_.PSObject.Properties.Name -contains 'ChecksumFile' })
    if ($package.Count -ne 1) { throw '검증된 ZIP 결과를 찾지 못했습니다.' }
    $package = $package[0]
    $publishRoot = [IO.Path]::ChangeExtension($package.Zip, $null)
    $releaseRoot = Join-Path ([IO.Path]::GetDirectoryName($package.Zip)) 'installer'
    $welcomePath = Join-Path ([IO.Path]::GetDirectoryName($package.Zip)) 'INSTALLER-README.txt'
    @"
Windows 메모 $($package.Version) prototype
설치 위치: %LOCALAPPDATA%\WindowsMemoApp
메모 위치: %LOCALAPPDATA%\WindowsMemo\Notes (설치 폴더와 분리)
설치가 끝나면 앱이 실행됩니다. 이미 실행 중이면 트레이에서 먼저 저장 후 종료하세요.
.NET 10 Desktop Runtime x64가 없으면 설치 과정에서 다운로드합니다.
이 prototype에는 코드 서명과 자동 업데이트 확인/적용 기능이 없습니다.
업데이트 및 제거 후 데이터 보존은 실제 설치 환경 검토가 남아 있습니다.
ZIP용 START-HERE.txt의 압축 해제 안내는 설치본에는 적용하지 않습니다.
"@ | Set-Content -LiteralPath $welcomePath -Encoding UTF8
    & $sdkPath tool run vpk -- --skip-updates pack --packId WindowsMemoApp --packVersion $package.Version --packDir $publishRoot --mainExe WindowsMemo.exe --packTitle 'Windows Memo prototype' --runtime win-x64 --channel win --framework net10-x64-desktop --noPortable --shortcuts StartMenuRoot --instReadme $welcomePath --outputDir $releaseRoot
    if ($LASTEXITCODE -ne 0) { throw '설치 패키징에 실패했습니다.' }
    $setup = Join-Path $releaseRoot 'WindowsMemoApp-win-Setup.exe'
    $fullPackage = Join-Path $releaseRoot ('WindowsMemoApp-' + $package.Version + '-full.nupkg')
    $feed = Join-Path $releaseRoot 'releases.win.json'
    foreach ($file in @($setup, $fullPackage, $feed)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "설치 산출물이 없습니다: $file" }
    }
    $files = @(Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ product = 'WindowsMemoApp'; version = $package.Version; stage = 'prototype'; velopackVersion = '1.2.161'; signed = $false; installRoot = '%LOCALAPPDATA%\WindowsMemoApp'; dataRoot = '%LOCALAPPDATA%\WindowsMemo\Notes'; sourceZipSha256 = $package.SHA256; files = $files } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $releaseRoot 'installer-manifest.json') -Encoding UTF8
    & (Join-Path $PSScriptRoot 'verify-installer.ps1') -ReleaseDirectory $releaseRoot -ExpectedVersion $package.Version
    [PSCustomObject]@{ Version = $package.Version; Setup = $setup; Package = $fullPackage; Feed = $feed }
}
finally { Pop-Location }
