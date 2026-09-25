using CorePilot.Core;
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


var policy = new MacOSWorkflowActionPolicy();

MacOSWorkflowActionContext Context(
    bool busy = false,
    bool hardware = true,
    bool deep = true,
    bool usb = true,
    bool compat = true,
    bool autoBuild = true,
    bool review = false,
    bool workspace = false,
    bool efi = false,
    bool manifest = false,
    bool usbReport = false,
    bool usbBlocked = false,
    bool dryRun = false,
    bool preflight = false,
    bool confirmation = false) =>
    new(
        IsMacOSSelected: true,
        IsBusy: busy,
        HasHardware: hardware,
        HasDeepScan: deep,
        HasUsbSelection: usb,
        CompatibilityCanProceed: compat,
        AutomationCanBuild: autoBuild,
        AutomationRequiresReview: review,
        HasWorkspace: workspace,
        HasEfi: efi,
        HasManifest: manifest,
        HasUsbSafetyReport: usbReport,
        UsbIsBlocked: usbBlocked,
        HasDryRun: dryRun,
        HasPreflight: preflight,
        HasConfirmation: confirmation);

var actionWorkflow = new MacOSWorkflowStateMachine();
actionWorkflow.Advance(MacOSWorkflowPhase.HardwareScanned, "hardware");
actionWorkflow.Advance(MacOSWorkflowPhase.CompatibilityReady, "compat");

Assert(!policy.Evaluate(
        CorePilotWorkflowAction.PreparePlan,
        actionWorkflow.Current,
        Context(deep: false)).Enabled,
    "PreparePlan must stay disabled without Deep Scan");

Assert(policy.Evaluate(
        CorePilotWorkflowAction.PreparePlan,
        actionWorkflow.Current,
        Context()).Enabled,
    "PreparePlan must enable when compatibility and Deep Scan are ready");

actionWorkflow.Advance(MacOSWorkflowPhase.WorkspaceStaged, "workspace");
Assert(policy.Evaluate(
        CorePilotWorkflowAction.BuildEfi,
        actionWorkflow.Current,
        Context(workspace: true)).Enabled,
    "BuildEfi must enable only at WorkspaceStaged");

actionWorkflow.Advance(MacOSWorkflowPhase.EfiValidated, "efi");
Assert(policy.Evaluate(
        CorePilotWorkflowAction.DownloadRecovery,
        actionWorkflow.Current,
        Context(workspace: true, efi: true)).Enabled,
    "DownloadRecovery must enable after EFI validation");

actionWorkflow.Advance(MacOSWorkflowPhase.RecoveryVerified, "recovery");
actionWorkflow.Advance(MacOSWorkflowPhase.ManifestVerified, "manifest");

Assert(policy.Evaluate(
        CorePilotWorkflowAction.InspectUsb,
        actionWorkflow.Current,
        Context(workspace: true, efi: true, manifest: true)).Enabled,
    "USB inspection must enable after manifest verification");

actionWorkflow.Advance(MacOSWorkflowPhase.UsbInspected, "usb");
Assert(policy.Evaluate(
        CorePilotWorkflowAction.CreateUsbDryRun,
        actionWorkflow.Current,
        Context(workspace: true, efi: true, manifest: true, usbReport: true)).Enabled,
    "Dry-run must enable after safe USB inspection");

actionWorkflow.Advance(MacOSWorkflowPhase.DryRunPlanned, "dry");
Assert(policy.Evaluate(
        CorePilotWorkflowAction.RunPreflight,
        actionWorkflow.Current,
        Context(workspace: true, efi: true, manifest: true, usbReport: true, dryRun: true)).Enabled,
    "Preflight must enable after dry-run planning");

var actionExpiry = DateTimeOffset.UtcNow.AddMinutes(2);
actionWorkflow.Advance(MacOSWorkflowPhase.PreflightReady, "preflight", actionExpiry);
Assert(policy.Evaluate(
        CorePilotWorkflowAction.ConfirmTarget,
        actionWorkflow.Current,
        Context(workspace: true, efi: true, manifest: true, usbReport: true, dryRun: true, preflight: true)).Enabled,
    "Confirmation must enable only for a live preflight");

actionWorkflow.Advance(MacOSWorkflowPhase.Confirmed, "confirmed", actionExpiry);
Assert(policy.Evaluate(
        CorePilotWorkflowAction.SimulateWrite,
        actionWorkflow.Current,
        Context(workspace: true, efi: true, manifest: true, usbReport: true, dryRun: true, preflight: true, confirmation: true)).Enabled,
    "Simulation must enable after exact confirmation");

Assert(!policy.Evaluate(
        CorePilotWorkflowAction.SimulateWrite,
        actionWorkflow.Current,
        Context(busy: true, workspace: true, efi: true, manifest: true, usbReport: true, dryRun: true, preflight: true, confirmation: true)).Enabled,
    "Busy state must disable workflow actions");

