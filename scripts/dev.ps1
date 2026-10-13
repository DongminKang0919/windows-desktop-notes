param(
    [ValidateSet('Desktop', 'Window', 'Test', 'ShellProbe')]
    [string]$Mode = 'Desktop'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkPath = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdkPath)) {
    throw '.NET 10 SDK를 설치하고 PowerShell을 다시 여세요.'
}

Push-Location -LiteralPath $projectRoot
try {
    & $sdkPath build WindowsMemo.slnx --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '빌드에 실패했습니다.' }
    if ($Mode -in @('Test', 'ShellProbe')) {
        $testArguments = @('tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll')
        if ($Mode -eq 'ShellProbe') { $testArguments += '--shell-probe' }
        & $sdkPath @testArguments
    }
    else {
        $appArguments = @('src/WindowsMemo/bin/Release/net10.0-windows/WindowsMemo.dll')
        if ($Mode -eq 'Window') { $appArguments += '--window' }
        & $sdkPath @appArguments
    }
    if ($LASTEXITCODE -ne 0) { throw '실행 또는 검증에 실패했습니다.' }
}
finally { Pop-Location }
