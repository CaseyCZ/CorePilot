using CorePilot.Core;
using CorePilot.MacOS;
using CorePilot.Windows;
using CorePilot.Linux;

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

var appleTahoe = compatibilityAnalyzer.Analyze(
    apple2017Hardware,
    new CorePilot.Core.SystemVariant("tahoe-26", "macOS Tahoe 26"));

Assert(!appleTahoe.CanProceed,
    "2017 MacBook Pro Tahoe path must remain blocked until the OCLP legacy-Mac path is verified");
Assert(appleTahoe.Findings.Any(x =>
        x.Component == "Patcher" &&
        x.State == CorePilot.Core.CompatibilityState.ActionRequired),
    "legacy Apple Tahoe result must explicitly show that OCLP is required");
Assert(appleTahoe.Findings.Any(x =>
        x.Component == "GPU" &&
        x.State == CorePilot.Core.CompatibilityState.Supported),
    "MacBookPro14,2 result must report its known OCLP graphics path");
Assert(appleTahoe.Findings.Any(x =>
        x.Component == "T1 / Wi-Fi / USB" &&
        x.State == CorePilot.Core.CompatibilityState.ActionRequired),
    "MacBookPro14,2 Tahoe result must expose hardware patch-readiness checks");

Console.WriteLine("CorePilot genuine-Apple compatibility smoke test OK");


OnlineSourceResolution LiveSource(
    string id,
    string name,
    string repository,
    string version) =>
    new(
        Id: id,
        Name: name,
        Role: "test",
        Trust: "upstream",
        Strategy: "githubRelease",
        Repository: repository,
        SourceUrl: $"https://github.com/{repository}/releases/latest",
        Critical: false,
        Success: true,
        Live: true,
        FromCache: false,
        Version: version,
        ResolvedRef: version,
        PublishedAt: DateTimeOffset.UtcNow,
        VerifiedCommit: null,
        CheckedAt: DateTimeOffset.UtcNow,
        Error: null);

var autoResolver = new MacOSAutoResolutionService();

var appleOclpSnapshot = new OnlineSourceSnapshot(
    "macos",
    DateTimeOffset.UtcNow,
    "test",
    true,
    new[]
    {
        LiveSource(
            "macos.oclp",
            "OpenCore Legacy Patcher",
            "dortania/OpenCore-Legacy-Patcher",
            "test-current")
    });

var appleTahoeAuto = await autoResolver.ResolveAsync(
    apple2017Hardware,
    new CorePilot.Core.SystemVariant("tahoe-26", "macOS Tahoe 26"),
    appleTahoe,
    profile: null,
    appleOclpSnapshot,
    hasDeepScan: false);

Assert(appleTahoeAuto.Items.Any(x =>
        x.SourceId == "macos.oclp" &&
        x.State == MacOSAutoResolutionState.SourceReady),
    "legacy Apple auto resolver must find the current live OCLP source");
Assert(!appleTahoeAuto.AutomaticConfigurationReady &&
       appleTahoeAuto.UnresolvedCount > 0,
    "legacy Apple Tahoe must stay blocked while model-specific OCLP work remains unresolved");

var appleVenturaAuto = await autoResolver.ResolveAsync(
    apple2017Hardware,
    new CorePilot.Core.SystemVariant("ventura-13", "macOS Ventura 13"),
    appleVentura,
    profile: null,
    verifySnapshot: null,
    hasDeepScan: false);

Assert(appleVenturaAuto.AutomaticConfigurationReady,
    "native Apple Ventura must be ready without Hackintosh patches or Deep Scan");

var pcVenturaTarget =
    new CorePilot.Core.SystemVariant("ventura-13", "macOS Ventura 13");
var pcVenturaHardware = testHardware with { FirmwareMode = "UEFI" };
var pcVenturaCompatibility =
    compatibilityAnalyzer.Analyze(pcVenturaHardware, pcVenturaTarget);
var pcProfile =
    new MacOSAutomationPlanner().Build(
        pcVenturaHardware,
        pcVenturaTarget,
        pcVenturaCompatibility);

