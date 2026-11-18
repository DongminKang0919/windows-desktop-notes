param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ProcessId,
    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 28800,
    [ValidateRange(1, 3600)]
    [int]$IntervalSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# GetGuiResources observes the existing process; it does not inject code or force GC.
if (-not ('WindowsMemoMonitor.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace WindowsMemoMonitor {
    public static class Native {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
'@
}

$process = Get-Process -Id $ProcessId -ErrorAction Stop
$processName = $process.ProcessName
$startedAt = $process.StartTime.ToUniversalTime()
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $projectRoot 'artifacts'
for ($current = $artifactRoot; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Linked output directories are not allowed.'
    }
}
$runRoot = Join-Path $artifactRoot ('app-monitor-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$csvPath = Join-Path $runRoot 'samples.csv'
$summaryPath = Join-Path $runRoot 'summary.json'
$clock = [Diagnostics.Stopwatch]::StartNew()
$previousCpu = $null
$previousElapsed = $null
$sampleCount = 0
$status = 'interrupted'
$errorText = $null

Write-Host "Monitoring PID $ProcessId ($($process.ProcessName)). Output: $runRoot"
Write-Host 'Leave this window open. Stop with Ctrl+C; keep the resulting partial samples.'
try {
    while ($true) {
        try {
            $process.Refresh()
            if ($process.HasExited -or $process.StartTime.ToUniversalTime() -ne $startedAt) {
                $status = 'process_exited'
                break
            }
            $elapsed = $clock.Elapsed.TotalSeconds
            $cpu = $process.TotalProcessorTime.TotalSeconds
            $cpuPercent = $null
            if ($null -ne $previousCpu -and $elapsed -gt $previousElapsed) {
                $cpuPercent = [Math]::Round(100 * ($cpu - $previousCpu) / ($elapsed - $previousElapsed) / [Environment]::ProcessorCount, 4)
            }
            $handle = $process.Handle
            $gdi = [WindowsMemoMonitor.Native]::GetGuiResources($handle, 0)
            $user = [WindowsMemoMonitor.Native]::GetGuiResources($handle, 1)
            $sample = [pscustomobject]@{
                timestamp_utc = [DateTime]::UtcNow.ToString('o')
                elapsed_seconds = [Math]::Round($elapsed, 3)
                cpu_total_seconds = [Math]::Round($cpu, 3)
                cpu_machine_percent = $cpuPercent
                private_bytes = $process.PrivateMemorySize64
                working_set_bytes = $process.WorkingSet64
                handles = $process.HandleCount
                gdi_objects = $gdi
                user_objects = $user
            }
        }
        catch {
            if ($process.HasExited) { $status = 'process_exited'; break }
            throw
        }
        $sample | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Append -Encoding UTF8
        $sampleCount++
        $previousCpu = $cpu
        $previousElapsed = $elapsed
        if ($elapsed -ge $DurationSeconds) { $status = 'completed'; break }
        $remaining = $DurationSeconds - $clock.Elapsed.TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Max(1, [Math]::Min($IntervalSeconds * 1000, $remaining * 1000))) }
    }
}
catch {
    $status = 'error'
    $errorText = $_.Exception.Message
    throw
}
finally {
    $clock.Stop()
    [pscustomobject]@{
        status = $status
        process_id = $ProcessId
        process_name = $processName
        process_start_utc = $startedAt.ToString('o')
        finished_utc = [DateTime]::UtcNow.ToString('o')
        requested_seconds = $DurationSeconds
        elapsed_seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
        interval_seconds = $IntervalSeconds
        logical_processors = [Environment]::ProcessorCount
        sample_count = $sampleCount
        error = $errorText
    } | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    $process.Dispose()
    Write-Host "Status: $status. Results: $runRoot"
}
if ($status -ne 'completed') { exit 1 }
