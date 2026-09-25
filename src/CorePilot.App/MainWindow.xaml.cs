using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CorePilot.Core;
using CorePilot.Hardware;
using CorePilot.Linux;
using CorePilot.MacOS;
using CorePilot.Windows;

namespace CorePilot.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly HardwareScanner _scanner = new();
    private readonly HardwareSnifferBridge _hardwareSniffer = new();
    private readonly HardwareSnifferReportParser _hardwareSnifferParser = new();
    private readonly MacOSCompatibilityAnalyzer _macAnalyzer = new();
    private readonly MacOSAutomationPlanner _macAutomationPlanner = new();
    private readonly MacOSAutomationProfileStore _macProfileStore = new();
    private readonly OpCoreSimplifyStager _opCoreStager = new();
    private readonly OpCoreSimplifyBuilder _opCoreBuilder = new();
    private readonly AppleRecoveryDownloader _appleRecoveryDownloader = new();
    private readonly InstallerManifestService _installerManifestService = new();
    private readonly UsbTargetSafetyInspector _usbSafetyInspector = new();
    private readonly MacOSUsbWritePlanService _usbWritePlanService = new();
    private readonly MacOSUsbExecutionPreflightService _usbExecutionPreflightService = new();
    private readonly MacOSUsbTypedConfirmationService _usbTypedConfirmationService = new();
    private readonly MacOSUsbWriteSimulationService _usbWriteSimulationService = new();
    private readonly LoggingDiskOperationBackend _loggingDiskBackend = new();
    private readonly MacOSWorkflowStateMachine _workflowStateMachine = new();
    private HardwareSnifferExportResult? _deepScanExport;
    private OpCoreStagingResult? _opCoreStage;
    private OpCoreBuildResult? _lastEfiBuild;
    private AppleRecoveryResult? _lastRecovery;
    private InstallerManifestResult? _lastInstallerManifest;
    private UsbTargetSafetyReport? _usbSafetyReport;
    private MacOSUsbWritePlanResult? _lastUsbWritePlan;
    private MacOSUsbExecutionPreflightResult? _lastUsbExecutionPreflight;
    private MacOSUsbTypedConfirmationResult? _lastUsbTypedConfirmation;
    private HardwareReport? _hardwareReport;
    private CompatibilityReport? _compatibilityReport;
    private MacOSAutomationProfile? _automationProfile;

    private string _scanStatus = "Not scanned";
    private string _deepScanStatus = "Deep scan not run. It downloads the official Hardware-Sniffer-CLI release on first use.";
    private string _planStatus = "Select a system and USB drive, then prepare an installation plan.";
    private string _usbSafetyStatus = "USB target not inspected. No physical-disk writes are enabled.";
    private string _compatibilitySummary = "Scan hardware and select macOS to run compatibility checks.";
    private string _macPlanDetails = "";
    private string _workflowStatus = "Workflow · IDLE · Not started.";

    public ObservableCollection<ISystemModule> Systems { get; } = [];
    public ObservableCollection<SystemVariant> Variants { get; } = [];
    public ObservableCollection<UsbDriveInfo> UsbDrives { get; } = [];
    public ObservableCollection<HardwareDisplayItem> HardwareItems { get; } = [];
    public ObservableCollection<CompatibilityFinding> CompatibilityItems { get; } = [];

    public string ScanStatus
    {
        get => _scanStatus;
        private set { _scanStatus = value; OnPropertyChanged(); }
    }

    public string DeepScanStatus
    {
        get => _deepScanStatus;
        private set { _deepScanStatus = value; OnPropertyChanged(); }
    }

    public string PlanStatus
    {
        get => _planStatus;
        private set { _planStatus = value; OnPropertyChanged(); }
    }

    public string UsbSafetyStatus
    {
        get => _usbSafetyStatus;
        private set { _usbSafetyStatus = value; OnPropertyChanged(); }
    }

    public string CompatibilitySummary
    {
        get => _compatibilitySummary;
        private set { _compatibilitySummary = value; OnPropertyChanged(); }
    }

    public string MacPlanDetails
    {
        get => _macPlanDetails;
        private set { _macPlanDetails = value; OnPropertyChanged(); }
    }

    public string WorkflowStatus
    {
        get => _workflowStatus;
        private set { _workflowStatus = value; OnPropertyChanged(); }
    }

    public Visibility MacPlanVisibility =>
        string.IsNullOrWhiteSpace(MacPlanDetails) ? Visibility.Collapsed : Visibility.Visible;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        Systems.Add(new MacOSModule());
        Systems.Add(new WindowsModule());
        Systems.Add(new LinuxModule());

        SystemCombo.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            await RefreshDrivesAsync();
            await ScanHardwareAsync();
        };
    }

    private async void ScanHardware_OnClick(object sender, RoutedEventArgs e) => await ScanHardwareAsync();

    private MacOSWorkflowPhase HardwareBaselinePhase =>
        _deepScanExport is not null
            ? MacOSWorkflowPhase.DeepScanned
            : _hardwareReport is not null
                ? MacOSWorkflowPhase.HardwareScanned
                : MacOSWorkflowPhase.Idle;

    private void UpdateWorkflowStatus()
    {
        var state = _workflowStateMachine.Current;
        var expiry = state.AuthorizationExpiresAt is null
            ? ""
            : $" · expires {state.AuthorizationExpiresAt.Value.ToLocalTime():HH:mm:ss}";

        WorkflowStatus =
            $"Workflow · {state.PhaseText} · gen {state.Generation}{expiry} · {state.Reason}";
    }

    private void InvalidateWorkflowAfter(
        MacOSWorkflowPhase preserveThrough,
        string reason)
    {
        _workflowStateMachine.InvalidateAfter(preserveThrough, reason);
        UpdateWorkflowStatus();
    }

    private void AdvanceWorkflow(
        MacOSWorkflowPhase phase,
        string reason,
        DateTimeOffset? authorizationExpiresAt = null)
    {
        _workflowStateMachine.Advance(phase, reason, authorizationExpiresAt);
        UpdateWorkflowStatus();
    }

    private async void DeepScan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DeepScanStatus = "Starting Hardware Sniffer…";
            var progress = new Progress<string>(message => DeepScanStatus = message);
            var result = await _hardwareSniffer.ExportAsync(progress);
            _deepScanExport = result;

            _hardwareReport ??= await _scanner.ScanAsync();
            _hardwareReport = await _hardwareSnifferParser.MergeAsync(result.ReportPath, _hardwareReport);

            RefreshHardwareView();
            _workflowStateMachine.Reset("Deep Scan refreshed hardware identity.");
            AdvanceWorkflow(
                MacOSWorkflowPhase.DeepScanned,
                "Hardware Sniffer Report.json + ACPI imported.");
            RunCompatibilityAnalysis();

            DeepScanStatus =
                $"Hardware Sniffer {result.Version} imported successfully · " +
                $"{_hardwareReport.Devices.Count} devices · " +
                $"SHA256 {result.ToolSha256[..16]}… · {result.ReportPath}";
        }
        catch (Exception ex)
        {
            DeepScanStatus = $"Deep scan failed: {ex.Message}";
        }
    }

    private async Task ScanHardwareAsync()
    {
        try
        {
            ScanStatus = "Scanning…";
            _hardwareReport = await _scanner.ScanAsync();
            _deepScanExport = null;
            _workflowStateMachine.Reset("Local hardware scan refreshed; previous downstream state revoked.");
            AdvanceWorkflow(
                MacOSWorkflowPhase.HardwareScanned,
                "Local hardware scan complete.");
            RefreshHardwareView();
            ScanStatus = $"Detected {HardwareItems.Count} hardware items";
            RunCompatibilityAnalysis();
        }
        catch (Exception ex)
        {
            ScanStatus = "Scan failed";
            PlanStatus = $"Hardware scan failed: {ex.Message}";
        }
    }

    private void RefreshHardwareView()
    {
        HardwareItems.Clear();

        if (_hardwareReport is null)
            return;

        foreach (var item in _hardwareReport.ToDisplayItems())
            HardwareItems.Add(item);

        ScanStatus = $"Detected {HardwareItems.Count} hardware items";
    }

    private async void RefreshDrives_OnClick(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    private void UsbCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _usbSafetyReport = null;
        _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;
        InvalidateWorkflowAfter(
            MacOSWorkflowPhase.ManifestVerified,
            "USB target changed; target-specific authorization revoked.");
        UsbSafetyStatus = UsbCombo.SelectedItem is UsbDriveInfo
            ? "USB target changed — safety inspection required."
            : "USB target not selected.";
    }

    private async void InspectUsb_OnClick(object sender, RoutedEventArgs e)
    {
        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            UsbSafetyStatus = "Select a USB target first.";
            return;
        }

        try
        {
            UsbSafetyStatus = "Inspecting physical disk, partitions and mounted volumes…";
            var report = await _usbSafetyInspector.InspectAsync(usb);
            _usbSafetyReport = report;
            _lastUsbWritePlan = null;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.ManifestVerified,
                "USB safety inspection refreshed; previous target authorization revoked.");
            UsbSafetyStatus = report.Summary;

            if (_workflowStateMachine.Current.Phase >= MacOSWorkflowPhase.ManifestVerified)
            {
                AdvanceWorkflow(
                    MacOSWorkflowPhase.UsbInspected,
                    $"USB safety inspected: {report.LevelText}.");
            }
        }
        catch (Exception ex)
        {
            _usbSafetyReport = null;
            UsbSafetyStatus = $"BLOCKED · USB inspection failed closed: {ex.Message}";
        }
    }

    private async Task RefreshDrivesAsync()
    {
        try
        {
            var current = UsbCombo.SelectedItem as UsbDriveInfo;
            var disks = await _scanner.ScanDisksAsync();

            _usbSafetyReport = null;
            _lastUsbWritePlan = null;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.ManifestVerified,
                "USB device list refreshed; target-specific authorization revoked.");
            UsbSafetyStatus = "USB list refreshed — safety inspection required.";

            UsbDrives.Clear();
            foreach (var disk in disks.Where(x => x.IsUsb))
                UsbDrives.Add(disk);

            if (UsbDrives.Count > 0)
                UsbCombo.SelectedItem = current is null
                    ? UsbDrives[0]
                    : UsbDrives.FirstOrDefault(x => x.DeviceId == current.DeviceId) ?? UsbDrives[0];

            if (UsbDrives.Count == 0)
                PlanStatus = "No USB disk detected. Connect a USB flash drive and press Refresh drives.";
        }
        catch (Exception ex)
        {
            PlanStatus = $"Disk scan failed: {ex.Message}";
        }
    }

    private void SystemCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Variants.Clear();

        if (SystemCombo.SelectedItem is not ISystemModule module)
            return;

        foreach (var variant in module.Variants)
            Variants.Add(variant);

        if (Variants.Count > 0)
            VariantCombo.SelectedIndex = 0;

        PlanStatus = module.Description;
        RunCompatibilityAnalysis();
    }

    private void VariantCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RunCompatibilityAnalysis();

    private void RunCompatibilityAnalysis()
    {
        CompatibilityItems.Clear();
        InvalidateWorkflowAfter(
            HardwareBaselinePhase,
            "System/version or hardware compatibility inputs changed.");
        _compatibilityReport = null;
        _automationProfile = null;
        _opCoreStage = null;
        _lastEfiBuild = null;
        _lastRecovery = null;
        _lastInstallerManifest = null;
        _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;
        MacPlanDetails = "";
        OnPropertyChanged(nameof(MacPlanVisibility));

        if (_hardwareReport is null)
        {
            CompatibilitySummary = "Scan hardware first.";
            return;
        }

        if (SystemCombo.SelectedItem is not ISystemModule { Id: "macos" } ||
            VariantCombo.SelectedItem is not SystemVariant target)
        {
            CompatibilitySummary = "Compatibility rules for this system module are not enabled yet.";
            return;
        }

        _compatibilityReport = _macAnalyzer.Analyze(_hardwareReport, target);
        _automationProfile = _macAutomationPlanner.Build(_hardwareReport, target, _compatibilityReport);

        foreach (var finding in _compatibilityReport.Findings)
            CompatibilityItems.Add(finding);

        CompatibilitySummary = _compatibilityReport.Summary;
        MacPlanDetails = BuildPlanText(_compatibilityReport, _automationProfile);
        AdvanceWorkflow(
            MacOSWorkflowPhase.CompatibilityReady,
            $"Compatibility evaluated for {target.DisplayName}.");
        OnPropertyChanged(nameof(MacPlanVisibility));
    }

    private static string BuildPlanText(
        CompatibilityReport report,
        MacOSAutomationProfile? automationProfile)
    {
        var parts = new List<string>();

        if (report.RequiredKexts.Count > 0)
            parts.Add("Kexts: " + string.Join(", ", report.RequiredKexts));

        if (report.RequiredPatches.Count > 0)
            parts.Add("Patches: " + string.Join(", ", report.RequiredPatches));

        if (report.BootArguments.Count > 0)
            parts.Add("Boot args: " + string.Join(" ", report.BootArguments));

        if (automationProfile is not null)
        {
            parts.Add($"Automation: SMBIOS {automationProfile.SmbiosModel} · " +
                      $"GPU {automationProfile.GraphicsMode} · Wi-Fi {automationProfile.WifiMode} · " +
                      $"Audio {automationProfile.AudioMode}");

            if (automationProfile.RequiresReview)
                parts.Add("Advanced review: " + string.Join("; ",
                    automationProfile.Decisions
                        .Where(x => x.RequiresReview)
                        .Select(x => $"{x.Subject}: {x.Choice}")));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private async void PreparePlan_OnClick(object sender, RoutedEventArgs e)
    {
        if (SystemCombo.SelectedItem is not ISystemModule system ||
            VariantCombo.SelectedItem is not SystemVariant variant)
        {
            PlanStatus = "Choose a system and version first.";
            return;
        }

        if (system.Id == "macos" && _compatibilityReport is { CanProceed: false })
        {
            PlanStatus = $"Cannot prepare native macOS media yet: {_compatibilityReport.Summary}. Resolve blocking hardware first.";
            return;
        }

        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            PlanStatus = "Connect and select a USB flash drive.";
            return;
        }

        if (system.Id == "macos" && _automationProfile is not null)
        {
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.CompatibilityReady,
                "Workspace preparation restarted; generated artifacts revoked.");
            _opCoreStage = null;
            _lastEfiBuild = null;
            _lastRecovery = null;
            _lastInstallerManifest = null;
            _lastUsbWritePlan = null;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;

            var profilePath = await _macProfileStore.SaveAsync(_automationProfile);

            if (_deepScanExport is null)
            {
                PlanStatus = $"Plan ready: {variant.DisplayName} → {usb.DisplayName}. " +
                             $"Automation profile saved to {profilePath}. Run Deep scan to prepare the EFI workspace.";
                return;
            }

            PlanStatus = "Preparing reproducible OpenCore workspace…";
            var stage = await _opCoreStager.StageAsync(
                _deepScanExport.ReportPath,
                _deepScanExport.AcpiDirectory,
                _automationProfile);
            _opCoreStage = stage;
            AdvanceWorkflow(
                MacOSWorkflowPhase.WorkspaceStaged,
                $"OpenCore workspace staged for {variant.DisplayName}.");

            var review = _automationProfile.RequiresReview
                ? " Advanced review is required before EFI generation."
                : "";

            PlanStatus = $"Workspace ready: {stage.WorkspaceDirectory}. " +
                         $"OpCore Simplify {stage.UpstreamCommit[..8]} · " +
                         $"Python: {(stage.PythonAvailable ? stage.PythonVersion : "not found")}." +
                         review;
            return;
        }

        PlanStatus = $"Plan ready: {variant.DisplayName} → {usb.DisplayName}. " +
                     $"Next milestone: OpenCore/EFI generation, downloads and guarded USB writing for {system.DisplayName}.";
    }

    private async void BuildEfi_OnClick(object sender, RoutedEventArgs e)
    {
        if (SystemCombo.SelectedItem is not ISystemModule { Id: "macos" })
        {
            PlanStatus = "EFI Builder is currently available only for the macOS module.";
            return;
        }

        if (_automationProfile is null)
        {
            PlanStatus = "Run the hardware scan and select a macOS version first.";
            return;
        }

        if (!_automationProfile.CanBuildEfi)
        {
            PlanStatus = "EFI build is blocked by the current compatibility profile.";
            return;
        }

        if (_automationProfile.RequiresReview)
        {
            PlanStatus = "EFI build requires Advanced review before CorePilot can continue.";
            return;
        }

        if (_opCoreStage is null)
        {
            PlanStatus = "Prepare the plan first. CorePilot needs a staged OpenCore workspace before building EFI.";
            return;
        }

        try
        {
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.WorkspaceStaged,
                "EFI build restarted; recovery, manifest and USB authorization revoked.");
            _lastEfiBuild = null;
            _lastRecovery = null;
            _lastInstallerManifest = null;
            _lastUsbWritePlan = null;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;

            var progress = new Progress<string>(message => PlanStatus = message);
            var result = await _opCoreBuilder.BuildAsync(
                _opCoreStage,
                _automationProfile,
                progress);
            _lastEfiBuild = result;
            _lastRecovery = null;
            _lastInstallerManifest = null;
            _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;

            PlanStatus =
                $"EFI build complete ✅ {result.EfiDirectory}. " +
                $"SMBIOS {result.SmbiosModel} · {result.Kexts.Count} kexts · " +
                $"{result.AcpiPatches.Count} ACPI selections · " +
                $"ocvalidate: {result.OcValidateStatus} · structure: {result.StructuralValidationStatus}.";
        }
        catch (Exception ex)
        {
            PlanStatus = $"EFI build failed: {ex.Message}";
        }
    }

    private async void DownloadRecovery_OnClick(object sender, RoutedEventArgs e)
    {
        if (SystemCombo.SelectedItem is not ISystemModule { Id: "macos" })
        {
            PlanStatus = "Apple Recovery is available only for the macOS module.";
            return;
        }

        if (_automationProfile is null || _opCoreStage is null)
        {
            PlanStatus = "Prepare the macOS plan first.";
            return;
        }

        if (_lastEfiBuild is null || !_lastEfiBuild.Success)
        {
            PlanStatus = "Build and validate EFI before downloading Apple Recovery.";
            return;
        }

        try
        {
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.EfiValidated,
                "Recovery generation restarted; manifest and USB authorization revoked.");
            _lastRecovery = null;
            _lastInstallerManifest = null;
            _lastUsbWritePlan = null;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;

            var progress = new Progress<string>(message => PlanStatus = message);
            var result = await _appleRecoveryDownloader.DownloadAsync(
                _opCoreStage,
                _automationProfile,
                _lastEfiBuild,
                progress);
            _lastRecovery = result;

            PlanStatus = "Creating final installer manifest…";
            var manifest = await _installerManifestService.CreateAsync(
                _opCoreStage,
                _automationProfile,
                _lastEfiBuild,
                result);

            var verification = await _installerManifestService.VerifyAsync(
                manifest.ManifestPath,
                _opCoreStage.WorkspaceDirectory);

            if (!verification.Success)
                throw new InvalidOperationException(
                    "Installer manifest verification failed: " +
                    string.Join("; ", verification.Errors.Take(4)));

            _lastInstallerManifest = manifest;
            _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;

            PlanStatus =
                $"Apple Recovery + installer manifest ready ✅ " +
                $"{manifest.FileCount} files · " +
                $"manifest SHA256 {manifest.ManifestSha256[..16]}… · " +
                $"{result.OutputDirectory}.";
        }
        catch (Exception ex)
        {
            PlanStatus = $"Apple Recovery failed: {ex.Message}";
        }
    }

    private async void CreateUsbDryRun_OnClick(object sender, RoutedEventArgs e)
    {
        if (SystemCombo.SelectedItem is not ISystemModule { Id: "macos" })
        {
            PlanStatus = "USB dry-run planning is currently available only for the macOS module.";
            return;
        }

        if (_opCoreStage is null ||
            _lastInstallerManifest is null ||
            _lastRecovery is null)
        {
            PlanStatus = "Build EFI, download Recovery and create the verified installer manifest first.";
            return;
        }

        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            PlanStatus = "Select a USB target first.";
            return;
        }

        if (_usbSafetyReport is null)
        {
            PlanStatus = "Run Check USB safety before creating the dry-run plan.";
            return;
        }

        if (_usbSafetyReport.IsBlocked)
        {
            PlanStatus = $"USB dry-run blocked: {_usbSafetyReport.Summary}";
            return;
        }

        try
        {
            PlanStatus = "Re-inspecting USB identity before dry-run planning…";
            var freshReport = await _usbSafetyInspector.InspectAsync(usb);

            if (!freshReport.IdentityFingerprint.Equals(
                    _usbSafetyReport.IdentityFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                _usbSafetyReport = freshReport;
                _lastUsbWritePlan = null;
                UsbSafetyStatus = freshReport.Summary;
                PlanStatus = "USB identity changed since the previous inspection. Dry-run plan was rejected; inspect the target again.";
                return;
            }

            _usbSafetyReport = freshReport;
            UsbSafetyStatus = freshReport.Summary;
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.ManifestVerified,
                "USB dry-run restarted; previous target authorization revoked.");
            AdvanceWorkflow(
                MacOSWorkflowPhase.UsbInspected,
                $"USB identity re-inspected for dry-run: {freshReport.LevelText}.");

            PlanStatus = "Creating manifest-bound USB dry-run plan…";
            var plan = await _usbWritePlanService.CreateDryRunAsync(
                _opCoreStage,
                _lastInstallerManifest,
                freshReport);

            _lastUsbWritePlan = plan;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;
            AdvanceWorkflow(
                MacOSWorkflowPhase.DryRunPlanned,
                "Manifest-bound USB dry-run plan created and verified.");

            var confirmation = plan.RequiresStrongConfirmation
                ? " · future strong confirmation required"
                : "";

            PlanStatus =
                $"USB dry-run ready ✅ {plan.ActionCount} planned steps · " +
                $"FAT32 {plan.PlannedFat32PartitionBytes / 1024d / 1024d / 1024d:0.##} GiB · " +
                $"plan SHA256 {plan.PlanSha256[..16]}…{confirmation}. " +
                "No physical disk operation was executed.";
        }
        catch (Exception ex)
        {
            _lastUsbWritePlan = null;
            PlanStatus = $"USB dry-run failed: {ex.Message}";
        }
    }

    private async void RunExecutionPreflight_OnClick(object sender, RoutedEventArgs e)
    {
        if (_opCoreStage is null ||
            _lastInstallerManifest is null ||
            _lastUsbWritePlan is null)
        {
            PlanStatus = "Create the verified installer manifest and USB dry-run plan first.";
            return;
        }

        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            PlanStatus = "Select the same USB target used by the dry-run plan.";
            return;
        }

        try
        {
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.DryRunPlanned,
                "Execution preflight restarted; previous confirmation revoked.");
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;

            PlanStatus = "Running atomic execution preflight…";
            var freshTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (freshTarget.IsBlocked)
            {
                _lastUsbExecutionPreflight = null;
                _lastUsbTypedConfirmation = null;
                _usbSafetyReport = freshTarget;
                UsbSafetyStatus = freshTarget.Summary;
                PlanStatus = $"Execution preflight BLOCKED: {freshTarget.Summary}";
                return;
            }

            _usbSafetyReport = freshTarget;
            UsbSafetyStatus = freshTarget.Summary;

            var result = await _usbExecutionPreflightService.CreateAsync(
                _opCoreStage,
                _lastInstallerManifest,
                _lastUsbWritePlan,
                freshTarget);

            _lastUsbExecutionPreflight = result;
            _lastUsbTypedConfirmation = null;
            ConfirmationTextBox.Text = "";
            AdvanceWorkflow(
                MacOSWorkflowPhase.PreflightReady,
                "Atomic execution preflight passed.",
                result.ExpiresAt);

            PlanStatus =
                $"Execution preflight ready ✅ ID {result.PreflightId[..8]} · " +
                $"expires {result.ExpiresAt.ToLocalTime():HH:mm:ss} · " +
                $"next confirmation phrase: {result.RequiredConfirmationPhrase}. " +
                "Ready for confirmation only — physical disk writing is still disabled.";
        }
        catch (Exception ex)
        {
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;
            PlanStatus = $"Execution preflight failed: {ex.Message}";
        }
    }

    private async void ConfirmTarget_OnClick(object sender, RoutedEventArgs e)
    {
        if (_opCoreStage is null ||
            _lastInstallerManifest is null ||
            _lastUsbWritePlan is null ||
            _lastUsbExecutionPreflight is null)
        {
            PlanStatus = "Run a fresh execution preflight before confirmation.";
            return;
        }

        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            PlanStatus = "Select the same USB target used by the execution preflight.";
            return;
        }

        try
        {
            if (_workflowStateMachine.InvalidateIfAuthorizationExpired(
                    DateTimeOffset.UtcNow,
                    "Execution preflight expired before typed confirmation."))
            {
                _lastUsbExecutionPreflight = null;
                _lastUsbTypedConfirmation = null;
                ConfirmationTextBox.Text = "";
                UpdateWorkflowStatus();
                PlanStatus = "Execution preflight expired. Run it again before confirmation.";
                return;
            }

            _workflowStateMachine.EnsureExactly(MacOSWorkflowPhase.PreflightReady);
            PlanStatus = "Re-inspecting USB before accepting typed confirmation…";
            var freshTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (freshTarget.IsBlocked)
            {
                _lastUsbTypedConfirmation = null;
                _lastUsbExecutionPreflight = null;
                _usbSafetyReport = freshTarget;
                UsbSafetyStatus = freshTarget.Summary;
                PlanStatus = $"Confirmation BLOCKED: {freshTarget.Summary}";
                return;
            }

            _usbSafetyReport = freshTarget;
            UsbSafetyStatus = freshTarget.Summary;

            var result = await _usbTypedConfirmationService.CreateAsync(
                _opCoreStage,
                _lastInstallerManifest,
                _lastUsbWritePlan,
                _lastUsbExecutionPreflight,
                freshTarget,
                ConfirmationTextBox.Text);

            _lastUsbTypedConfirmation = result;
            AdvanceWorkflow(
                MacOSWorkflowPhase.Confirmed,
                "Exact typed confirmation accepted.",
                result.ExpiresAt);

            PlanStatus =
                $"Typed confirmation accepted ✅ ID {result.ConfirmationId[..8]} · " +
                $"valid only until {result.ExpiresAt.ToLocalTime():HH:mm:ss}. " +
                "Physical disk writing is still disabled.";
        }
        catch (Exception ex)
        {
            _lastUsbTypedConfirmation = null;
            PlanStatus = $"Confirmation rejected: {ex.Message}";
        }
    }

    private async void SimulateWrite_OnClick(object sender, RoutedEventArgs e)
    {
        if (_opCoreStage is null ||
            _lastInstallerManifest is null ||
            _lastUsbWritePlan is null ||
            _lastUsbExecutionPreflight is null ||
            _lastUsbTypedConfirmation is null)
        {
            PlanStatus = "Complete fresh preflight and exact typed confirmation before simulation.";
            return;
        }

        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            PlanStatus = "Select the same USB target used by the confirmed plan.";
            return;
        }

        try
        {
            if (_workflowStateMachine.InvalidateIfAuthorizationExpired(
                    DateTimeOffset.UtcNow,
                    "Execution authorization expired before simulation."))
            {
                _lastUsbExecutionPreflight = null;
                _lastUsbTypedConfirmation = null;
                ConfirmationTextBox.Text = "";
                UpdateWorkflowStatus();
                PlanStatus = "Execution authorization expired. Run preflight and confirmation again.";
                return;
            }

            _workflowStateMachine.EnsureExactly(MacOSWorkflowPhase.Confirmed);
            PlanStatus = "Re-inspecting USB and simulating the confirmed write plan…";
            var freshTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (freshTarget.IsBlocked)
            {
                PlanStatus = $"Simulation BLOCKED: {freshTarget.Summary}";
                return;
            }

            _usbSafetyReport = freshTarget;
            UsbSafetyStatus = freshTarget.Summary;

            var result = await _usbWriteSimulationService.SimulateAsync(
                _opCoreStage,
                _lastInstallerManifest,
                _lastUsbWritePlan,
                _lastUsbExecutionPreflight,
                _lastUsbTypedConfirmation,
                freshTarget,
                _loggingDiskBackend);

            AdvanceWorkflow(
                MacOSWorkflowPhase.Simulated,
                "Confirmed USB write plan completed through logging-only simulation.");

            PlanStatus =
                $"Write simulation complete ✅ {result.SimulatedSteps} steps · " +
                $"transcript SHA256 {result.TranscriptSha256[..16]}… · " +
                $"backend can write physical disks: {result.CanWritePhysicalDisks}. " +
                "No disk, partition, filesystem or volume was modified.";
        }
        catch (Exception ex)
        {
            PlanStatus = $"Write simulation failed: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
