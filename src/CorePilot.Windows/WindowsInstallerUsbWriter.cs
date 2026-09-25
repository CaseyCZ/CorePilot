using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.Windows;

public sealed class WindowsInstallerUsbWriter
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
                "Windows installer USB writing currently runs on Windows.");

        if (!image.SystemId.Equals("windows", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The prepared image is not a Windows installation image.");

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
                "Prepared Windows ISO is missing.",
                image.IsoPath);

        progress?.Report("Re-verifying the prepared Windows ISO before USB erase…");
        var actualIsoSha = await ComputeSha256Async(
            image.IsoPath,
            cancellationToken);

        if (!actualIsoSha.Equals(
                image.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Prepared Windows ISO changed after Verify.");

        var isoLength = new FileInfo(image.IsoPath).Length;
        if (freshTarget.SizeBytes < isoLength + 1024L * 1024L * 1024L)
            throw new InvalidOperationException(
                "Selected USB disk does not have enough capacity for the Windows installer.");

        var root = Path.GetDirectoryName(image.ManifestPath)
            ?? throw new InvalidOperationException(
                "Prepared Windows media workspace is invalid.");

        Directory.CreateDirectory(root);

        var driveLetter = ChooseFreeDriveLetter();
        var partitionMiB = Math.Min(
            32700L,
            Math.Max(
                0,
                freshTarget.SizeBytes / 1024L / 1024L - 64L));

        if (partitionMiB < 7000)
            throw new InvalidOperationException(
                "Windows installer USB requires at least about 8 GB of usable target capacity.");

        var diskPartPath = Path.Combine(
            root,
            "CorePilot-Windows-diskpart.txt");

        var resultPath = Path.Combine(
            root,
            "CorePilot-Windows-UsbWrite-Result.json");

        var scriptPath = Path.Combine(
            root,
            "CorePilot-Windows-UsbWrite.ps1");

        var diskPart = string.Join(
            Environment.NewLine,
            $"select disk {freshTarget.DiskIndex}",
            "clean",
            "convert gpt",
            $"create partition primary size={partitionMiB}",
            "format fs=fat32 quick label=COREPILOT",
            $"assign letter={driveLetter}",
            "exit",
            "");

        await File.WriteAllTextAsync(
            diskPartPath,
            diskPart,
            Encoding.ASCII,
            cancellationToken);

        await File.WriteAllTextAsync(
            scriptPath,
            BuildElevatedScript(),
            new UTF8Encoding(false),
            cancellationToken);

        if (File.Exists(resultPath))
            File.Delete(resultPath);

        progress?.Report(
            $"Windows will request administrator approval to erase physical disk {freshTarget.DiskIndex} and create the installer.");

        await RunElevatedAsync(
            scriptPath,
            diskPartPath,
            resultPath,
            image,
            freshTarget,
            driveLetter,
            cancellationToken);

        if (!File.Exists(resultPath))
            throw new InvalidOperationException(
                "Windows USB writer did not produce a completion result.");

        using var resultJson = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                resultPath,
                cancellationToken));

        var resultRoot = resultJson.RootElement;

        var completed =
            resultRoot.TryGetProperty("completed", out var completedNode) &&
            completedNode.ValueKind == JsonValueKind.True;

        if (!completed)
            throw new InvalidOperationException(
                "Windows USB writer did not report successful completion.");

        var writtenDrive = resultRoot.GetProperty("driveLetter").GetString()
            ?? $"{driveLetter}:";

        await VerifyMountedVolumeBelongsToTargetAsync(
            driveLetter,
            freshTarget.DiskIndex,
            cancellationToken);

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
            driveLetter = writtenDrive,
            sourceIso = image.IsoPath,
            sourceIsoSha256 = image.Sha256,
            sourceUrl = image.SourceUrl,
            sourceManifest = image.ManifestPath,
            splitInstallWim =
                resultRoot.TryGetProperty("splitInstallWim", out var splitNode) &&
                splitNode.ValueKind == JsonValueKind.True,
            verifiedBootFiles =
                resultRoot.TryGetProperty("verifiedBootFiles", out var verifyNode)
                    ? verifyNode.GetInt32()
                    : 0,
            physicalWriteCompleted = true
        };

        var transcriptPath = Path.Combine(
            root,
            "CorePilot-Windows-UsbWrite.json");

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
            "Windows installer USB written and verified successfully.");

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
    [Parameter(Mandatory=$true)][string]$DiskPartScriptB64,
    [Parameter(Mandatory=$true)][string]$ResultPathB64,
    [Parameter(Mandatory=$true)][string]$DriveLetter
)

