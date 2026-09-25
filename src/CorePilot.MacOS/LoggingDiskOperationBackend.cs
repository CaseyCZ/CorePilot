using CorePilot.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CorePilot.MacOS;

public sealed record DiskOperationSimulationEntry(
    int Order,
    string Phase,
    string Description,
    bool WouldBeDestructive,
    string CommandPreview,
    DateTimeOffset SimulatedAt,
    string Result);

public sealed record DiskOperationSimulationTranscript(
    int SchemaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Backend,
    bool CanWritePhysicalDisks,
    bool SimulationOnly,
    int TargetDiskIndex,
    string TargetIdentityFingerprint,
    string InstallerManifestSha256,
    string UsbWritePlanSha256,
    string TypedConfirmationSha256,
    IReadOnlyList<DiskOperationSimulationEntry> Entries);

public sealed record DiskOperationSimulationResult(
    string TranscriptPath,
    string Sha256Path,
    string TranscriptSha256,
    int SimulatedSteps,
    bool CanWritePhysicalDisks);

public interface IDiskOperationBackend
{
    string Name { get; }
    bool CanWritePhysicalDisks { get; }

    Task<IReadOnlyList<DiskOperationSimulationEntry>> ExecuteAsync(
        IReadOnlyList<MacOSUsbWritePlanAction> actions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Simulation-only backend. It records what a future writer would do.
/// There is intentionally no process launch, device handle, filesystem mount,
/// partition API, format API or raw-disk API in this implementation.
/// </summary>
public sealed class LoggingDiskOperationBackend : IDiskOperationBackend
{
    public string Name => "CorePilot.LoggingDiskOperationBackend";
    public bool CanWritePhysicalDisks => false;

    public Task<IReadOnlyList<DiskOperationSimulationEntry>> ExecuteAsync(
        IReadOnlyList<MacOSUsbWritePlanAction> actions,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<DiskOperationSimulationEntry>(actions.Count);

        foreach (var action in actions.OrderBy(x => x.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();

            entries.Add(new(
                action.Order,
                action.Phase,
                action.Description,
                action.Destructive,
                action.CommandPreview,
                DateTimeOffset.UtcNow,
                action.Destructive
                    ? "SIMULATED ONLY — destructive action was NOT executed"
                    : "SIMULATED ONLY — no system action was executed"));
        }

        return Task.FromResult<IReadOnlyList<DiskOperationSimulationEntry>>(entries);
    }
}

public sealed class MacOSUsbWriteSimulationService
{
    private const string TranscriptName = "CorePilotUsbSimulationTranscript.json";
    private const string ShaName = "CorePilotUsbSimulationTranscript.sha256";

    private readonly MacOSUsbWritePlanService _planService = new();
    private readonly MacOSUsbTypedConfirmationService _confirmationService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<DiskOperationSimulationResult> SimulateAsync(
        OpCoreStagingResult stage,
        InstallerManifestResult manifest,
        MacOSUsbWritePlanResult plan,
        MacOSUsbExecutionPreflightResult preflight,
        MacOSUsbTypedConfirmationResult confirmation,
        UsbTargetSafetyReport freshTarget,
        IDiskOperationBackend backend,
        CancellationToken cancellationToken = default)
    {
        if (backend.CanWritePhysicalDisks)
            throw new InvalidOperationException(
                "Simulation refuses a backend capable of physical disk writes.");

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
                "Typed confirmation failed simulation preflight: " +
                string.Join("; ", confirmationVerification.Errors.Take(4)));

        var planVerification = await _planService.VerifyAsync(
            plan.PlanPath,
            stage,
            manifest,
            freshTarget,
            cancellationToken);

        if (!planVerification.Success)
            throw new InvalidOperationException(
                "USB write plan failed simulation verification: " +
                string.Join("; ", planVerification.Errors.Take(4)));

        var json = await File.ReadAllTextAsync(
            plan.PlanPath,
            cancellationToken);

        var document = JsonSerializer.Deserialize<MacOSUsbWritePlanDocument>(
            json,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "USB write plan could not be parsed for simulation.");

        if (!document.DryRunOnly)
            throw new InvalidOperationException(
                "Simulation accepts only a DryRunOnly USB plan.");

        var startedAt = DateTimeOffset.UtcNow;
        var entries = await backend.ExecuteAsync(
            document.Actions,
            cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;

        var transcript = new DiskOperationSimulationTranscript(
            SchemaVersion: 1,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            Backend: backend.Name,
            CanWritePhysicalDisks: backend.CanWritePhysicalDisks,
            SimulationOnly: true,
            TargetDiskIndex: freshTarget.DiskIndex,
            TargetIdentityFingerprint: freshTarget.IdentityFingerprint,
            InstallerManifestSha256: manifest.ManifestSha256,
            UsbWritePlanSha256: plan.PlanSha256,
            TypedConfirmationSha256: confirmation.ConfirmationSha256,
            Entries: entries);

        var transcriptPath = Path.Combine(
            stage.WorkspaceDirectory,
            TranscriptName);

        await File.WriteAllTextAsync(
            transcriptPath,
            JsonSerializer.Serialize(transcript, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var transcriptSha = await ComputeSha256Async(
            transcriptPath,
            cancellationToken);

        var shaPath = Path.Combine(stage.WorkspaceDirectory, ShaName);
        await File.WriteAllTextAsync(
            shaPath,
            $"{transcriptSha}  {TranscriptName}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        return new(
            transcriptPath,
            shaPath,
            transcriptSha,
            entries.Count,
            backend.CanWritePhysicalDisks);
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
