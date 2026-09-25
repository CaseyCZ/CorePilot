using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed record MacOSUsbPhysicalWriteResult(
    string TranscriptPath,
    string TranscriptSha256,
    int TargetDiskIndex,
    string DriveLetter,
    int VerifiedFiles,
    long BytesWritten);

public sealed class MacOSUsbPhysicalWriteService
{
    private readonly InstallerManifestService _manifestService = new();
    private readonly MacOSUsbWritePlanService _planService = new();
    private readonly MacOSUsbTypedConfirmationService _confirmationService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<MacOSUsbPhysicalWriteResult> WriteAsync(
        OpCoreStagingResult stage,
        OpCoreBuildResult efiBuild,
        AppleRecoveryResult recovery,
        InstallerManifestResult manifest,
        MacOSUsbWritePlanResult plan,
        MacOSUsbExecutionPreflightResult preflight,
        MacOSUsbTypedConfirmationResult confirmation,
        UsbTargetSafetyReport freshTarget,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Physical USB writing is currently implemented only on Windows.");

        if (freshTarget.IsBlocked ||
            !freshTarget.IsUsb ||
            freshTarget.DiskIndex < 0 ||
            string.IsNullOrWhiteSpace(freshTarget.IdentityFingerprint))
            throw new InvalidOperationException(
                "The current USB target is not safe enough for physical writing.");

        var now = DateTimeOffset.UtcNow;
        var confirmationVerification = await _confirmationService.VerifyAsync(
            confirmation,
            stage,
            manifest,
            plan,
            preflight,
            freshTarget,
            now,
            cancellationToken);

        if (!confirmationVerification.Success)
            throw new InvalidOperationException(
                "Typed confirmation no longer authorizes this target: " +
                string.Join("; ", confirmationVerification.Errors.Take(4)));

        var planVerification = await _planService.VerifyAsync(
            plan.PlanPath,
            stage,
            manifest,
            freshTarget,
            cancellationToken);

        if (!planVerification.Success)
            throw new InvalidOperationException(
                "USB write plan no longer verifies: " +
                string.Join("; ", planVerification.Errors.Take(4)));

        var manifestVerification = await _manifestService.VerifyAsync(
            manifest.ManifestPath,
            stage.WorkspaceDirectory,
            cancellationToken);

        if (!manifestVerification.Success)
            throw new InvalidOperationException(
                "Installer manifest no longer verifies: " +
                string.Join("; ", manifestVerification.Errors.Take(4)));

        var planDocument = JsonSerializer.Deserialize<MacOSUsbWritePlanDocument>(
            await File.ReadAllTextAsync(plan.PlanPath, cancellationToken),
            JsonOptions)
            ?? throw new InvalidOperationException("USB write plan could not be parsed.");

        if (!planDocument.DryRunOnly ||
            planDocument.TargetDiskIndex != freshTarget.DiskIndex ||
            !planDocument.TargetIdentityFingerprint.Equals(
                freshTarget.IdentityFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The write plan is not bound to the currently inspected target.");

        if (!Directory.Exists(efiBuild.EfiDirectory))
            throw new DirectoryNotFoundException("Verified EFI directory is missing.");

        if (!File.Exists(recovery.DmgPath) || !File.Exists(recovery.ChunklistPath))
            throw new FileNotFoundException("Verified Apple Recovery payload is missing.");

        progress?.Report("Selecting a temporary drive letter…");
        var driveLetter = ChooseFreeDriveLetter();

        var scriptPath = Path.Combine(
            stage.WorkspaceDirectory,
            "CorePilot-diskpart.txt");

        var partitionMiB = planDocument.PlannedFat32PartitionBytes / (1024L * 1024L);
        var script = string.Join(
            Environment.NewLine,
            $"select disk {freshTarget.DiskIndex}",
            "clean",
            "convert gpt",
            $"create partition primary size={partitionMiB}",
            "format fs=fat32 quick label=EFI",
            $"assign letter={driveLetter}",
            "exit",
            "");

        await File.WriteAllTextAsync(
            scriptPath,
            script,
            Encoding.ASCII,
            cancellationToken);

        progress?.Report(
            $"Windows will request administrator approval to erase physical disk {freshTarget.DiskIndex}.");

        await RunDiskPartElevatedAsync(scriptPath, cancellationToken);

        var root = $"{driveLetter}:\\";
        if (!Directory.Exists(root))
            throw new InvalidOperationException(
                $"The formatted target volume {root} was not mounted.");

        progress?.Report("Copying verified EFI files…");
        var verifiedFiles = 0;
        long bytesWritten = 0;

        var efiDestination = Path.Combine(root, "EFI");
        var efiCopy = await CopyAndVerifyTreeAsync(
            efiBuild.EfiDirectory,
            efiDestination,
            cancellationToken);
        verifiedFiles += efiCopy.Files;
        bytesWritten += efiCopy.Bytes;

        progress?.Report("Copying verified Apple Recovery files…");
        var recoveryDestination = Path.Combine(root, "com.apple.recovery.boot");
        Directory.CreateDirectory(recoveryDestination);

        foreach (var source in new[] { recovery.DmgPath, recovery.ChunklistPath })
        {
            var destination = Path.Combine(
                recoveryDestination,
                Path.GetFileName(source));

            await CopyAndVerifyFileAsync(source, destination, cancellationToken);
            verifiedFiles++;
            bytesWritten += new FileInfo(source).Length;
        }

        progress?.Report("Re-verifying source manifest after the physical write…");
        var finalManifestVerification = await _manifestService.VerifyAsync(
            manifest.ManifestPath,
            stage.WorkspaceDirectory,
            cancellationToken);

        if (!finalManifestVerification.Success)
            throw new InvalidOperationException(
                "Source manifest changed during USB writing: " +
                string.Join("; ", finalManifestVerification.Errors.Take(4)));

        var transcript = new
        {
            schemaVersion = 1,
            completedAt = DateTimeOffset.UtcNow,
            targetDiskIndex = freshTarget.DiskIndex,
            targetIdentityFingerprint = freshTarget.IdentityFingerprint,
            targetModel = freshTarget.Model,
            targetSizeBytes = freshTarget.SizeBytes,
            driveLetter = $"{driveLetter}:",
            installerManifestSha256 = manifest.ManifestSha256,
            usbWritePlanSha256 = plan.PlanSha256,
            typedConfirmationSha256 = confirmation.ConfirmationSha256,
            efiDirectory = efiBuild.EfiDirectory,
            recoveryDmgSha256 = recovery.DmgSha256,
            recoveryChunklistSha256 = recovery.ChunklistSha256,
            verifiedFiles,
            bytesWritten,
            physicalWriteCompleted = true
        };

        var transcriptPath = Path.Combine(
            stage.WorkspaceDirectory,
            "CorePilotUsbPhysicalWrite.json");

        await File.WriteAllTextAsync(
            transcriptPath,
            JsonSerializer.Serialize(transcript, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        var transcriptSha = await ComputeSha256Async(
            transcriptPath,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(stage.WorkspaceDirectory, "CorePilotUsbPhysicalWrite.sha256"),
            $"{transcriptSha}  {Path.GetFileName(transcriptPath)}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        return new(
            transcriptPath,
            transcriptSha,
            freshTarget.DiskIndex,
            $"{driveLetter}:",
            verifiedFiles,
            bytesWritten);
    }

    private static async Task RunDiskPartElevatedAsync(
        string scriptPath,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "diskpart.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add(scriptPath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start diskpart.");

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"diskpart failed with exit code {process.ExitCode}.");
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
            "No free drive letter is available for the temporary installer volume.");
    }

    private static async Task<(int Files, long Bytes)> CopyAndVerifyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var files = 0;
        long bytes = 0;

        foreach (var source in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceRoot, source);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe EFI relative path.");

            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyAndVerifyFileAsync(source, destination, cancellationToken);
            files++;
            bytes += new FileInfo(source).Length;
        }

        return (files, bytes);
    }

    private static async Task CopyAndVerifyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists)
            throw new FileNotFoundException("Source file disappeared during writing.", source);

        await using (var input = new FileStream(
                         source,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
                         destination,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        var destinationInfo = new FileInfo(destination);
        if (destinationInfo.Length != sourceInfo.Length)
            throw new InvalidOperationException(
                $"Copied file size mismatch: {destination}");

        var sourceSha = await ComputeSha256Async(source, cancellationToken);
        var destinationSha = await ComputeSha256Async(destination, cancellationToken);

        if (!sourceSha.Equals(destinationSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Copied file SHA-256 mismatch: {destination}");
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
