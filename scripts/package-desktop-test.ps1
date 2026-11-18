param([string]$RuntimeVersion = '10.0.15')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($RuntimeVersion -notmatch '^10\.0\.\d+$') { throw '정식 .NET 10 런타임 버전을 지정하세요.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $projectRoot 'artifacts'
if (-not (Test-Path -LiteralPath $artifactRoot)) { New-Item -ItemType Directory -Path $artifactRoot | Out-Null }
if ((Get-Item -LiteralPath $artifactRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '링크된 artifacts 경로는 사용할 수 없습니다.' }
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
$runRoot = Join-Path $artifactRoot ('desktop-test-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $runRoot "WindowsMemo-$version-desktop-test"
$buildRoot = Join-Path $runRoot 'build'
New-Item -ItemType Directory -Path $packageRoot | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    & dotnet build WindowsMemo.slnx --configuration Release --nologo --artifacts-path $buildRoot -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw '빌드 실패' }
    & dotnet (Join-Path $buildRoot 'bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll')
    if ($LASTEXITCODE -ne 0) { throw '메모 회귀 검사 실패' }
    & dotnet (Join-Path $buildRoot 'bin/WindowsMemo.Monitor/release/WindowsMemo.Monitor.dll') --self-test
    if ($LASTEXITCODE -ne 0) { throw '감시 도구 검사 실패' }
    foreach ($component in @(
        @{ Project = 'src/WindowsMemo/WindowsMemo.csproj'; Folder = 'app' },
        @{ Project = 'tools/WindowsMemo.Monitor/WindowsMemo.Monitor.csproj'; Folder = 'monitor' }
    )) {
        & dotnet publish $component.Project --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $packageRoot $component.Folder) --artifacts-path $buildRoot --nologo -p:RestoreLockedMode=true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "publish 실패: $($component.Folder)" }
    }
    foreach ($hours in @(8, 24)) {
        $launcher = @"
@echo off
title Windows Memo - $hours hour monitor
"%~dp0monitor\WindowsMemo.Monitor.exe" --exe "%~dp0app\WindowsMemo.exe" --launch --duration-hours $hours --output "%~dp0logs"
echo.
pause
"@
        [IO.File]::WriteAllText((Join-Path $packageRoot "Start-${hours}h.cmd"), $launcher.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/LONG-RUN-TEST.md') -Destination (Join-Path $packageRoot 'README-ko.md')
    $files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
        @{ path = $_.FullName.Substring($packageRoot.TrimEnd('\').Length + 1); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $sourceCommit = $null
    $sourceDirty = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $sourceCommit = (& git rev-parse HEAD).Trim()
        $sourceDirty = [bool](& git status --porcelain --untracked-files=normal)
    }
    @{ schemaVersion = 1; appVersion = $version; runtimeVersion = $RuntimeVersion; selfContained = $true; sourceCommit = $sourceCommit; sourceDirty = $sourceDirty; files = $files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8
    $zip = Join-Path $runRoot "WindowsMemo-$version-desktop-test-win-x64.zip"
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -CompressionLevel Optimal
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
    [PSCustomObject]@{ Folder = $packageRoot; Zip = $zip; Runtime = $RuntimeVersion }
}
finally { Pop-Location }
