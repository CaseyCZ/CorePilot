namespace CorePilot.MacOS;

public enum MacOSWorkflowPhase
{
    Idle = 0,
    HardwareScanned = 10,
    DeepScanned = 20,
    CompatibilityReady = 30,
    WorkspaceStaged = 40,
    EfiValidated = 50,
    RecoveryVerified = 60,
    ManifestVerified = 70,
    UsbInspected = 80,
    DryRunPlanned = 90,
    PreflightReady = 100,
    Confirmed = 110,
    Simulated = 120,
    Written = 130
}

public sealed record MacOSWorkflowSnapshot(
    MacOSWorkflowPhase Phase,
    int Generation,
    string Reason,
    DateTimeOffset? AuthorizationExpiresAt)
{
    public string PhaseText => Phase switch
    {
        MacOSWorkflowPhase.Idle => "IDLE",
        MacOSWorkflowPhase.HardwareScanned => "HARDWARE SCANNED",
        MacOSWorkflowPhase.DeepScanned => "DEEP SCANNED",
        MacOSWorkflowPhase.CompatibilityReady => "COMPATIBILITY READY",
        MacOSWorkflowPhase.WorkspaceStaged => "WORKSPACE STAGED",
        MacOSWorkflowPhase.EfiValidated => "EFI VALIDATED",
        MacOSWorkflowPhase.RecoveryVerified => "RECOVERY VERIFIED",
        MacOSWorkflowPhase.ManifestVerified => "MANIFEST VERIFIED",
        MacOSWorkflowPhase.UsbInspected => "USB INSPECTED",
        MacOSWorkflowPhase.DryRunPlanned => "DRY-RUN PLANNED",
        MacOSWorkflowPhase.PreflightReady => "PREFLIGHT READY",
        MacOSWorkflowPhase.Confirmed => "CONFIRMED",
        MacOSWorkflowPhase.Simulated => "SIMULATED",
        MacOSWorkflowPhase.Written => "WRITTEN",
        _ => Phase.ToString().ToUpperInvariant()
    };
}

/// <summary>
/// Central authority for the macOS media workflow. Any input mutation can
/// invalidate all downstream authorizations in one place.
/// </summary>
public sealed class MacOSWorkflowStateMachine
{
    private readonly object _gate = new();
    private MacOSWorkflowPhase _phase = MacOSWorkflowPhase.Idle;
    private int _generation;
    private string _reason = "Not started.";
    private DateTimeOffset? _authorizationExpiresAt;

    public MacOSWorkflowSnapshot Current
    {
        get
        {
            lock (_gate)
                return Snapshot();
        }
    }

    public void Reset(string reason)
    {
        lock (_gate)
        {
            _generation++;
            _phase = MacOSWorkflowPhase.Idle;
            _authorizationExpiresAt = null;
            _reason = reason;
        }
    }

    public void Advance(
        MacOSWorkflowPhase phase,
        string reason,
        DateTimeOffset? authorizationExpiresAt = null)
    {
        lock (_gate)
        {
            if (phase < _phase)
                throw new InvalidOperationException(
                    $"Workflow cannot move backward from {_phase} to {phase}. Use InvalidateAfter().");

            _phase = phase;
            _reason = reason;

            if (phase is MacOSWorkflowPhase.PreflightReady or MacOSWorkflowPhase.Confirmed)
            {
                _authorizationExpiresAt = authorizationExpiresAt
                    ?? throw new InvalidOperationException(
                        "Preflight/confirmation states require an expiry.");
            }
            else if (phase < MacOSWorkflowPhase.PreflightReady ||
                     phase >= MacOSWorkflowPhase.Simulated)
            {
                _authorizationExpiresAt = null;
            }
        }
    }

    public void InvalidateAfter(
        MacOSWorkflowPhase preserveThrough,
        string reason)
    {
        lock (_gate)
        {
            _generation++;

            if (_phase > preserveThrough)
                _phase = preserveThrough;

            if (_phase < MacOSWorkflowPhase.PreflightReady)
                _authorizationExpiresAt = null;

            _reason = reason;
        }
    }

    public void EnsureAtLeast(MacOSWorkflowPhase required)
    {
        lock (_gate)
        {
            if (_phase < required)
                throw new InvalidOperationException(
                    $"Workflow is {_phase}; {required} is required.");
        }
    }

    public void EnsureExactly(MacOSWorkflowPhase required)
    {
        lock (_gate)
        {
            if (_phase != required)
                throw new InvalidOperationException(
                    $"Workflow is {_phase}; exact state {required} is required.");
        }
    }

    public bool InvalidateIfAuthorizationExpired(
        DateTimeOffset now,
        string reason = "Execution authorization expired.")
    {
        lock (_gate)
        {
            if (_phase < MacOSWorkflowPhase.PreflightReady ||
                _authorizationExpiresAt is null ||
                now <= _authorizationExpiresAt.Value)
                return false;

            _generation++;
            _phase = MacOSWorkflowPhase.DryRunPlanned;
            _authorizationExpiresAt = null;
            _reason = reason;
            return true;
        }
    }

    private MacOSWorkflowSnapshot Snapshot() =>
        new(_phase, _generation, _reason, _authorizationExpiresAt);
}
