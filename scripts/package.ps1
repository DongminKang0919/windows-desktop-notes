param(
    [string]$OutputDirectory = 'artifacts',
    [switch]$ShellProbe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$destinationRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else { [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory)) }
$boundary = $artifactRoot.TrimEnd('\') + '\'
if ($destinationRoot -ne $artifactRoot -and -not $destinationRoot.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw '패키지 출력은 프로젝트 artifacts 폴더 안으로 지정하세요.'
}
for ($current = $destinationRoot; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if (Test-Path -LiteralPath $current) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw '링크된 패키지 출력 경로는 사용할 수 없습니다.'
        }
    }
}

[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '공통 버전이 MAJOR.MINOR.PATCH 형식이 아닙니다.' }
$expectedAssemblyVersion = [Version]($version + '.0')
$packageName = "WindowsMemo-$version-win-x64-framework-dependent"
$runRoot = Join-Path $destinationRoot ('package-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$publishRoot = Join-Path $runRoot $packageName
$zipPath = Join-Path $runRoot ($packageName + '.zip')
$hashPath = $zipPath + '.sha256'
$partialZip = $zipPath + '.partial'
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkPath = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdkPath -PathType Leaf)) { throw '.NET 10 SDK를 설치하세요.' }

Push-Location -LiteralPath $projectRoot
try {
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    $gitPath = if ($gitCommand) { $gitCommand.Source } else { Join-Path $env:ProgramFiles 'Git\cmd\git.exe' }
    $sourceCommit = $null
    $sourceDirty = $null
    if (Test-Path -LiteralPath $gitPath -PathType Leaf) {
        $commit = & $gitPath rev-parse HEAD
        if ($LASTEXITCODE -eq 0) { $sourceCommit = "$commit".Trim() }
        $changes = & $gitPath status --porcelain --untracked-files=normal
        if ($LASTEXITCODE -eq 0) { $sourceDirty = [bool]$changes }
    }
    $sdkVersion = & $sdkPath --version
    if ($LASTEXITCODE -ne 0) { throw 'SDK 버전을 확인하지 못했습니다.' }
    & $sdkPath build WindowsMemo.slnx --configuration Release --nologo --artifacts-path $buildRoot -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Release 빌드에 실패했습니다.' }
    $testArguments = @((Join-Path $buildRoot 'bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll'))
    if ($ShellProbe) { $testArguments += '--shell-probe' }
    & $sdkPath @testArguments
    if ($LASTEXITCODE -ne 0) { throw '검증에 실패하여 패키지를 생성하지 않습니다.' }

    New-Item -ItemType Directory -Path $publishRoot -ErrorAction Stop | Out-Null
    & $sdkPath publish src/WindowsMemo/WindowsMemo.csproj --configuration Release --runtime win-x64 --self-contained false --output $publishRoot --artifacts-path $buildRoot --nologo -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows x64 publish에 실패했습니다.' }
    foreach ($required in @('WindowsMemo.exe', 'WindowsMemo.dll', 'WindowsMemo.deps.json', 'WindowsMemo.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $required) -PathType Leaf)) { throw "필수 실행 파일이 없습니다: $required" }
    }
    $assemblyVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishRoot 'WindowsMemo.dll')).FileVersion)
    $fileVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishRoot 'WindowsMemo.exe')).FileVersion)
    if ($assemblyVersion -ne $expectedAssemblyVersion -or $fileVersion -ne $expectedAssemblyVersion) { throw '공통 버전과 실행 파일 버전이 일치하지 않습니다.' }
    $exeBytes = [IO.File]::ReadAllBytes((Join-Path $publishRoot 'WindowsMemo.exe'))
    $peOffset = [BitConverter]::ToInt32($exeBytes, 0x3c)
    if ([BitConverter]::ToUInt16($exeBytes, $peOffset + 4) -ne 0x8664) { throw 'apphost가 Windows x64 형식이 아닙니다.' }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'WindowsMemo.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @($runtimeConfig.runtimeOptions.frameworks)
    if (-not ($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version.StartsWith('10.0.') })) {
        throw '.NET 10 Windows Desktop 런타임 요구사항을 확인하지 못했습니다.'
    }
    $readme = @"
