using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed record MacOSUsbTypedConfirmationDocument(
    int SchemaVersion,
    string ConfirmationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string PreflightId,
    string PreflightSha256,
    string TargetIdentityFingerprint,
    int TargetDiskIndex,
    string InstallerManifestSha256,
    string UsbWritePlanSha256,
    string AcceptedPhrase,
    bool ConfirmationAccepted,
    bool PhysicalDiskWritesEnabled);

public sealed record MacOSUsbTypedConfirmationResult(
    string ConfirmationPath,
    string Sha256Path,
    string ConfirmationSha256,
    string ConfirmationId,
    DateTimeOffset ExpiresAt);

public sealed record MacOSUsbTypedConfirmationVerificationResult(
    bool Success,
    IReadOnlyList<string> Errors);

/// <summary>
/// Accepts only the exact phrase from a valid, unexpired execution preflight.
/// This class does not enable or perform physical-disk writes.
/// </summary>
public sealed class MacOSUsbTypedConfirmationService
{
    private const string ConfirmationName = "CorePilotUsbTypedConfirmation.json";
    private const string ShaName = "CorePilotUsbTypedConfirmation.sha256";

    private readonly MacOSUsbExecutionPreflightService _preflightService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<MacOSUsbTypedConfirmationResult> CreateAsync(
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        MacOSUsbWritePlanResult plan,
        MacOSUsbExecutionPreflightResult preflight,
        UsbTargetSafetyReport freshTarget,
        string typedPhrase,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var verification = await _preflightService.VerifyAsync(
            preflight.PreflightPath,
            stage,
            manifest,
            plan,
            freshTarget,
            now,
            cancellationToken);

        if (!verification.Success)
            throw new InvalidOperationException(
                "Execution preflight is no longer valid: " +
                string.Join("; ", verification.Errors.Take(4)));

        if (now > preflight.ExpiresAt)
            throw new InvalidOperationException(
                "Execution preflight expired. Run it again before confirming.");

        if (!string.Equals(
                typedPhrase?.Trim(),
                preflight.RequiredConfirmationPhrase,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Confirmation phrase does not match exactly.");

        var actualPreflightSha = await ComputeSha256Async(
            preflight.PreflightPath,
            cancellationToken);

        if (!actualPreflightSha.Equals(
                preflight.PreflightSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Execution preflight hash changed before confirmation.");

        var document = new MacOSUsbTypedConfirmationDocument(
            SchemaVersion: 1,
            ConfirmationId: Guid.NewGuid().ToString("N"),
            CreatedAt: now,
            ExpiresAt: preflight.ExpiresAt,
            PreflightId: preflight.PreflightId,
            PreflightSha256: preflight.PreflightSha256,
            TargetIdentityFingerprint: freshTarget.IdentityFingerprint,
            TargetDiskIndex: freshTarget.DiskIndex,
            InstallerManifestSha256: manifest.ManifestSha256,
            UsbWritePlanSha256: plan.PlanSha256,
            AcceptedPhrase: preflight.RequiredConfirmationPhrase,
            ConfirmationAccepted: true,
            PhysicalDiskWritesEnabled: false);

        var confirmationPath = Path.Combine(
            stage.WorkspaceDirectory,
            ConfirmationName);

        await File.WriteAllTextAsync(
            confirmationPath,
            JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var confirmationSha = await ComputeSha256Async(
            confirmationPath,
            cancellationToken);

        var shaPath = Path.Combine(stage.WorkspaceDirectory, ShaName);
        await File.WriteAllTextAsync(
            shaPath,
            $"{confirmationSha}  {ConfirmationName}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        // Confirmation must never outlive the original preflight.
        if (DateTimeOffset.UtcNow > document.ExpiresAt)
            throw new InvalidOperationException(
                "Execution preflight expired while confirmation was being recorded.");

        return new(
            confirmationPath,
            shaPath,
            confirmationSha,
            document.ConfirmationId,
            document.ExpiresAt);
    }

    public async Task<MacOSUsbTypedConfirmationVerificationResult> VerifyAsync(
        MacOSUsbTypedConfirmationResult confirmation,
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        MacOSUsbWritePlanResult plan,
        MacOSUsbExecutionPreflightResult preflight,
        UsbTargetSafetyReport freshTarget,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!File.Exists(confirmation.ConfirmationPath))
            return new(false, ["Typed confirmation file is missing."]);

        var expectedPath = Path.GetFullPath(
            Path.Combine(stage.WorkspaceDirectory, ConfirmationName));

        if (!Path.GetFullPath(confirmation.ConfirmationPath).Equals(
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
            return new(false, ["Typed confirmation file is outside the workspace."]);

        if (now > confirmation.ExpiresAt)
            errors.Add("Typed confirmation expired with its execution preflight.");

        var sidecarPath = Path.Combine(stage.WorkspaceDirectory, ShaName);
        if (!File.Exists(sidecarPath))
        {
            errors.Add("Typed confirmation SHA-256 sidecar is missing.");
        }
        else
        {
            var expectedSha = (await File.ReadAllTextAsync(
                    sidecarPath,
                    cancellationToken))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            var actualSha = await ComputeSha256Async(
                confirmation.ConfirmationPath,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(expectedSha) ||
                !actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                errors.Add("Typed confirmation SHA-256 does not match its sidecar.");

            if (!actualSha.Equals(
                    confirmation.ConfirmationSha256,
                    StringComparison.OrdinalIgnoreCase))
                errors.Add("Typed confirmation hash no longer matches the in-memory result.");
        }

        MacOSUsbTypedConfirmationDocument? document;

        try
        {
            var json = await File.ReadAllTextAsync(
                confirmation.ConfirmationPath,
                cancellationToken);

            document = JsonSerializer.Deserialize<MacOSUsbTypedConfirmationDocument>(
                json,
                JsonOptions);
        }
        catch (Exception ex)
        {
            return new(false, [$"Typed confirmation JSON is invalid: {ex.Message}"]);
        }

        if (document is null)
            return new(false, ["Typed confirmation could not be parsed."]);

        if (document.SchemaVersion != 1)
            errors.Add($"Unsupported typed-confirmation schema: {document.SchemaVersion}.");

        if (!document.ConfirmationAccepted)
            errors.Add("Typed confirmation is not marked accepted.");

        if (document.PhysicalDiskWritesEnabled)
            errors.Add("Typed confirmation unexpectedly enables physical disk writes.");

        if (document.ExpiresAt != preflight.ExpiresAt ||
            document.ExpiresAt != confirmation.ExpiresAt)
            errors.Add("Typed confirmation expiry differs from the execution preflight.");

        if (!document.PreflightId.Equals(
                preflight.PreflightId,
                StringComparison.Ordinal))
            errors.Add("Typed confirmation points to a different preflight.");

        if (!document.PreflightSha256.Equals(
                preflight.PreflightSha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("Typed confirmation points to a different preflight hash.");

        if (!document.TargetIdentityFingerprint.Equals(
                freshTarget.IdentityFingerprint,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB target fingerprint changed after confirmation.");

        if (document.TargetDiskIndex != freshTarget.DiskIndex)
            errors.Add("USB disk number changed after confirmation.");

        if (!document.InstallerManifestSha256.Equals(
                manifest.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("Installer-manifest hash changed after confirmation.");

        if (!document.UsbWritePlanSha256.Equals(
                plan.PlanSha256,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("USB write-plan hash changed after confirmation.");

        if (!document.AcceptedPhrase.Equals(
                preflight.RequiredConfirmationPhrase,
                StringComparison.Ordinal))
            errors.Add("Accepted phrase differs from the execution preflight.");

        var preflightVerification = await _preflightService.VerifyAsync(
            preflight.PreflightPath,
            stage,
            manifest,
            plan,
            freshTarget,
            now,
            cancellationToken);

        if (!preflightVerification.Success)
            errors.Add(
                "Execution preflight no longer verifies: " +
                string.Join("; ", preflightVerification.Errors.Take(2)));

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