actionWorkflow.Advance(MacOSWorkflowPhase.Simulated, "simulated");
Assert(!policy.Evaluate(
        CorePilotWorkflowAction.SimulateWrite,
        actionWorkflow.Current,
        Context(workspace: true, efi: true, manifest: true, usbReport: true, dryRun: true, preflight: true, confirmation: true)).Enabled,
    "Simulation must be one-shot in the action policy");

Console.WriteLine("CorePilot workflow action-policy smoke test OK");


var compatibilityAnalyzer = new MacOSCompatibilityAnalyzer();
var testHardware = new CorePilot.Core.HardwareReport(
    "TEST-PC",
    "Test",
    "Laptop",
    "Laptop",
    "Intel(R) Core(TM) i5-7360U CPU @ 2.30GHz",
    2,
    4,
    "Test Board",
    "Unknown",
    false,
    8L * 1024 * 1024 * 1024,
    new CorePilot.Core.HardwareDeviceInfo[]
    {
        new("GPU", "SudoMaker Virtual Display Adapter", @"ROOT\DISPLAY\0000"),
        new("GPU", "Intel(R) Iris(R) Plus Graphics 650", @"PCI\VEN_8086&DEV_5927&SUBSYS_0175106B&REV_06")
    },
    Array.Empty<CorePilot.Core.UsbDriveInfo>());

var tahoeCompatibility = compatibilityAnalyzer.Analyze(
    testHardware,
    new CorePilot.Core.SystemVariant("tahoe-26", "macOS Tahoe 26"));

Assert(!tahoeCompatibility.Findings.Any(x =>
        x.Component == "Firmware" &&
        x.State == CorePilot.Core.CompatibilityState.Blocked),
    "Unknown firmware must not be falsely classified as Legacy BIOS blocker");

Assert(tahoeCompatibility.Findings.Any(x =>
        x.Component == "Firmware" &&
        x.State == CorePilot.Core.CompatibilityState.Unknown),
    "Unknown firmware must remain an explicit verification item");

Assert(tahoeCompatibility.Findings.Any(x =>
        x.Title.Contains("SudoMaker", StringComparison.OrdinalIgnoreCase) &&
        x.Title.Contains("ignored", StringComparison.OrdinalIgnoreCase)),
    "Virtual display adapter must be explicitly ignored");

Assert(!tahoeCompatibility.Findings.Any(x =>
        x.Title.Contains("SudoMaker", StringComparison.OrdinalIgnoreCase) &&
        x.State == CorePilot.Core.CompatibilityState.Unknown),
    "Virtual display adapter must never become an unknown physical GPU");

Assert(tahoeCompatibility.Findings.Any(x =>
        x.Title.Contains("Kaby Lake", StringComparison.OrdinalIgnoreCase) &&
        x.Details.Contains("8086:5927", StringComparison.OrdinalIgnoreCase)),
    "Intel Iris Plus 650 PCI 8086:5927 must resolve as Kaby Lake graphics");

Assert(tahoeCompatibility.Findings.Any(x =>
        x.Component == "CPU" &&
        x.Title.Contains("7th-generation", StringComparison.OrdinalIgnoreCase)),
    "Intel i5-7360U must resolve as 7th-generation CPU");

var venturaCompatibility = compatibilityAnalyzer.Analyze(
    testHardware with { FirmwareMode = "UEFI" },
    new CorePilot.Core.SystemVariant("ventura-13", "macOS Ventura 13"));

Assert(venturaCompatibility.Findings.Any(x =>
        x.Title.Contains("Kaby Lake", StringComparison.OrdinalIgnoreCase) &&
        x.State == CorePilot.Core.CompatibilityState.Supported),
    "Kaby Lake graphics must use the native Ventura compatibility path");

Console.WriteLine("CorePilot hardware compatibility regression smoke test OK");


var apple2017Hardware = new CorePilot.Core.HardwareReport(
    "TEST-MAC",
    "Apple Inc.",
    "MacBookPro14,2",
    "Laptop",
    "Intel(R) Core(TM) i5-7267U CPU @ 3.10GHz",
    2,
    4,
    "Apple Inc. Mac-827FB448E656EC26",
    "Unknown",
    null,
    8L * 1024 * 1024 * 1024,
    new CorePilot.Core.HardwareDeviceInfo[]
    {
        new("GPU", "Intel(R) Iris(R) Plus Graphics 650", @"PCI\VEN_8086&DEV_5927&SUBSYS_0175106B&REV_06"),
        new("GPU", "SudoMaker Virtual Display Adapter", @"ROOT\DISPLAY\0000")
    },
    Array.Empty<CorePilot.Core.UsbDriveInfo>());

