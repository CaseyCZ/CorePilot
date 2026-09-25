using CorePilot.MacOS;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("State-machine smoke test failed: " + message);
}

var workflow = new MacOSWorkflowStateMachine();

Assert(workflow.Current.Phase == MacOSWorkflowPhase.Idle, "initial phase must be Idle");
Assert(workflow.Current.Generation == 0, "initial generation must be zero");

workflow.Advance(MacOSWorkflowPhase.HardwareScanned, "hardware");
workflow.Advance(MacOSWorkflowPhase.DeepScanned, "deep");
workflow.Advance(MacOSWorkflowPhase.CompatibilityReady, "compatibility");
workflow.Advance(MacOSWorkflowPhase.WorkspaceStaged, "workspace");
workflow.Advance(MacOSWorkflowPhase.EfiValidated, "efi");
workflow.Advance(MacOSWorkflowPhase.RecoveryVerified, "recovery");
workflow.Advance(MacOSWorkflowPhase.ManifestVerified, "manifest");
workflow.Advance(MacOSWorkflowPhase.UsbInspected, "usb");
workflow.Advance(MacOSWorkflowPhase.DryRunPlanned, "plan");

var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
workflow.Advance(MacOSWorkflowPhase.PreflightReady, "preflight", expiry);
workflow.Advance(MacOSWorkflowPhase.Confirmed, "confirmed", expiry);

Assert(workflow.Current.Phase == MacOSWorkflowPhase.Confirmed, "confirmation must advance the phase");
Assert(workflow.Current.AuthorizationExpiresAt == expiry, "confirmation must preserve the original expiry");

var generationBeforeUsbChange = workflow.Current.Generation;
workflow.InvalidateAfter(
    MacOSWorkflowPhase.ManifestVerified,
    "USB target changed");

Assert(workflow.Current.Phase == MacOSWorkflowPhase.ManifestVerified,
    "USB target change must revoke all target-specific states");
Assert(workflow.Current.AuthorizationExpiresAt is null,
    "USB target change must clear short-lived authorization");
Assert(workflow.Current.Generation == generationBeforeUsbChange + 1,
    "invalidation must increment the generation");

workflow.Advance(MacOSWorkflowPhase.UsbInspected, "usb2");
workflow.Advance(MacOSWorkflowPhase.DryRunPlanned, "plan2");

var expiry2 = DateTimeOffset.UtcNow.AddSeconds(1);
workflow.Advance(MacOSWorkflowPhase.PreflightReady, "preflight2", expiry2);
workflow.Advance(MacOSWorkflowPhase.Confirmed, "confirmed2", expiry2);

Assert(!workflow.InvalidateIfAuthorizationExpired(expiry2.AddMilliseconds(-1)),
    "authorization must stay valid before expiry");

Assert(workflow.InvalidateIfAuthorizationExpired(expiry2.AddMilliseconds(1)),
    "authorization must expire after its deadline");
Assert(workflow.Current.Phase == MacOSWorkflowPhase.DryRunPlanned,
    "expired authorization must fall back to DryRunPlanned");
Assert(workflow.Current.AuthorizationExpiresAt is null,
    "expired authorization must be cleared");

var backwardsRejected = false;
try
{
    workflow.Advance(MacOSWorkflowPhase.HardwareScanned, "illegal backwards move");
}
catch (InvalidOperationException)
{
    backwardsRejected = true;
}

Assert(backwardsRejected, "backward Advance() must be rejected");

workflow.Reset("simulation one-shot");
workflow.Advance(MacOSWorkflowPhase.HardwareScanned, "hardware");
workflow.Advance(MacOSWorkflowPhase.CompatibilityReady, "compatibility");
workflow.Advance(MacOSWorkflowPhase.WorkspaceStaged, "workspace");
workflow.Advance(MacOSWorkflowPhase.EfiValidated, "efi");
workflow.Advance(MacOSWorkflowPhase.RecoveryVerified, "recovery");
workflow.Advance(MacOSWorkflowPhase.ManifestVerified, "manifest");
workflow.Advance(MacOSWorkflowPhase.UsbInspected, "usb");
workflow.Advance(MacOSWorkflowPhase.DryRunPlanned, "plan");

var expiry3 = DateTimeOffset.UtcNow.AddMinutes(2);
workflow.Advance(MacOSWorkflowPhase.PreflightReady, "preflight3", expiry3);
workflow.Advance(MacOSWorkflowPhase.Confirmed, "confirmed3", expiry3);
workflow.EnsureExactly(MacOSWorkflowPhase.Confirmed);
workflow.Advance(MacOSWorkflowPhase.Simulated, "simulated");

var reuseRejected = false;
try
{
    workflow.EnsureExactly(MacOSWorkflowPhase.Confirmed);
}
catch (InvalidOperationException)
{
    reuseRejected = true;
}

Assert(reuseRejected, "simulated authorization must not be reusable");

workflow.Reset("new hardware scan");
Assert(workflow.Current.Phase == MacOSWorkflowPhase.Idle, "Reset must return to Idle");
Assert(workflow.Current.AuthorizationExpiresAt is null, "Reset must clear authorization");

Console.WriteLine("CorePilot state-machine smoke test OK");
