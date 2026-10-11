using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed record MacOSUsbWritePlanAction(
    int Order,
    string Phase,
    string Description,
    bool Destructive,
    string CommandPreview = "");

public sealed record MacOSUsbWritePlanDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    bool DryRunOnly,
    string LayoutReference,
    string InstallerManifestPath,
    string InstallerManifestSha256,
    string TargetDeviceId,
    int TargetDiskIndex,
    string TargetModel,
    string TargetSerialNumber,
    long TargetSizeBytes,
    string TargetIdentityFingerprint,
    string TargetSafetyLevel,
    bool RequiresStrongConfirmation,
    string FutureConfirmationPhrase,
    string PartitionStyle,
    int PartitionCount,
    string FileSystem,
    string VolumeLabel,
    long PlannedFat32PartitionBytes,
    long RequiredPayloadBytes,
    long InstallerManifestPayloadBytes,
    IReadOnlyList<MacOSUsbWritePlanAction> Actions);

public sealed record MacOSUsbWritePlanResult(
    string PlanPath,
    string Sha256Path,
    string PlanSha256,
    int ActionCount,
    long PlannedFat32PartitionBytes,
    bool RequiresStrongConfirmation,
    string FutureConfirmationPhrase);

public sealed record MacOSUsbWritePlanVerificationResult(
    bool Success,
    IReadOnlyList<string> Errors);

/// <summary>
/// Creates and verifies a dry-run description of the future destructive USB operation.
/// This service never opens a raw disk, invokes diskpart, formats, dismounts or writes volumes.
/// </summary>
public sealed class MacOSUsbWritePlanService
{
    private const string PlanName = "CorePilotUsbWritePlan.json";
    private const string ShaName = "CorePilotUsbWritePlan.sha256";

    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;
    private const long WindowsFat32PracticalLimit = 32L * GiB;