$ErrorActionPreference = 'Stop'

function Decode([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($value))
}

function Assert-SameFile([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing ISO source file: $source" }
    if (-not (Test-Path -LiteralPath $destination)) { throw "Missing USB destination file: $destination" }

    $sourceInfo = Get-Item -LiteralPath $source
    $destinationInfo = Get-Item -LiteralPath $destination
    if ($sourceInfo.Length -ne $destinationInfo.Length) {
        throw "Copied file size mismatch: $destination"
    }

    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if (-not [string]::Equals($sourceHash, $destinationHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Copied file SHA-256 mismatch: $destination"
    }
}

$expectedDeviceId = Decode $ExpectedDeviceIdB64
$expectedPnp = Decode $ExpectedPnpB64
$expectedSerial = Decode $ExpectedSerialB64
$isoPath = Decode $IsoPathB64
$diskPartScript = Decode $DiskPartScriptB64
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
    throw "Prepared Windows ISO disappeared before writing."
}

$mounted = $null
try {
    $mounted = Mount-DiskImage -ImagePath $isoPath -StorageType ISO -Access ReadOnly -PassThru
    $sourceVolume = $mounted | Get-Volume
    $sourceLetter = [string]$sourceVolume.DriveLetter
    if ([string]::IsNullOrWhiteSpace($sourceLetter)) {
        throw "Mounted Windows ISO has no drive letter."
    }

    & diskpart.exe /s $diskPartScript
    if ($LASTEXITCODE -ne 0) {
        throw "diskpart failed with exit code $LASTEXITCODE."
    }

    $diskAfterFormat = Get-CimInstance Win32_DiskDrive -Filter "Index = $DiskIndex"
    if ($null -eq $diskAfterFormat) { throw "Target disk disappeared after formatting." }
    if (-not [string]::Equals([string]$diskAfterFormat.DeviceID, $expectedDeviceId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target DeviceID changed after formatting."
    }
    if (-not [string]::IsNullOrWhiteSpace($expectedPnp) -and
        -not [string]::Equals([string]$diskAfterFormat.PNPDeviceID, $expectedPnp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target PNP identity changed after formatting."
    }
    if ([Int64]$diskAfterFormat.Size -ne $ExpectedSize) {
        throw "Target size changed after formatting."
    }
    $actualSerialAfterFormat = ([string]$diskAfterFormat.SerialNumber).Trim()
    if (-not [string]::IsNullOrWhiteSpace($expectedSerial) -and
        -not [string]::IsNullOrWhiteSpace($actualSerialAfterFormat) -and
        -not [string]::Equals($actualSerialAfterFormat, $expectedSerial.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target serial changed after formatting."
    }

    $destinationDisk = (Get-Partition -DriveLetter $DriveLetter -ErrorAction Stop | Get-Disk -ErrorAction Stop).Number
    if ([int]$destinationDisk -ne $DiskIndex) {
        throw "Formatted Windows installer volume does not belong to the expected physical disk."
    }

    $sourceRoot = $sourceLetter + ":\\"
    $destinationRoot = $DriveLetter + ":\\"

    & robocopy.exe $sourceRoot $destinationRoot /E /R:1 /W:1 /NFL /NDL /NP /XF install.wim
    if ($LASTEXITCODE -gt 7) {
        throw "robocopy failed with exit code $LASTEXITCODE."
    }

    $sourceWim = Join-Path $sourceRoot "sources\\install.wim"
    $destinationSources = Join-Path $destinationRoot "sources"
    New-Item -ItemType Directory -Force -Path $destinationSources | Out-Null

    $splitInstallWim = $false
    if (Test-Path -LiteralPath $sourceWim) {
        $wimInfo = Get-Item -LiteralPath $sourceWim
        if ($wimInfo.Length -gt 4000000000) {
            $splitInstallWim = $true
            $swmPath = Join-Path $destinationSources "install.swm"
            & dism.exe /Split-Image /ImageFile:$sourceWim /SWMFile:$swmPath /FileSize:3800
            if ($LASTEXITCODE -ne 0) {
                throw "DISM failed to split install.wim with exit code $LASTEXITCODE."
            }
            if (-not (Test-Path -LiteralPath $swmPath)) {
                throw "DISM reported success but install.swm is missing."
            }
        }
        else {
            Copy-Item -LiteralPath $sourceWim -Destination (Join-Path $destinationSources "install.wim") -Force
            Assert-SameFile $sourceWim (Join-Path $destinationSources "install.wim")
        }
    }

    $sourceEsd = Join-Path $sourceRoot "sources\\install.esd"
    if (Test-Path -LiteralPath $sourceEsd) {
        $esdInfo = Get-Item -LiteralPath $sourceEsd
        if ($esdInfo.Length -gt 4290000000) {
            throw "install.esd exceeds FAT32 file size and cannot be safely split by this writer."
        }
        Assert-SameFile $sourceEsd (Join-Path $destinationSources "install.esd")
    }

    $critical = @(
        "efi\\boot\\bootx64.efi",
        "sources\\boot.wim"
    )

    $bootMgr = Join-Path $sourceRoot "bootmgr"
    if (Test-Path -LiteralPath $bootMgr) {
        $critical += "bootmgr"
    }

    $verified = 0
    foreach ($relative in $critical) {
        Assert-SameFile (Join-Path $sourceRoot $relative) (Join-Path $destinationRoot $relative)
        $verified++
    }

    [ordered]@{
        completed = $true
        diskIndex = $DiskIndex
        driveLetter = $DriveLetter + ":"
        splitInstallWim = $splitInstallWim
        verifiedBootFiles = $verified
    } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
finally {
    if ($null -ne $mounted) {
        Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
    }
}
""";

    private static async Task RunElevatedAsync(
        string scriptPath,
        string diskPartPath,
        string resultPath,
        PreparedIsoImage image,
        UsbTargetSafetyReport target,
        char driveLetter,
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
            "-DiskPartScriptB64",
            B64(diskPartPath),
            "-ResultPathB64",
            B64(resultPath),
            "-DriveLetter",
            driveLetter.ToString()
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Unable to start the elevated Windows USB writer.");

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Elevated Windows USB writer stopped with exit code {process.ExitCode}.");
    }

    private static async Task VerifyMountedVolumeBelongsToTargetAsync(
        char driveLetter,
        int targetDiskIndex,
        CancellationToken cancellationToken)
    {
        var command =
            $"(Get-Partition -DriveLetter '{driveLetter}' -ErrorAction Stop | Get-Disk -ErrorAction Stop).Number";

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Unable to verify the Windows installer USB volume.");

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask =
            process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0 ||
            !int.TryParse(stdout, out var mountedDisk) ||
            mountedDisk != targetDiskIndex)
            throw new InvalidOperationException(
                $"Mounted Windows installer volume verification failed. Expected disk {targetDiskIndex}; returned '{stdout}'. {stderr}".Trim());
    }

    private static char ChooseFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(x => char.ToUpperInvariant(x.Name[0]))
            .ToHashSet();

        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
                return letter;
        }

        throw new InvalidOperationException(
            "No free drive letter is available for the Windows installer USB.");
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