Windows 메모 $version prototype / Windows x64

실행 전 .NET 10 Desktop Runtime x64가 필요합니다.
다운로드: https://dotnet.microsoft.com/download/dotnet/10.0
SDK가 아니라 Desktop Runtime을 선택하세요.

1. ZIP 전체를 한 폴더에 풀고 WindowsMemo.exe를 실행하세요.
2. 닫기는 저장 후 해당 메모를 숨깁니다. 트레이에서 목록과 휴지통을 엽니다.
3. 종료는 트레이의 '모든 메모 저장 후 종료'를 선택하세요.
4. 일반 창 진단: WindowsMemo.exe --window (별도 메모 데이터)
5. 자동 실행은 트레이에서 선택합니다. 등록 후 실행 폴더를 이동/삭제하면 재등록해야 합니다.

메모 데이터는 %LOCALAPPDATA%\WindowsMemo\Notes에 별도로 저장합니다.
프로그램 폴더와 함께 메모 데이터 폴더를 삭제하지 마세요.
저장 스키마는 4이며 이전 스키마 1/2/3을 읽습니다. 이전 앱 버전으로 데이터 다운그레이드는 지원하지 않습니다.

이 ZIP은 설치 프로그램이나 자동 업데이트 패키지가 아닙니다.
코드 서명과 정식 릴리스 게시, 실제 UI/로그인/다중 모니터/장시간 검토는 남아 있습니다.
package-manifest.json의 파일 해시와 ZIP 옆 SHA256은 손상 확인용이며 게시자 서명을 대신하지 않습니다.
"@
    Set-Content -LiteralPath (Join-Path $publishRoot 'START-HERE.txt') -Value $readme -Encoding UTF8
    $files = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
        if ($_.Name -match 'WindowsMemo\.Tests|\.(pdb|rtf|tmp|bak|cs|xaml)$|\.damaged-' -or $relative -match '(^|/)(Notes|Diagnostic|storage-tests|workspace-tests)/') {
            throw "패키지에 개발/메모 데이터 파일이 포함되어 있습니다: $relative"
        }
        [ordered]@{ path = $relative; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{
        formatVersion = 1; product = 'WindowsMemo'; version = $version; stage = 'prototype'
        runtimeIdentifier = 'win-x64'; selfContained = $false; requiredRuntime = '.NET 10 Desktop Runtime x64'
        sourceCommit = $sourceCommit; sourceDirty = $sourceDirty; sdkVersion = "$sdkVersion".Trim()
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); files = $files
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishRoot 'package-manifest.json') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipStream = [IO.File]::Open($partialZip, [IO.FileMode]::CreateNew)
    $zipArchive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName) {
            $relative = $item.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
            $entry = $zipArchive.CreateEntry($packageName + '/' + $relative, [IO.Compression.CompressionLevel]::Optimal)
            $sourceStream = [IO.File]::OpenRead($item.FullName)
            $targetStream = $entry.Open()
            try { $sourceStream.CopyTo($targetStream) }
            finally { $targetStream.Dispose(); $sourceStream.Dispose() }
        }
    }
    finally { $zipArchive.Dispose(); $zipStream.Dispose() }
    Move-Item -LiteralPath $partialZip -Destination $zipPath -ErrorAction Stop
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $hashPath -Encoding ASCII
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -ZipPath $zipPath -ExpectedVersion $version
    [PSCustomObject]@{ Version = $version; Zip = $zipPath; SHA256 = $zipHash; ChecksumFile = $hashPath; Manifest = (Join-Path $publishRoot 'package-manifest.json') }
}
finally { Pop-Location }
