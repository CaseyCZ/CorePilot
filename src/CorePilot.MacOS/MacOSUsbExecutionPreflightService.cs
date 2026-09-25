using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed record MacOSUsbExecutionPreflightDocument(
    int SchemaVersion,
    string PreflightId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool ReadyForConfirmationOnly,
    bool PhysicalDiskWritesEnabled,
    string TargetDeviceId,
    int TargetDiskIndex,
    string TargetIdentityFingerprint,
    string TargetSafetyLevel,
    string InstallerManifestSha256,
    string UsbWritePlanSha256,
    string RequiredConfirmationPhrase,
    bool RequiresStrongConfirmation);

public sealed record MacOSUsbExecutionPreflightResult(
    string PreflightPath,
    string Sha256Path,
    string PreflightSha256,
    string PreflightId,
    DateTimeOffset ExpiresAt,
    string RequiredConfirmationPhrase,
    bool RequiresStrongConfirmation);

public sealed record MacOSUsbExecutionPreflightVerificationResult(
    bool Success,
    IReadOnlyList<string> Errors);

/// <summary>
/// Creates a short-lived, read-only gate proving that all destructive-write
/// prerequisites still match. It cannot execute disk operations.
/// </summary>
public sealed class MacOSUsbExecutionPreflightService
{
    private const string PreflightName = "CorePilotUsbExecutionPreflight.json";
    private const string ShaName = "CorePilotUsbExecutionPreflight.sha256";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly InstallerManifestService _manifestService = new();
    private readonly MacOSUsbWritePlanService _planService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<MacOSUsbExecutionPreflightResult> CreateAsync(
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        MacOSUsbWritePlanResult plan,
        UsbTargetSafetyReport freshTarget,
        CancellationToken cancellationToken = default)
    {
        if (freshTarget.IsBlocked)
            throw new InvalidOperationException(
                $"Execution preflight blocked: USB safety is {freshTarget.LevelText}.");

        var manifestVerification = await _manifestService.VerifyAsync(
            manifest.ManifestPath,
            stage.WorkspaceDirectory,
            cancellationToken);

        if (!manifestVerification.Success)
            throw new InvalidOperationException(
                "Installer manifest failed execution preflight: " +
                string.Join("; ", manifestVerification.Errors.Take(4)));

        var planVerification = await _planService.VerifyAsync(
            plan.PlanPath,
            stage,
            manifest,
            freshTarget,
            cancellationToken);

        if (!planVerification.Success)
            throw new InvalidOperationException(
                "USB dry-run plan failed execution preflight: " +
                string.Join("; ", planVerification.Errors.Take(4)));

        var actualManifestSha = await ComputeSha256Async(
            manifest.ManifestPath,
            cancellationToken);

        if (!actualManifestSha.Equals(
                manifest.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Installer-manifest SHA-256 changed during execution preflight.");

        var actualPlanSha = await ComputeSha256Async(
            plan.PlanPath,
            cancellationToken);

        if (!actualPlanSha.Equals(
                plan.PlanSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "USB dry-run plan SHA-256 changed during execution preflight.");

        var now = DateTimeOffset.UtcNow;
        var document = new MacOSUsbExecutionPreflightDocument(
            SchemaVersion: 1,
            PreflightId: Guid.NewGuid().ToString("N"),
            CreatedAt: now,
            ExpiresAt: now.Add(Lifetime),
            ReadyForConfirmationOnly: true,
            PhysicalDiskWritesEnabled: false,
            TargetDeviceId: freshTarget.DeviceId,
            TargetDiskIndex: freshTarget.DiskIndex,
            TargetIdentityFingerprint: freshTarget.IdentityFingerprint,
            TargetSafetyLevel: freshTarget.LevelText,
            InstallerManifestSha256: manifest.ManifestSha256,
            UsbWritePlanSha256: plan.PlanSha256,
            RequiredConfirmationPhrase: plan.FutureConfirmationPhrase,
            RequiresStrongConfirmation: plan.RequiresStrongConfirmation);

        var preflightPath = Path.Combine(
            stage.WorkspaceDirectory,
            PreflightName);

        await File.WriteAllTextAsync(
            preflightPath,
            JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var preflightSha = await ComputeSha256Async(
            preflightPath,
            cancellationToken);

        var shaPath = Path.Combine(stage.WorkspaceDirectory, ShaName);
        await File.WriteAllTextAsync(
            shaPath,
            $"{preflightSha}  {PreflightName}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        var immediateVerification = await VerifyAsync(
            preflightPath,
            stage,
            manifest,
            plan,
            freshTarget,
            DateTimeOffset.UtcNow,
            cancellationToken);

        if (!immediateVerification.Success)
            throw new InvalidOperationException(
                "Execution preflight failed immediate verification: " +
                string.Join("; ", immediateVerification.Errors.Take(4)));

        return new(
            preflightPath,
            shaPath,
            preflightSha,
            document.PreflightId,
            document.ExpiresAt,
            document.RequiredConfirmationPhrase,
            document.RequiresStrongConfirmation);
    }

    public async Task<MacOSUsbExecutionPreflightVerificationResult> VerifyAsync(
        string preflightPath,
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        MacOSUsbWritePlanResult plan,
        UsbTargetSafetyReport freshTarget,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        var expectedPath = Path.GetFullPath(
            Path.Combine(stage.WorkspaceDirectory, PreflightName));

        if (!File.Exists(preflightPath))
            return new(false, ["Execution preflight file is missing."]);

        if (!Path.GetFullPath(preflightPath).Equals(
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
            return new(false, ["Execution preflight file is outside the workspace."]);

        var shaPath = Path.Combine(stage.WorkspaceDirectory, ShaName);

        if (!File.Exists(shaPath))
        {
            errors.Add("Execution preflight SHA-256 sidecar is missing.");
        }
        else
        {
            var expectedSha = (await File.ReadAllTextAsync(
                    shaPath,
                    cancellationToken))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            var actualSha = await ComputeSha256Async(
                preflightPath,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(expectedSha) ||
                !actualSha.Equals(
                    expectedSha,
                    StringComparison.OrdinalIgnoreCase))
                errors.Add("Execution preflight SHA-256 does not match its sidecar.");
        }

        MacOSUsbExecutionPreflightDocument? document;

        try
        {
            var json = await File.ReadAllTextAsync(
                preflightPath,
                cancellationToken);

            document = JsonSerializer.Deserialize<MacOSUsbExecutionPreflightDocument>(
                json,
                JsonOptions);
        }
        catch (Exception ex)
        {
            return new(false, [$"Execution preflight JSON is invalid: {ex.Message}"]);
        }

        if (document is null)
            return new(false, ["Execution preflight could not be parsed."]);

        if (document.SchemaVersion != 1)
            errors.Add(
                $"Unsupported execution preflight schema: {document.SchemaVersion}.");

        if (!document.ReadyForConfirmationOnly)
            errors.Add("Execution preflight is not marked confirmation-only.");

        if (document.PhysicalDiskWritesEnabled)
            errors.Add(
                "Execution preflight unexpectedly claims physical writes are enabled.");

        if (now > document.ExpiresAt)
            errors.Add("Execution preflight expired.");

        if (!document.TargetIdentityFingerprint.Equals(
                freshTarget.IdentityFingerprint,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB target fingerprint changed after preflight creation.");

        if (document.TargetDiskIndex != freshTarget.DiskIndex)
            errors.Add("USB disk number changed after preflight creation.");

        if (!document.TargetDeviceId.Equals(
                freshTarget.DeviceId,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB device path changed after preflight creation.");

        if (!document.InstallerManifestSha256.Equals(
                manifest.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("Installer-manifest hash no longer matches preflight.");

        if (!document.UsbWritePlanSha256.Equals(
                plan.PlanSha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB write-plan hash no longer matches preflight.");

        if (!document.RequiredConfirmationPhrase.Equals(
                plan.FutureConfirmationPhrase,
                StringComparison.Ordinal))
            errors.Add("Required confirmation phrase changed.");

        if (freshTarget.IsBlocked)
            errors.Add($"Current USB safety state is {freshTarget.LevelText}.");

        var manifestVerification = await _manifestService.VerifyAsync(
            manifest.ManifestPath,
            stage.WorkspaceDirectory,
            cancellationToken);

        if (!manifestVerification.Success)
            errors.Add(
                "Installer manifest no longer verifies: " +
                string.Join("; ", manifestVerification.Errors.Take(2)));

        var planVerification = await _planService.VerifyAsync(
            plan.PlanPath,
            stage,
            manifest,
            freshTarget,
            cancellationToken);

        if (!planVerification.Success)
            errors.Add(
                "USB write plan no longer verifies: " +
                string.Join("; ", planVerification.Errors.Take(2)));

        return new(errors.Count == 0, errors);
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