var appleVentura = compatibilityAnalyzer.Analyze(
    apple2017Hardware,
    new CorePilot.Core.SystemVariant("ventura-13", "macOS Ventura 13"));

Assert(appleVentura.CanProceed,
    "2017 MacBook Pro must pass native Ventura compatibility");
Assert(appleVentura.Findings.Any(x =>
        x.Component == "macOS" &&
        x.State == CorePilot.Core.CompatibilityState.Supported &&
        x.Title.Contains("officially supported", StringComparison.OrdinalIgnoreCase)),
    "2017 MacBook Pro must explicitly report Ventura as officially supported");
Assert(appleVentura.RequiredKexts.Count == 0 &&
       appleVentura.RequiredPatches.Count == 0 &&
       appleVentura.BootArguments.Count == 0,
    "native Apple Ventura path must not require Hackintosh kexts, patches or boot arguments");

var appleSonoma = compatibilityAnalyzer.Analyze(
    apple2017Hardware,
    new CorePilot.Core.SystemVariant("sonoma-14", "macOS Sonoma 14"));

Assert(!appleSonoma.CanProceed,
    "2017 MacBook Pro Sonoma path must not be treated as natively supported until OCLP integration exists");

Console.WriteLine("CorePilot genuine-Apple Ventura compatibility smoke test OK");


var sourceCatalog = OnlineSourceCatalogService.LoadBundledCatalog();
var sourceIds = sourceCatalog.Sources
    .Select(x => x.Id)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

var requiredOnlineSources = new[]
{
    "macos.apple-download-install",
    "macos.dortania-guide",
    "macos.opencore",
    "macos.opcore-simplify",
    "macos.macrecoveryx",
    "macos.usbtoolbox",
    "macos.propertree",
    "macos.ocat",
    "macos.hackintool",
    "macos.gensmbios",
    "macos.oclp",
    "macos.oclp-mod-kgp",
    "macos.kext.lilu",
    "macos.kext.virtualsmc",
    "macos.kext.whatevergreen",
    "macos.kext.applealc",
    "windows.microsoft-windows11",
    "windows.rufus",
    "linux.ubuntu",
    "linux.fedora",
    "linux.debian",
    "linux.mint"
};

foreach (var sourceId in requiredOnlineSources)
    Assert(sourceIds.Contains(sourceId),
        $"online source catalog is missing {sourceId}");

Assert(sourceCatalog.Sources
        .Where(x => x.Strategy == "webPage")
        .All(x => Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) &&
                  uri.Scheme == Uri.UriSchemeHttps),
    "all web catalog sources must use HTTPS");

var opCoreSimplifySource = sourceCatalog.Sources.Single(x =>
    x.Id == "macos.opcore-simplify");

Assert(opCoreSimplifySource.Strategy == "githubBranchHead" &&
       opCoreSimplifySource.RequireVerifiedCommit,
    "OpCore Simplify must resolve a current verified GitHub branch head");

Assert(sourceCatalog.Sources
        .Where(x => x.Role == "kext")
        .All(x => x.Strategy == "githubRelease" &&
                  !string.IsNullOrWhiteSpace(x.Repository)),
    "kext sources must resolve from upstream GitHub releases");

Console.WriteLine("CorePilot online source catalog smoke test OK");


var writtenWorkflow = new MacOSWorkflowStateMachine();
writtenWorkflow.Advance(MacOSWorkflowPhase.HardwareScanned, "hardware");
writtenWorkflow.Advance(MacOSWorkflowPhase.DeepScanned, "deep");
writtenWorkflow.Advance(MacOSWorkflowPhase.CompatibilityReady, "compat");
writtenWorkflow.Advance(MacOSWorkflowPhase.WorkspaceStaged, "workspace");
writtenWorkflow.Advance(MacOSWorkflowPhase.EfiValidated, "efi");
writtenWorkflow.Advance(MacOSWorkflowPhase.RecoveryVerified, "recovery");
writtenWorkflow.Advance(MacOSWorkflowPhase.ManifestVerified, "manifest");
writtenWorkflow.Advance(MacOSWorkflowPhase.UsbInspected, "usb");
writtenWorkflow.Advance(MacOSWorkflowPhase.DryRunPlanned, "plan");
var writtenExpiry = DateTimeOffset.UtcNow.AddMinutes(2);
writtenWorkflow.Advance(MacOSWorkflowPhase.PreflightReady, "preflight", writtenExpiry);
writtenWorkflow.Advance(MacOSWorkflowPhase.Confirmed, "confirmed", writtenExpiry);
writtenWorkflow.Advance(MacOSWorkflowPhase.Written, "physical write complete");
Assert(writtenWorkflow.Current.Phase == MacOSWorkflowPhase.Written,
    "guarded physical write must have a terminal Written phase");
