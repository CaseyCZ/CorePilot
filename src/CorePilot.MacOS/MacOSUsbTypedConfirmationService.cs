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