var pcSourceSnapshot = new OnlineSourceSnapshot(
    "macos",
    DateTimeOffset.UtcNow,
    "test",
    true,
    new[]
    {
        LiveSource("macos.kext.lilu", "Lilu", "acidanthera/Lilu", "test"),
        LiveSource("macos.kext.virtualsmc", "VirtualSMC", "acidanthera/VirtualSMC", "test"),
        LiveSource("macos.kext.whatevergreen", "WhateverGreen", "acidanthera/WhateverGreen", "test")
    });

var pcAuto = await autoResolver.ResolveAsync(
    pcVenturaHardware,
    pcVenturaTarget,
    pcVenturaCompatibility,
    pcProfile,
    pcSourceSnapshot,
    hasDeepScan: true);

Assert(pcAuto.AutomaticConfigurationReady,
    "compatible PC with Deep Scan and live required kext sources must become auto-configured");
Assert(pcAuto.Items.Any(x =>
        x.Category == "SMBIOS" &&
        x.State == MacOSAutoResolutionState.Prepared),
    "auto resolver must prepare the SMBIOS choice from detected hardware");
Assert(pcAuto.Items.Count(x =>
        x.State == MacOSAutoResolutionState.SourceReady) >= 3,
    "auto resolver must locate the required live kext sources");

Console.WriteLine("CorePilot automatic hardware-resolution smoke test OK");


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
    "macos.kext.itlwm",
    "macos.heliport",
    "macos.amd-vanilla",
    "windows.microsoft-windows11",
    "windows.fido",
    "windows.rufus",
    "linux.ubuntu",
    "linux.fedora",
    "linux.debian",
    "linux.mint"
};

foreach (var sourceId in requiredOnlineSources)
    Assert(sourceIds.Contains(sourceId),
        $"online source catalog is missing {sourceId}");

var windows11Source = sourceCatalog.Sources.Single(x =>
    x.Id == "windows.microsoft-windows11");
var windows10Source = sourceCatalog.Sources.Single(x =>
    x.Id == "windows.microsoft-windows10");
var ubuntuSource = sourceCatalog.Sources.Single(x =>
    x.Id == "linux.ubuntu");
var fedoraSource = sourceCatalog.Sources.Single(x =>
    x.Id == "linux.fedora");

Assert(OnlineSourceCatalogService.AppliesToTarget(
           windows11Source,
           "windows-11") &&
       !OnlineSourceCatalogService.AppliesToTarget(
           windows11Source,
           "windows-10") &&
       OnlineSourceCatalogService.AppliesToTarget(
           windows10Source,
           "windows-10"),
    "Windows verification sources must be scoped to the selected Windows target");

Assert(OnlineSourceCatalogService.AppliesToTarget(
           ubuntuSource,
           "ubuntu") &&
       !OnlineSourceCatalogService.AppliesToTarget(
           ubuntuSource,
           "fedora") &&
       OnlineSourceCatalogService.AppliesToTarget(
           fedoraSource,
           "fedora"),
    "Linux verification sources must be scoped to the selected distribution");

var fidoSource = sourceCatalog.Sources.Single(x =>
    x.Id == "windows.fido");
Assert(fidoSource.RequireVerifiedCommit &&
       OnlineSourceCatalogService.AppliesToTarget(
           fidoSource,
           "windows-11") &&
       OnlineSourceCatalogService.AppliesToTarget(
           fidoSource,
           "windows-10"),
    "Fido must stay a verified common Windows ISO resolver for both targets");

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

var knowledgeIds = sourceCatalog.Sources
    .Where(x => x.UseFor is { Count: > 0 })
    .Select(x => x.Id)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

Assert(knowledgeIds.Contains("macos.dortania-guide") &&
       knowledgeIds.Contains("macos.opcore-simplify") &&
       knowledgeIds.Contains("macos.usbtoolbox") &&
       knowledgeIds.Contains("macos.propertree") &&
       knowledgeIds.Contains("macos.hackintool") &&
       knowledgeIds.Contains("macos.gensmbios") &&
       knowledgeIds.Contains("macos.oclp"),
    "macOS preparation knowledge must include the supplied configuration and remediation toolchain");