    private readonly InstallerManifestService _manifestService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<MacOSUsbWritePlanResult> CreateDryRunAsync(
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        UsbTargetSafetyReport target,
        CancellationToken cancellationToken = default)
    {
        if (target.IsBlocked)
            throw new InvalidOperationException(
                $"USB target is {target.LevelText} and cannot receive a write plan.");

        if (!target.IsUsb || target.DiskIndex < 0 || target.SizeBytes <= 0)
            throw new InvalidOperationException(
                "USB target identity is incomplete, so CorePilot fails closed.");

        if (string.IsNullOrWhiteSpace(target.IdentityFingerprint))
            throw new InvalidOperationException(
                "USB target fingerprint is missing.");

        var manifestVerification = await _manifestService.VerifyAsync(
            manifest.ManifestPath,
            stage.WorkspaceDirectory,
            cancellationToken);

        if (!manifestVerification.Success)
            throw new InvalidOperationException(
                "Installer manifest must verify before creating a USB plan: " +
                string.Join("; ", manifestVerification.Errors.Take(4)));

        var actualManifestSha = await ComputeSha256Async(
            manifest.ManifestPath,
            cancellationToken);

        if (!actualManifestSha.Equals(
                manifest.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Installer manifest SHA-256 changed after verification.");

        var requiredPayload = Math.Max(
            2L * GiB,
            checked(manifest.TotalBytes + 256L * MiB));

        var availableForPartition = target.SizeBytes - 16L * MiB;
        if (availableForPartition < requiredPayload)
            throw new InvalidOperationException(
                $"USB target is too small. Required at least {FormatBytes(requiredPayload)}.");

        var desiredPartition = RoundUpToGiB(
            Math.Max(requiredPayload, 4L * GiB));

        long plannedPartition;

        if (target.SizeBytes <= WindowsFat32PracticalLimit)
        {
            plannedPartition = availableForPartition;
        }
        else
        {
            plannedPartition = Math.Min(
                availableForPartition,
                desiredPartition);
        }

        if (plannedPartition < requiredPayload)
            throw new InvalidOperationException(
                "Unable to create a FAT32 layout large enough for the verified installer payload.");

        if (plannedPartition >= WindowsFat32PracticalLimit)
            throw new InvalidOperationException(
                "Planned FAT32 partition would exceed the Windows built-in FAT32 formatting limit. " +
                "CorePilot refuses to generate an executable-style plan until a dedicated FAT32 formatter is integrated.");

        var requiresStrongConfirmation =
            target.Level == UsbTargetSafetyLevel.StrongConfirmation;

        var shortFingerprint = target.IdentityFingerprint.Length >= 12
            ? target.IdentityFingerprint[..12]
            : target.IdentityFingerprint;

        var confirmationPhrase =
            $"ERASE DISK {target.DiskIndex} {shortFingerprint}";

        var partitionMiB = plannedPartition / MiB;

        var actions = new List<MacOSUsbWritePlanAction>
        {
            new(
                1,
                "Preflight",
                "Re-inspect the selected physical disk immediately before execution and require an exact identity-fingerprint match.",
                false),
            new(
                2,
                "Preflight",
                "Re-read and re-hash CorePilotInstallerManifest.json and every referenced EFI/recovery file.",
                false),
            new(
                3,
                "Confirmation",
                $"Require the user to type the exact phrase: {confirmationPhrase}",
                false),
            new(
                4,
                "Target lock",
                "Dismount only volumes that belong to the already-verified target disk.",
                true),
            new(
                5,
                "Partition table",
                "Erase the selected target partition table.",
                true,
                $"select disk {target.DiskIndex} ; clean"),
            new(
                6,
                "Partition table",
                "Initialize the selected target as GPT.",
                true,
                "convert gpt"),
            new(
                7,
                "Partition",
                $"Create one primary FAT32 installer partition of approximately {partitionMiB} MiB.",
                true,
                $"create partition primary size={partitionMiB}"),
            new(
                8,
                "Filesystem",
                "Quick-format the installer partition as FAT32 with volume label EFI.",
                true,
                "format fs=fat32 quick label=EFI"),
            new(
                9,
                "Mount",
                "Assign a temporary Windows drive letter chosen at execution time.",
                false,
                "assign"),
            new(
                10,
                "Copy",
                "Copy the manifest-verified EFI/ tree to the root of the FAT32 partition.",
                false),
            new(
                11,
                "Copy",
                "Create com.apple.recovery.boot/ at the root of the FAT32 partition.",
                false),
            new(
                12,
                "Copy",
                "Copy the manifest-verified BaseSystem/RecoveryImage .dmg and .chunklist into com.apple.recovery.boot/.",
                false),
            new(
                13,
                "Verification",
                "Re-hash all copied files on the USB and compare them with CorePilotInstallerManifest.json.",
                false),
            new(
                14,
                "Finish",
                "Flush pending writes, remove the temporary drive-letter assignment when appropriate, and safely release the target.",
                false)
        };

        var document = new MacOSUsbWritePlanDocument(
            SchemaVersion: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            DryRunOnly: true,
            LayoutReference: "Dortania OpenCore Install Guide: Windows recovery USB (GPT + primary FAT32 + EFI/ + com.apple.recovery.boot/)",
            InstallerManifestPath: manifest.ManifestPath,
            InstallerManifestSha256: manifest.ManifestSha256,
            TargetDeviceId: target.DeviceId,
            TargetDiskIndex: target.DiskIndex,
            TargetModel: target.Model,
            TargetSerialNumber: target.SerialNumber,
            TargetSizeBytes: target.SizeBytes,
            TargetIdentityFingerprint: target.IdentityFingerprint,
            TargetSafetyLevel: target.LevelText,
            RequiresStrongConfirmation: requiresStrongConfirmation,
            FutureConfirmationPhrase: confirmationPhrase,
            PartitionStyle: "GPT",
            PartitionCount: 1,
            FileSystem: "FAT32",
            VolumeLabel: "EFI",
            PlannedFat32PartitionBytes: plannedPartition,
            RequiredPayloadBytes: requiredPayload,
            InstallerManifestPayloadBytes: manifest.TotalBytes,
            Actions: actions);

        var planPath = Path.Combine(stage.WorkspaceDirectory, PlanName);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(
            planPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var planSha = await ComputeSha256Async(planPath, cancellationToken);
        var shaPath = Path.Combine(stage.WorkspaceDirectory, ShaName);

        await File.WriteAllTextAsync(
            shaPath,
            $"{planSha}  {PlanName}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        var verification = await VerifyAsync(
            planPath,
            stage,
            manifest,
            target,
            cancellationToken);

        if (!verification.Success)
            throw new InvalidOperationException(
                "Dry-run USB plan failed immediate verification: " +
                string.Join("; ", verification.Errors.Take(4)));

        return new(
            planPath,
            shaPath,
            planSha,
            actions.Count,
            plannedPartition,
            requiresStrongConfirmation,
            confirmationPhrase);
    }

    public async Task<MacOSUsbWritePlanVerificationResult> VerifyAsync(
        string planPath,
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        UsbTargetSafetyReport currentTarget,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!File.Exists(planPath))
            return new(false, ["USB write plan is missing."]);

        var expectedPlanPath = Path.GetFullPath(
            Path.Combine(stage.WorkspaceDirectory, PlanName));

        if (!Path.GetFullPath(planPath).Equals(
                expectedPlanPath,
                StringComparison.OrdinalIgnoreCase))
            return new(false, ["USB write plan is outside the expected workspace."]);

        var shaPath = Path.Combine(stage.WorkspaceDirectory, ShaName);
        if (!File.Exists(shaPath))
        {
            errors.Add("USB write-plan SHA-256 sidecar is missing.");
        }
        else
        {
            var expectedSha = (await File.ReadAllTextAsync(
                    shaPath,
                    cancellationToken))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            var actualSha = await ComputeSha256Async(planPath, cancellationToken);

            if (string.IsNullOrWhiteSpace(expectedSha) ||
                !actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                errors.Add("USB write-plan SHA-256 does not match its sidecar.");
        }

        MacOSUsbWritePlanDocument? plan;

        try
        {
            var json = await File.ReadAllTextAsync(planPath, cancellationToken);
            plan = JsonSerializer.Deserialize<MacOSUsbWritePlanDocument>(
                json,
                JsonOptions);
        }
        catch (Exception ex)
        {
            return new(false, [$"USB write-plan JSON is invalid: {ex.Message}"]);
        }

        if (plan is null)
            return new(false, ["USB write plan could not be parsed."]);

        if (plan.SchemaVersion != 1)
            errors.Add($"Unsupported USB write-plan schema: {plan.SchemaVersion}.");

        if (!plan.DryRunOnly)
            errors.Add("USB write plan is not marked DryRunOnly.");

        if (!plan.InstallerManifestSha256.Equals(
                manifest.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("Installer-manifest SHA-256 no longer matches the USB plan.");

        if (!plan.TargetIdentityFingerprint.Equals(
                currentTarget.IdentityFingerprint,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB target identity fingerprint changed.");

        if (plan.TargetDiskIndex != currentTarget.DiskIndex)
            errors.Add("USB physical disk number changed.");

        if (!plan.TargetDeviceId.Equals(
                currentTarget.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB physical device path changed.");

        if (plan.TargetSizeBytes != currentTarget.SizeBytes)
            errors.Add("USB target size changed.");

        if (currentTarget.IsBlocked)
            errors.Add($"Current USB safety state is {currentTarget.LevelText}.");

        var manifestVerification = await _manifestService.VerifyAsync(
            manifest.ManifestPath,
            stage.WorkspaceDirectory,
            cancellationToken);

        if (!manifestVerification.Success)
            errors.Add(
                "Installer manifest no longer verifies: " +
                string.Join("; ", manifestVerification.Errors.Take(2)));

        return new(errors.Count == 0, errors);
    }

    private static long RoundUpToGiB(long bytes)
    {
        var units = checked((bytes + GiB - 1) / GiB);
        return checked(units * GiB);
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / 1024d / 1024d / 1024d:0.##} GiB";

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
