namespace CorePilot.MacOS;

public enum CorePilotWorkflowAction
{
    ScanHardware,
    DeepScan,
    RefreshDrives,
    InspectUsb,
    PreparePlan,
    BuildEfi,
    DownloadRecovery,
    CreateUsbDryRun,
    RunPreflight,
    ConfirmTarget,
    SimulateWrite
}

public sealed record MacOSWorkflowActionContext(
    bool IsMacOSSelected,
    bool IsBusy,
    bool HasHardware,
    bool HasDeepScan,
    bool HasUsbSelection,
    bool CompatibilityCanProceed,
    bool AutomationCanBuild,
    bool AutomationRequiresReview,
    bool HasWorkspace,
    bool HasEfi,
    bool HasManifest,
    bool HasUsbSafetyReport,
    bool UsbIsBlocked,
    bool HasDryRun,
    bool HasPreflight,
    bool HasConfirmation);

public sealed record WorkflowActionDecision(
    bool Enabled,
    string Reason);

/// <summary>
/// Pure state/action policy used by the UI and CI smoke tests.
/// It decides whether an action is valid before a click can occur.
/// </summary>
public sealed class MacOSWorkflowActionPolicy
{
    public WorkflowActionDecision Evaluate(
        CorePilotWorkflowAction action,
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (action == CorePilotWorkflowAction.ScanHardware)
            return context.IsBusy
                ? Disabled("CorePilot is busy with another operation.")
                : Enabled("Refresh the local hardware inventory. This revokes downstream generated state.");

        if (action == CorePilotWorkflowAction.RefreshDrives)
            return context.IsBusy
                ? Disabled("CorePilot is busy with another operation.")
                : Enabled("Refresh the physical USB disk list. Target-specific authorization will be revoked.");

        if (context.IsBusy)
            return Disabled("CorePilot is busy with another operation.");

        if (!context.IsMacOSSelected)
            return Disabled("This workflow action is currently implemented only for macOS.");

        return action switch
        {
            CorePilotWorkflowAction.DeepScan => DeepScan(context),
            CorePilotWorkflowAction.InspectUsb => InspectUsb(state, context),
            CorePilotWorkflowAction.PreparePlan => PreparePlan(state, context),
            CorePilotWorkflowAction.BuildEfi => BuildEfi(state, context),
            CorePilotWorkflowAction.DownloadRecovery => DownloadRecovery(state, context),
            CorePilotWorkflowAction.CreateUsbDryRun => CreateDryRun(state, context),
            CorePilotWorkflowAction.RunPreflight => RunPreflight(state, context),
            CorePilotWorkflowAction.ConfirmTarget => ConfirmTarget(state, context),
            CorePilotWorkflowAction.SimulateWrite => SimulateWrite(state, context),
            _ => Disabled("Action is not available.")
        };
    }

    private static WorkflowActionDecision DeepScan(MacOSWorkflowActionContext context)
    {
        if (!context.HasHardware)
            return Disabled("Run Scan hardware first.");

        return Enabled(
            context.HasDeepScan
                ? "Run Deep Scan again. This refreshes exact hardware identity and revokes downstream generated state."
                : "Run Hardware Sniffer Deep Scan to collect exact Report.json + ACPI data.");
    }

    private static WorkflowActionDecision PreparePlan(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.CompatibilityReady)
            return Disabled("Compatibility must be ready before staging the OpenCore workspace.");

        if (!context.HasDeepScan)
            return Disabled("Run Deep Scan first; exact Report.json + ACPI input is required.");

        if (!context.HasUsbSelection)
            return Disabled("Select a USB target first.");

        if (!context.CompatibilityCanProceed)
            return Disabled("Compatibility contains a blocking issue.");

        if (!context.AutomationCanBuild)
            return Disabled("The current automation profile blocks EFI generation.");

        if (context.AutomationRequiresReview)
            return Disabled("Advanced review is required before automatic EFI generation.");

        return Enabled("Stage the reproducible OpenCore workspace.");
    }

    private static WorkflowActionDecision BuildEfi(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.WorkspaceStaged || !context.HasWorkspace)
            return Disabled("Prepare and stage the macOS plan first.");

        if (!context.AutomationCanBuild || context.AutomationRequiresReview)
            return Disabled("The automation profile is not cleared for automatic EFI generation.");

        return Enabled("Build and validate the OpenCore EFI.");
    }

    private static WorkflowActionDecision DownloadRecovery(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.EfiValidated || !context.HasEfi)
            return Disabled("Build and validate EFI first.");

        return Enabled("Download and verify Apple Recovery, then create the installer manifest.");
    }

    private static WorkflowActionDecision InspectUsb(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (!context.HasUsbSelection)
            return Disabled("Select a USB target first.");

        if (state.Phase < MacOSWorkflowPhase.ManifestVerified)
            return Disabled("Create and verify the installer manifest first.");

        if (state.Phase > MacOSWorkflowPhase.UsbInspected)
            return Enabled("Re-inspect the USB target. This intentionally revokes the existing dry-run/preflight/confirmation.");

        return Enabled("Inspect the exact physical USB target and fail closed on unsafe Windows disk relationships.");
    }

    private static WorkflowActionDecision CreateDryRun(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.UsbInspected)
            return Disabled("Run Check USB safety after the installer manifest is ready.");

        if (!context.HasManifest)
            return Disabled("Verified installer manifest is missing.");

        if (!context.HasUsbSafetyReport)
            return Disabled("USB safety inspection is missing.");

        if (context.UsbIsBlocked)
            return Disabled("The selected USB target is blocked by safety inspection.");

        return Enabled("Create the manifest-bound USB dry-run plan. No physical disk command is executed.");
    }

    private static WorkflowActionDecision RunPreflight(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.DryRunPlanned || !context.HasDryRun)
            return Disabled("Create and verify the USB dry-run plan first.");

        return Enabled("Run the short-lived atomic execution preflight.");
    }

    private static WorkflowActionDecision ConfirmTarget(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.PreflightReady || !context.HasPreflight)
            return Disabled("Run a fresh execution preflight first.");

        if (state.AuthorizationExpiresAt is null ||
            DateTimeOffset.UtcNow > state.AuthorizationExpiresAt.Value)
            return Disabled("Execution preflight expired.");

        return Enabled("Type the exact confirmation phrase shown by the execution preflight.");
    }

    private static WorkflowActionDecision SimulateWrite(
        MacOSWorkflowSnapshot state,
        MacOSWorkflowActionContext context)
    {
        if (state.Phase != MacOSWorkflowPhase.Confirmed ||
            !context.HasPreflight ||
            !context.HasConfirmation)
            return Disabled("Exact typed confirmation is required before simulation.");

        if (state.AuthorizationExpiresAt is null ||
            DateTimeOffset.UtcNow > state.AuthorizationExpiresAt.Value)
            return Disabled("Execution authorization expired.");

        return Enabled("Run the confirmed plan through the logging-only backend. Physical disk writes remain impossible.");
    }

    private static WorkflowActionDecision Enabled(string reason) => new(true, reason);
    private static WorkflowActionDecision Disabled(string reason) => new(false, reason);
}