Assert(knowledgeIds.Contains("macos.guide.vyoralek-opencore") &&
       knowledgeIds.Contains("macos.video.opencore-install-2giy") &&
       knowledgeIds.Contains("macos.video.virtualbox-ya3x"),
    "supplied practical article/video references must remain in the preparation knowledge catalog");

var virtualBoxReference = sourceCatalog.Sources.Single(x =>
    x.Id == "macos.video.virtualbox-ya3x");
Assert(!virtualBoxReference.ResolveOnVerify &&
       virtualBoxReference.UseFor?.Contains("installation-reference") == true,
    "VirtualBox-only video must stay reference-only and must never drive physical OpenCore configuration automatically");

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
        Tpm20 = true,
        MemoryBytes = 8L * 1024 * 1024 * 1024
    },
    new SystemVariant("windows-11", "Windows 11"));

Assert(windows11Ok.CanProceed,
    "Windows 11 generic verification must complete for a basic UEFI/4GB+ machine");
Assert(windows11Ok.Findings.Any(x =>
        x.Component == "TPM" &&
        x.State == CompatibilityState.Supported),
    "Windows 11 must require and confirm TPM 2.0 before reporting a usable path");

var windows11NoTpm = genericAnalyzer.Analyze(
    "windows",
    testHardware with
    {
        FirmwareMode = "UEFI",
        SecureBoot = true,
        Tpm20 = false,
        MemoryBytes = 8L * 1024 * 1024 * 1024
    },
    new SystemVariant("windows-11", "Windows 11"));

Assert(!windows11NoTpm.CanProceed &&
       windows11NoTpm.Findings.Any(x =>
           x.Component == "TPM" &&
           x.State == CompatibilityState.Blocked),
    "Windows 11 must fail closed when TPM 2.0 is absent or disabled");

