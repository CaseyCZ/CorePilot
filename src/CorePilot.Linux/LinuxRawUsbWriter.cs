using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.Linux;

public sealed class LinuxRawUsbWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string RequiredConfirmationPhrase(
        UsbTargetSafetyReport target,
        PreparedIsoImage image)
    {
        var fingerprint = target.IdentityFingerprint.Length > 12
            ? target.IdentityFingerprint[..12]
            : target.IdentityFingerprint;

        return $"ERASE DISK {target.DiskIndex} {fingerprint} AND WRITE {image.DisplayName}".ToUpperInvariant();
    }

    public async Task<GenericUsbWriteResult> WriteAsync(
        PreparedIsoImage image,
        UsbTargetSafetyReport freshTarget,
        string typedConfirmation,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Linux ISO USB writing currently runs from CorePilot on Windows.");

        if (!image.SystemId.Equals("linux", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The prepared image is not a Linux installation image.");

        if (!image.IntegrityVerified ||
            string.IsNullOrWhiteSpace(image.ExpectedSha256))
            throw new InvalidOperationException(
                "Linux ISO must have a verified official checksum before writing.");

        if (!freshTarget.IsUsb ||
            freshTarget.IsBlocked ||
            freshTarget.DiskIndex < 0 ||
            string.IsNullOrWhiteSpace(freshTarget.IdentityFingerprint))
            throw new InvalidOperationException(
                "The selected target is not a safe writable USB disk.");

        var expectedPhrase = RequiredConfirmationPhrase(
            freshTarget,
            image);

        if (!typedConfirmation.Equals(
                expectedPhrase,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Exact destructive confirmation phrase was not entered.");

        if (!File.Exists(image.IsoPath))
            throw new FileNotFoundException(
                "Prepared Linux ISO is missing.",
                image.IsoPath);

        progress?.Report("Re-verifying the Linux ISO against its prepared SHA-256…");

        var actualIsoSha = await ComputeSha256Async(
            image.IsoPath,
            cancellationToken);

        if (!actualIsoSha.Equals(
                image.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !actualIsoSha.Equals(
                image.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Prepared Linux ISO no longer matches the verified official checksum.");

        var isoLength = new FileInfo(image.IsoPath).Length;
        if (freshTarget.SizeBytes < isoLength + 64L * 1024L * 1024L)
            throw new InvalidOperationException(
                "Selected USB disk is smaller than the prepared Linux image.");

        var root = Path.GetDirectoryName(image.ManifestPath)
            ?? throw new InvalidOperationException(
                "Prepared Linux media workspace is invalid.");

        Directory.CreateDirectory(root);

        var scriptPath = Path.Combine(
            root,
            "CorePilot-Linux-RawUsbWrite.ps1");

        var resultPath = Path.Combine(
            root,
            "CorePilot-Linux-RawUsbWrite-Result.json");

        await File.WriteAllTextAsync(
            scriptPath,
            BuildElevatedScript(),
            new UTF8Encoding(false),
            cancellationToken);

        if (File.Exists(resultPath))
            File.Delete(resultPath);

        progress?.Report(
            $"Windows will request administrator approval to erase physical disk {freshTarget.DiskIndex} and raw-write the verified Linux ISO.");

        await RunElevatedAsync(
            scriptPath,
            resultPath,
            image,
            freshTarget,
            cancellationToken);

        if (!File.Exists(resultPath))
            throw new InvalidOperationException(
                "Linux raw USB writer did not produce a completion result.");

        using var resultJson = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                resultPath,
                cancellationToken));

        var resultRoot = resultJson.RootElement;

        var completed =
            resultRoot.TryGetProperty("completed", out var completedNode) &&
            completedNode.ValueKind == JsonValueKind.True;

        var readBackSha =
            resultRoot.TryGetProperty("readBackSha256", out var hashNode)
                ? hashNode.GetString()
                : null;

        if (!completed ||
            string.IsNullOrWhiteSpace(readBackSha) ||
            !readBackSha.Equals(
                image.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Linux USB read-back verification did not match the prepared ISO.");

        var transcript = new
        {
            schemaVersion = 1,
            completedAt = DateTimeOffset.UtcNow,
            system = image.SystemId,
            target = image.TargetId,
            targetDiskIndex = freshTarget.DiskIndex,
            targetIdentityFingerprint = freshTarget.IdentityFingerprint,
            targetModel = freshTarget.Model,
            targetSizeBytes = freshTarget.SizeBytes,
            sourceIso = image.IsoPath,
            sourceIsoSha256 = image.Sha256,
            expectedIsoSha256 = image.ExpectedSha256,
            sourceUrl = image.SourceUrl,
            sourceManifest = image.ManifestPath,
            readBackSha256 = readBackSha,
            bytesWritten = isoLength,
            physicalWriteCompleted = true
        };

        var transcriptPath = Path.Combine(
            root,
            "CorePilot-Linux-UsbWrite.json");

        await File.WriteAllTextAsync(
            transcriptPath,
            JsonSerializer.Serialize(transcript, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        var transcriptSha = await ComputeSha256Async(
            transcriptPath,
            cancellationToken);

        await File.WriteAllTextAsync(
            transcriptPath + ".sha256",
            $"{transcriptSha}  {Path.GetFileName(transcriptPath)}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        progress?.Report(
            "Linux installer USB raw-write and full image read-back verification completed.");

        return new(
            image.SystemId,
            image.TargetId,
            freshTarget.DiskIndex,
            freshTarget.IdentityFingerprint,
            transcriptPath,
            transcriptSha,
            isoLength,
            Verified: true);
    }

    private static string BuildElevatedScript() => """
param(
    [Parameter(Mandatory=$true)][int]$DiskIndex,
    [Parameter(Mandatory=$true)][string]$ExpectedDeviceIdB64,
    [Parameter(Mandatory=$true)][string]$ExpectedPnpB64,
    [Parameter(Mandatory=$true)][Int64]$ExpectedSize,
    [Parameter(Mandatory=$true)][string]$ExpectedSerialB64,
    [Parameter(Mandatory=$true)][string]$IsoPathB64,
    [Parameter(Mandatory=$true)][string]$ExpectedIsoSha256,
    [Parameter(Mandatory=$true)][string]$ResultPathB64
)

$ErrorActionPreference = 'Stop'

function Decode([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($value))
}

$expectedDeviceId = Decode $ExpectedDeviceIdB64
$expectedPnp = Decode $ExpectedPnpB64
$expectedSerial = Decode $ExpectedSerialB64
$isoPath = Decode $IsoPathB64
$resultPath = Decode $ResultPathB64

$disk = Get-CimInstance Win32_DiskDrive -Filter "Index = $DiskIndex"
if ($null -eq $disk) { throw "Target disk disappeared before erase." }

if (-not [string]::Equals([string]$disk.DeviceID, $expectedDeviceId, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target DeviceID changed before erase."
}

if (-not [string]::IsNullOrWhiteSpace($expectedPnp) -and
    -not [string]::Equals([string]$disk.PNPDeviceID, $expectedPnp, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target PNP identity changed before erase."
}

if ([Int64]$disk.Size -ne $ExpectedSize) {
    throw "Target size changed before erase."
}

$actualSerial = ([string]$disk.SerialNumber).Trim()
if (-not [string]::IsNullOrWhiteSpace($expectedSerial) -and
    -not [string]::IsNullOrWhiteSpace($actualSerial) -and
    -not [string]::Equals($actualSerial, $expectedSerial.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target serial changed before erase."
}

if (-not (Test-Path -LiteralPath $isoPath)) {
    throw "Prepared Linux ISO disappeared before writing."
}

$sourceHash = (Get-FileHash -LiteralPath $isoPath -Algorithm SHA256).Hash
if (-not [string]::Equals($sourceHash, $ExpectedIsoSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Prepared Linux ISO failed SHA-256 verification inside the elevated write boundary."
}

$isoLength = (Get-Item -LiteralPath $isoPath).Length
if ($isoLength -ge $ExpectedSize) {
    throw "Prepared Linux ISO is not smaller than the target disk."
}

Get-Partition -DiskNumber $DiskIndex -ErrorAction SilentlyContinue |
    Where-Object { $_.DriveLetter } |
    ForEach-Object {
        & mountvol.exe ($_.DriveLetter.ToString() + ":") /p | Out-Null
    }

$storageDisk = Get-Disk -Number $DiskIndex -ErrorAction Stop
if ($storageDisk.IsReadOnly) {
    Set-Disk -Number $DiskIndex -IsReadOnly $false
}
if ($storageDisk.IsOffline) {
    Set-Disk -Number $DiskIndex -IsOffline $false
}

Clear-Disk -Number $DiskIndex -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop

$diskAfterClear = Get-CimInstance Win32_DiskDrive -Filter "Index = $DiskIndex"
if ($null -eq $diskAfterClear) { throw "Target disk disappeared after erase preparation." }
if (-not [string]::Equals([string]$diskAfterClear.DeviceID, $expectedDeviceId, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target DeviceID changed after erase preparation."
}
if (-not [string]::IsNullOrWhiteSpace($expectedPnp) -and
    -not [string]::Equals([string]$diskAfterClear.PNPDeviceID, $expectedPnp, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target PNP identity changed after erase preparation."
}
if ([Int64]$diskAfterClear.Size -ne $ExpectedSize) {
    throw "Target size changed after erase preparation."
}
$actualSerialAfterClear = ([string]$diskAfterClear.SerialNumber).Trim()
if (-not [string]::IsNullOrWhiteSpace($expectedSerial) -and
    -not [string]::IsNullOrWhiteSpace($actualSerialAfterClear) -and
    -not [string]::Equals($actualSerialAfterClear, $expectedSerial.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target serial changed after erase preparation."
}

$buffer = New-Object byte[] (4MB)
$input = [IO.File]::Open($isoPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$output = [IO.File]::Open($expectedDeviceId, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)

try {
    $output.Position = 0
    $total = 0L

    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $output.Write($buffer, 0, $read)
        $total += $read
    }

    if ($total -ne $isoLength) {
        throw "Raw write byte count mismatch."
    }

    $output.Flush($true)

    $output.Position = 0
    $remaining = [Int64]$isoLength
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)

    try {
        while ($remaining -gt 0) {
            $toRead = [int][Math]::Min([Int64]$buffer.Length, $remaining)
            $read = $output.Read($buffer, 0, $toRead)
            if ($read -le 0) {
                throw "Target ended before full ISO read-back verification completed."
            }

            $hasher.AppendData($buffer, 0, $read)
            $remaining -= $read
        }

        $readBackSha = [Convert]::ToHexString($hasher.GetHashAndReset())
    }
    finally {
        $hasher.Dispose()
    }

    if (-not [string]::Equals($readBackSha, $ExpectedIsoSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Raw USB read-back SHA-256 does not match the prepared ISO."
    }

    [ordered]@{
        completed = $true
        diskIndex = $DiskIndex
        bytesWritten = $isoLength
        readBackSha256 = $readBackSha
    } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
finally {
    $input.Dispose()
    $output.Dispose()
}

Update-HostStorageCache -ErrorAction SilentlyContinue
""";

    private static async Task RunElevatedAsync(
        string scriptPath,
        string resultPath,
        PreparedIsoImage image,
        UsbTargetSafetyReport target,
        CancellationToken cancellationToken)
    {
        static string B64(string value) =>
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? ""));

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal
        };

        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-DiskIndex",
            target.DiskIndex.ToString(),
            "-ExpectedDeviceIdB64",
            B64(target.DeviceId),
            "-ExpectedPnpB64",
            B64(target.PnpDeviceId),
            "-ExpectedSize",
            target.SizeBytes.ToString(),
            "-ExpectedSerialB64",
            B64(target.SerialNumber),
            "-IsoPathB64",
            B64(image.IsoPath),
            "-ExpectedIsoSha256",
            image.Sha256,
            "-ResultPathB64",
            B64(resultPath)
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Unable to start the elevated Linux raw USB writer.");

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Elevated Linux raw USB writer stopped with exit code {process.ExitCode}.");
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(
            stream,
            cancellationToken);

        return Convert.ToHexString(hash);
    }
}
