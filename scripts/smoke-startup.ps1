param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [int]$HoldSeconds = 10
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable -ErrorAction Stop).Path
$log = Join-Path $env:LOCALAPPDATA 'CorePilot/logs/startup.log'
$destination = Join-Path $env:RUNNER_TEMP 'CorePilot-startup.log'
$process = $null
Write-Host "Launching CorePilot: $exe"
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds $HoldSeconds
    $process.Refresh()
    if ($process.HasExited) {
        Write-Host "CorePilot exited unexpectedly with code $($process.ExitCode)."
        if (Test-Path $log) {
            Copy-Item $log $destination -Force
            Get-Content $log -Tail 70
        }
        throw "Application failed its $HoldSeconds-second startup test."
    }
    Write-Host "PASS: CorePilot is running (PID $($process.Id))."
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
}