var windows11Legacy = genericAnalyzer.Analyze(
    "windows",
    testHardware with
    {
        FirmwareMode = "Legacy BIOS",
        SecureBoot = false,
        Tpm20 = true,
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

var windowsPreparationSources = new OnlineSourceSnapshot(
    "windows",
    DateTimeOffset.UtcNow,
    "test",
    true,
    new[]
    {
        LiveSource(
            "windows.microsoft-windows11",
            "Microsoft Windows 11 download",
            "microsoft/windows",
            "current")
    });

var windowsPreparation = InstallationPreparationBuilder.FromGenericCompatibility(
    "windows",
    new SystemVariant("windows-11", "Windows 11"),
    windows11Ok,
    windowsPreparationSources,
    mediaWriterAvailable: false);

Assert(windowsPreparation.SystemPrepared,
    "a compatible Windows target with live sources must become a prepared system configuration");
Assert(!windowsPreparation.ReadyToWrite,
    "generic compatibility alone must not claim READY TO WRITE before a verified installer image is prepared");

var blockedWindowsPreparation = InstallationPreparationBuilder.FromGenericCompatibility(
    "windows",
    new SystemVariant("windows-11", "Windows 11"),
    windows11Legacy,
    windowsPreparationSources,
    mediaWriterAvailable: false);

Assert(!blockedWindowsPreparation.SystemPrepared &&
       blockedWindowsPreparation.UnresolvedCount > 0,
    "an unresolved Windows blocker must remain visible after preparation");

var windowsKnowledge = sourceCatalog.Sources
    .Where(x => x.Systems.Contains("windows") &&
                x.UseFor is { Count: > 0 })
    .ToArray();

Assert(windowsKnowledge.Any(x =>
        x.Id == "windows.rufus" &&
        x.UseFor!.Contains("media-writer")),
    "Rufus must be classified as a Windows media-writer preparation source");

var rufusReference = sourceCatalog.Sources.Single(x =>
    x.Id == "windows.rufus");
Assert(!rufusReference.ResolveOnVerify,
    "Rufus is reference/fallback knowledge only and must not slow normal Windows/Linux Verify");

var linuxKnowledge = sourceCatalog.Sources
    .Where(x => x.Systems.Contains("linux") &&
                x.UseFor is { Count: > 0 })
    .ToArray();

Assert(linuxKnowledge.Any(x =>
        x.Id == "linux.ubuntu" &&
        x.UseFor!.Contains("installation")),
    "Linux official images must be classified as installation preparation sources");

Console.WriteLine("CorePilot installation-preparation smoke test OK");

var writerTestTarget = new UsbTargetSafetyReport(
    @"\\.\PHYSICALDRIVE7",
    7,
    "Test USB",
    "SERIAL",
    "USB",
    "Removable Media",
    @"USBSTOR\TEST",
    32L * 1024 * 1024 * 1024,
    true,
    true,
    "ABCDEF0123456789ABCDEF0123456789",
    UsbTargetSafetyLevel.SafeCandidate,
    Array.Empty<string>(),
    Array.Empty<UsbPartitionInfo>());

var windowsTestImage = new PreparedIsoImage(
    "windows",
    "windows-11",
    "Windows 11",
    @"C:\test\windows.iso",
    "windows.iso",
    "https://software-download.microsoft.com/test/windows.iso",
    new string('A', 64),
    null,
    true,
    6L * 1024 * 1024 * 1024,
    @"C:\test\windows.iso.corepilot.json",
    "test");

var linuxTestImage = windowsTestImage with
{
    SystemId = "linux",
    TargetId = "ubuntu",
    DisplayName = "Ubuntu",
    FileName = "ubuntu.iso",
    SourceUrl = "https://releases.ubuntu.com/test/ubuntu.iso",
    Sha256 = new string('B', 64),
    ExpectedSha256 = new string('B', 64)
};

var windowsPhrase =
    WindowsInstallerUsbWriter.RequiredConfirmationPhrase(
        writerTestTarget,
        windowsTestImage);
var linuxPhrase =
    LinuxRawUsbWriter.RequiredConfirmationPhrase(
        writerTestTarget,
        linuxTestImage);

Assert(windowsPhrase.Contains("DISK 7", StringComparison.Ordinal) &&
       windowsPhrase.Contains("ABCDEF012345", StringComparison.Ordinal) &&
       windowsPhrase.Contains("WINDOWS 11", StringComparison.Ordinal),
    "Windows writer confirmation must bind the exact disk identity and prepared target");

Assert(linuxPhrase.Contains("DISK 7", StringComparison.Ordinal) &&
       linuxPhrase.Contains("ABCDEF012345", StringComparison.Ordinal) &&
       linuxPhrase.Contains("UBUNTU", StringComparison.Ordinal),
    "Linux writer confirmation must bind the exact disk identity and prepared target");

Console.WriteLine("CorePilot generic guarded-writer contract smoke test OK");

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
            "url": "https://github.com/acidanthera/OpenCorePkg/releases/download/1.0.7/OpenCore-1.0.7-RELEASE.zip",
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
    var liveCatalogSnapshot = new OnlineSourceSnapshot(
        "macos",
        DateTimeOffset.UtcNow,
        "test",
        true,
        new OnlineSourceResolution[]
        {
            new(
                "macos.opencore",
                "OpenCorePkg",
                "bootloader",
                "upstream",
                "githubRelease",
                "acidanthera/OpenCorePkg",
                "https://github.com/acidanthera/OpenCorePkg/releases/latest",
                true,
                true,
                true,
                false,
                "1.0.7",
                "1.0.7",
                DateTimeOffset.UtcNow,
                null,
                DateTimeOffset.UtcNow,
                null)
        });

    var componentAudit = await componentAuditService.AuditAsync(
        auditStage,
        liveCatalogSnapshot);

    Assert(componentAudit.ComponentCount == 1 &&
           componentAudit.Components[0].ProductName == "OpenCorePkg" &&
           componentAudit.Components[0].CatalogAligned is true,
        "downloaded component audit must bind hashed OpenCorePkg to the live catalog release");

    var staleCatalogSnapshot = liveCatalogSnapshot with
    {
        Sources = new OnlineSourceResolution[]
        {
            liveCatalogSnapshot.Sources[0] with
            {
                Version = "1.0.8",
                ResolvedRef = "1.0.8"
            }
        }
    };

    var staleReleaseRejected = false;
    try
    {
        _ = await componentAuditService.AuditAsync(
            auditStage,
            staleCatalogSnapshot);
    }
    catch (InvalidDataException)
    {
        staleReleaseRejected = true;
    }

    Assert(staleReleaseRejected,
        "downloaded component audit must reject an OpenCore release older than the live catalog");

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


var sourceTestTemp = Path.Combine(
    Path.GetTempPath(),
    "CorePilot-source-tests-" + Guid.NewGuid().ToString("N"));

try
{
    Directory.CreateDirectory(sourceTestTemp);

    var bundledCatalog = OnlineSourceCatalogService.LoadBundledCatalog();
    var bundledJson = System.Text.Json.JsonSerializer.Serialize(bundledCatalog);
    var verifiedSha = new string('d', 40);

    using var verifiedHttp = new HttpClient(new DelegateHttpHandler(request =>
    {
        if (request.RequestUri?.Host.Equals(
                "api.github.com",
                StringComparison.OrdinalIgnoreCase) == true &&
            request.RequestUri.AbsolutePath.EndsWith(
                "/repos/CaseyCZ/CorePilot/commits/main",
                StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sha = verifiedSha,
                        commit = new { verification = new { verified = true } }
                    }))
            };
        }

        if (request.RequestUri?.Host.Equals(
                "raw.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase) == true &&
            request.RequestUri.AbsolutePath.Contains(
                $"/CaseyCZ/CorePilot/{verifiedSha}/",
                StringComparison.OrdinalIgnoreCase) &&
            request.RequestUri.AbsolutePath.EndsWith(
                "/src/CorePilot.Core/Data/source-catalog.json",
                StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(bundledJson)
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }));

    var verifiedService = new OnlineSourceCatalogService(
        verifiedHttp,
        Path.Combine(sourceTestTemp, "verified"));

    var verifiedLoad = await verifiedService.LoadCatalogAsync();
    Assert(
        verifiedLoad.FromRemote &&
        verifiedLoad.Origin.Contains(verifiedSha, StringComparison.OrdinalIgnoreCase),
        "online catalog must load only from the immutable GitHub-verified main commit SHA");

    using var unverifiedHttp = new HttpClient(new DelegateHttpHandler(request =>
    {
        if (request.RequestUri?.AbsoluteUri ==
            "https://api.github.com/repos/CaseyCZ/CorePilot/commits/main")
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sha = new string('e', 40),
                        commit = new { verification = new { verified = false } }
                    }))
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }));

    var unverifiedService = new OnlineSourceCatalogService(
        unverifiedHttp,
        Path.Combine(sourceTestTemp, "unverified"));

    var unverifiedLoad = await unverifiedService.LoadCatalogAsync();
    Assert(
        !unverifiedLoad.FromRemote,
        "unverified CorePilot main commit must never authorize a remote source catalog");

    var corruptCacheDir = Path.Combine(sourceTestTemp, "corrupt-cache");
    Directory.CreateDirectory(corruptCacheDir);
    await File.WriteAllTextAsync(
        Path.Combine(corruptCacheDir, "source-catalog.json"),
        "{ definitely-not-json");

    using var offlineHttp = new HttpClient(new DelegateHttpHandler(_ =>
        new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));

    var offlineService = new OnlineSourceCatalogService(
        offlineHttp,
        corruptCacheDir);

    var offlineLoad = await offlineService.LoadCatalogAsync();
    Assert(
        !offlineLoad.FromRemote &&
        offlineLoad.Catalog.Sources.Count > 0,
        "offline/corrupt catalog cache must fail safely to the bundled trusted catalog");

    var offlineSnapshot = await offlineService.ResolveForSystemAsync("macos");
    Assert(
        offlineSnapshot.CriticalFailures > 0,
        "cached/offline critical sources must not authorize physical writing");
}
finally
{
    if (Directory.Exists(sourceTestTemp))
        Directory.Delete(sourceTestTemp, recursive: true);
}

Console.WriteLine("CorePilot online-source fallback and trust regression smoke test OK");

sealed class DelegateHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public DelegateHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(_handler(request));
}