Assert(writtenWorkflow.Current.AuthorizationExpiresAt is null,
    "terminal physical write must clear short-lived authorization");

Console.WriteLine("CorePilot physical-write state smoke test OK");

var genericAnalyzer = new GenericCompatibilityAnalyzer();

var windows11Ok = genericAnalyzer.Analyze(
    "windows",
    testHardware with
    {
        FirmwareMode = "UEFI",
        SecureBoot = true,
        MemoryBytes = 8L * 1024 * 1024 * 1024
    },
    new SystemVariant("windows-11", "Windows 11"));

Assert(windows11Ok.CanProceed,
    "Windows 11 generic verification must complete for a basic UEFI/4GB+ machine");
Assert(windows11Ok.Findings.Any(x =>
        x.Component == "TPM" &&
        x.State == CompatibilityState.Warning),
    "Windows 11 must explicitly disclose that TPM is not collected by the lightweight scan");

var windows11Legacy = genericAnalyzer.Analyze(
    "windows",
    testHardware with
    {
        FirmwareMode = "Legacy BIOS",
        SecureBoot = false,
        MemoryBytes = 8L * 1024 * 1024 * 1024
    },
    new SystemVariant("windows-11", "Windows 11"));

Assert(!windows11Legacy.CanProceed,
    "Windows 11 generic verification must block Legacy BIOS");

var ubuntuOk = genericAnalyzer.Analyze(
    "linux",
    testHardware with
    {
        FirmwareMode = "UEFI",
        MemoryBytes = 8L * 1024 * 1024 * 1024
    },
    new SystemVariant("ubuntu", "Ubuntu"));

Assert(ubuntuOk.CanProceed,
    "Linux generic verification must produce a usable report instead of failing because it is not macOS");

Console.WriteLine("CorePilot Windows/Linux compatibility smoke test OK");

Assert(opCoreSimplifySource.Repository == "lzhoang2801/OpCore-Simplify" &&
       opCoreSimplifySource.Branch == "main" &&
       opCoreSimplifySource.Critical,
    "execution-critical OpCore Simplify trust policy must stay pinned to the expected upstream");

Console.WriteLine("CorePilot execution-critical source trust smoke test OK");


var componentAuditTemp = Path.Combine(
    Path.GetTempPath(),
    "CorePilot-component-audit-" + Guid.NewGuid().ToString("N"));

try
{
    var upstream = Path.Combine(componentAuditTemp, "upstream");
    var workspace = Path.Combine(componentAuditTemp, "workspace");
    var ock = Path.Combine(upstream, "OCK_Files");
    var openCore = Path.Combine(ock, "OpenCorePkg");

    Directory.CreateDirectory(openCore);
    Directory.CreateDirectory(workspace);
    await File.WriteAllTextAsync(
        Path.Combine(openCore, "manifest.json"),
        "{}");

    var trustedHash = new string('A', 64);
    await File.WriteAllTextAsync(
        Path.Combine(ock, "history.json"),
        $$"""
        [
          {
            "product_name": "OpenCorePkg",
            "id": 123,
            "url": "https://example.com/OpenCorePkg.zip",
            "sha256": "{{trustedHash}}"
          }
        ]
        """);

    var auditStage = new OpCoreStagingResult(
        workspace,
        upstream,
        new string('B', 40),
        new string('C', 64),
        Path.Combine(workspace, "Report.json"),
        Path.Combine(workspace, "ACPI"),
        Path.Combine(workspace, "CorePilotAutomationProfile.json"),
        true,
        "python",
        "python.exe",
        "3.x");

    var componentAuditService = new OpCoreDownloadedComponentAuditService();
    var componentAudit = await componentAuditService.AuditAsync(auditStage);

    Assert(componentAudit.ComponentCount == 1 &&
           componentAudit.Components[0].ProductName == "OpenCorePkg",
        "downloaded component audit must accept hashed HTTPS OpenCorePkg history");

    await File.WriteAllTextAsync(
        Path.Combine(ock, "history.json"),
        """
        [
          {
            "product_name": "OpenCorePkg",
            "id": 123,
            "url": "https://example.com/OpenCorePkg.zip",
            "sha256": ""
          }
        ]
        """);

    var missingHashRejected = false;
    try
    {
        _ = await componentAuditService.AuditAsync(auditStage);
    }
    catch (InvalidDataException)
    {
        missingHashRejected = true;
    }

    Assert(missingHashRejected,
        "downloaded component audit must reject components without SHA-256 metadata");
}
finally
{
    if (Directory.Exists(componentAuditTemp))
        Directory.Delete(componentAuditTemp, recursive: true);
}

Console.WriteLine("CorePilot downloaded-component integrity smoke test OK");
