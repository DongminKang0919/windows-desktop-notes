param(
    [ValidateRange(1, 1000)]
    [int]$Cycles = 100,
    [ValidateRange(0, 86400)]
    [int]$IdleSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $projectRoot 'artifacts'
for ($current = $artifactRoot; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Linked output directories are not allowed.'
    }
}
$runRoot = Join-Path $artifactRoot ('workspace-run-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkPath = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdkPath)) { throw 'Install the .NET 10 SDK first.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

Push-Location -LiteralPath $projectRoot
try {
    Write-Host "Probe output: $runRoot"
    & $sdkPath build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path $buildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    $testDll = Join-Path $buildRoot 'bin\WindowsMemo.Tests\release\WindowsMemo.Tests.dll'
    # The executable writes metrics.jsonl and ten synthetic encrypted notes beneath this build.
    # Every run gets its own build so a running measurement never locks another run's output.
    & $sdkPath $testDll --workspace-probe --cycles $Cycles --idle-seconds $IdleSeconds |
        Tee-Object -FilePath (Join-Path $runRoot 'console.log')
    if ($LASTEXITCODE -ne 0) { throw 'Workspace lifecycle or idle probe failed. Check the log.' }
    Write-Host 'Probe complete. The start record identifies the run folder with metrics.jsonl and synthetic Notes.'
}
finally { Pop-Location }
