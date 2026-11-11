param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [int]$HoldSeconds = 12
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable -ErrorAction Stop).Path
$log = Join-Path $env:APPDATA 'CorePilot/nusalite.log'

# The previous smoke test incorrectly passed even while a modal CorePilot
# error MessageBox was displayed, because the process stayed alive.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class CorePilotDialogProbe
{
    private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int capacity);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public static bool HasErrorDialog(uint pid)
    {
        bool found = false;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != pid || !IsWindowVisible(hWnd)) return true;
            var name = new StringBuilder(64);
            GetClassName(hWnd, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$process = $null
Write-Host "Launching CorePilot: $exe"
try {
    $process = Start-Process -FilePath $exe -PassThru
    for ($n = 0; $n -lt ($HoldSeconds * 2); $n++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) {
            throw "CorePilot quit unexpectedly (exit code $($process.ExitCode))."
        }
        if ([CorePilotDialogProbe]::HasErrorDialog([uint32]$process.Id)) {
            throw 'CorePilot displayed a startup error dialog.'
        }
    }
    Write-Host "PASS: CorePilot running without an error dialog (PID $($process.Id))."
}
catch {
    if (Test-Path $log) {
        Write-Host 'Recent CorePilot diagnostic log:'
        Get-Content $log -Tail 60
    }
    throw
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $process.Dispose()
    }
}
