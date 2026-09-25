using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private readonly MacOSWorkflowActionPolicy _actionPolicy = new();
    private readonly SupportBundleService _supportBundleService = new();
    private readonly DispatcherTimer _authorizationTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private LogWindow? _logWindow;
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
    public ActivityLogService ActivityLog => App.Log;

    public bool CanChangeInputs => !ActivityLog.IsBusy;
    public bool CanScanHardware => Decision(CorePilotWorkflowAction.ScanHardware).Enabled;
    public bool CanDeepScan => Decision(CorePilotWorkflowAction.DeepScan).Enabled;
    public bool CanRefreshDrives => Decision(CorePilotWorkflowAction.RefreshDrives).Enabled;
    public bool CanInspectUsb => Decision(CorePilotWorkflowAction.InspectUsb).Enabled;
    public bool CanPreparePlan => Decision(CorePilotWorkflowAction.PreparePlan).Enabled;
    public bool CanBuildEfi => Decision(CorePilotWorkflowAction.BuildEfi).Enabled;
    public bool CanDownloadRecovery => Decision(CorePilotWorkflowAction.DownloadRecovery).Enabled;
    public bool CanCreateUsbDryRun => Decision(CorePilotWorkflowAction.CreateUsbDryRun).Enabled;
    public bool CanRunPreflight => Decision(CorePilotWorkflowAction.RunPreflight).Enabled;
    public bool CanConfirmTarget => Decision(CorePilotWorkflowAction.ConfirmTarget).Enabled;
    public bool CanSimulateWrite => Decision(CorePilotWorkflowAction.SimulateWrite).Enabled;
    public bool CanCreateSupportBundle => !ActivityLog.IsBusy;

    public string ScanHardwareToolTip => Decision(CorePilotWorkflowAction.ScanHardware).Reason;
    public string DeepScanToolTip => Decision(CorePilotWorkflowAction.DeepScan).Reason;
    public string RefreshDrivesToolTip => Decision(CorePilotWorkflowAction.RefreshDrives).Reason;
    public string InspectUsbToolTip => Decision(CorePilotWorkflowAction.InspectUsb).Reason;
    public string PreparePlanToolTip => Decision(CorePilotWorkflowAction.PreparePlan).Reason;
    public string BuildEfiToolTip => Decision(CorePilotWorkflowAction.BuildEfi).Reason;
    public string DownloadRecoveryToolTip => Decision(CorePilotWorkflowAction.DownloadRecovery).Reason;
    public string CreateUsbDryRunToolTip => Decision(CorePilotWorkflowAction.CreateUsbDryRun).Reason;
    public string RunPreflightToolTip => Decision(CorePilotWorkflowAction.RunPreflight).Reason;
    public string ConfirmTargetToolTip => Decision(CorePilotWorkflowAction.ConfirmTarget).Reason;
    public string SimulateWriteToolTip => Decision(CorePilotWorkflowAction.SimulateWrite).Reason;

    public string NextActionText
    {
        get
        {
            if (ActivityLog.IsBusy)
                return $"Running · {ActivityLog.CurrentArea} · {ActivityLog.CurrentStatus}";

            var phase = _workflowStateMachine.Current.Phase;

            if (phase == MacOSWorkflowPhase.CompatibilityReady && _deepScanExport is null)
                return "Next · Deep scan — exact hardware report is required before workspace staging.";

            return phase switch
            {
                MacOSWorkflowPhase.Idle => "Next · Scan hardware",
                MacOSWorkflowPhase.HardwareScanned => "Next · Deep scan",
                MacOSWorkflowPhase.DeepScanned => "Next · Select macOS/version and evaluate compatibility",
                MacOSWorkflowPhase.CompatibilityReady => "Next · Prepare plan",
                MacOSWorkflowPhase.WorkspaceStaged => "Next · Build EFI",
                MacOSWorkflowPhase.EfiValidated => "Next · Download Recovery",
                MacOSWorkflowPhase.RecoveryVerified => "Next · Finalize installer manifest",
                MacOSWorkflowPhase.ManifestVerified => "Next · Check USB safety",
                MacOSWorkflowPhase.UsbInspected => "Next · Dry-run USB plan",
                MacOSWorkflowPhase.DryRunPlanned => "Next · Execution preflight",
                MacOSWorkflowPhase.PreflightReady => "Next · Type exact confirmation phrase",
                MacOSWorkflowPhase.Confirmed => "Next · Simulate write",
                MacOSWorkflowPhase.Simulated => "Simulation complete · review Activity Log and transcript",
                _ => "Follow the enabled action."
            };
        }
    }

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
        ActivityLog.Info("UI", "Main window initialized.");
        ActivityLog.PropertyChanged += ActivityLog_OnPropertyChanged;
        _authorizationTimer.Tick += AuthorizationTimer_OnTick;
        _authorizationTimer.Start();
        Closed += (_, _) =>
        {
            _authorizationTimer.Stop();
            _authorizationTimer.Tick -= AuthorizationTimer_OnTick;
            ActivityLog.PropertyChanged -= ActivityLog_OnPropertyChanged;
        };

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

    private WorkflowActionDecision Decision(CorePilotWorkflowAction action) =>
        _actionPolicy.Evaluate(
            action,
            _workflowStateMachine.Current,
            BuildActionContext());

    private MacOSWorkflowActionContext BuildActionContext() =>
        new(
            IsMacOSSelected: SystemCombo.SelectedItem is ISystemModule { Id: "macos" },
            IsBusy: ActivityLog.IsBusy,
            HasHardware: _hardwareReport is not null,
            HasDeepScan: _deepScanExport is not null,
            HasUsbSelection: UsbCombo.SelectedItem is UsbDriveInfo,
            CompatibilityCanProceed: _compatibilityReport?.CanProceed == true,
            AutomationCanBuild: _automationProfile?.CanBuildEfi == true,
            AutomationRequiresReview: _automationProfile?.RequiresReview == true,
            HasWorkspace: _opCoreStage is not null,
            HasEfi: _lastEfiBuild?.Success == true,
            HasManifest: _lastInstallerManifest is not null && _lastRecovery is not null,
            HasUsbSafetyReport: _usbSafetyReport is not null,
            UsbIsBlocked: _usbSafetyReport?.IsBlocked != false,
            HasDryRun: _lastUsbWritePlan is not null,
            HasPreflight: _lastUsbExecutionPreflight is not null,
            HasConfirmation: _lastUsbTypedConfirmation is not null);

    private void ActivityLog_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActivityLogService.IsBusy) or
            nameof(ActivityLogService.CurrentStatus) or
            nameof(ActivityLogService.CurrentArea))
        {
            Dispatcher.BeginInvoke(RefreshActionAvailability);
        }
    }

    private void AuthorizationTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_workflowStateMachine.InvalidateIfAuthorizationExpired(
                DateTimeOffset.UtcNow,
                "Execution authorization expired automatically."))
        {
            if (_workflowStateMachine.Current.AuthorizationExpiresAt is not null)
                RefreshActionAvailability();
            return;
        }

        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;
        ConfirmationTextBox.Text = "";
        ActivityLog.Warning(
            "Workflow",
            "Execution authorization expired automatically; preflight and typed confirmation were revoked.");
        UpdateWorkflowStatus();
    }

    private void RefreshActionAvailability()
    {
        OnPropertyChanged(nameof(CanChangeInputs));
        OnPropertyChanged(nameof(CanScanHardware));
        OnPropertyChanged(nameof(CanDeepScan));
        OnPropertyChanged(nameof(CanRefreshDrives));
        OnPropertyChanged(nameof(CanInspectUsb));
        OnPropertyChanged(nameof(CanPreparePlan));
        OnPropertyChanged(nameof(CanBuildEfi));
        OnPropertyChanged(nameof(CanDownloadRecovery));
        OnPropertyChanged(nameof(CanCreateUsbDryRun));
        OnPropertyChanged(nameof(CanRunPreflight));
        OnPropertyChanged(nameof(CanConfirmTarget));
        OnPropertyChanged(nameof(CanSimulateWrite));
        OnPropertyChanged(nameof(CanCreateSupportBundle));

        OnPropertyChanged(nameof(ScanHardwareToolTip));
        OnPropertyChanged(nameof(DeepScanToolTip));
        OnPropertyChanged(nameof(RefreshDrivesToolTip));
        OnPropertyChanged(nameof(InspectUsbToolTip));
        OnPropertyChanged(nameof(PreparePlanToolTip));
        OnPropertyChanged(nameof(BuildEfiToolTip));
        OnPropertyChanged(nameof(DownloadRecoveryToolTip));
        OnPropertyChanged(nameof(CreateUsbDryRunToolTip));
        OnPropertyChanged(nameof(RunPreflightToolTip));
        OnPropertyChanged(nameof(ConfirmTargetToolTip));
        OnPropertyChanged(nameof(SimulateWriteToolTip));
        OnPropertyChanged(nameof(NextActionText));
    }

    private void OpenLog_OnClick(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null || !_logWindow.IsLoaded)
        {
            _logWindow = new LogWindow(ActivityLog)
            {
                Owner = this
            };
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
            ActivityLog.Info("Log", "Activity Log window opened.");
            return;
        }

        if (_logWindow.WindowState == WindowState.Minimized)
            _logWindow.WindowState = WindowState.Normal;

        _logWindow.Activate();
    }

    private async void CreateSupportBundle_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ActivityLog.Start(
                "Support Bundle",
                "Collecting and redacting CorePilot diagnostics…");

            var selectedSystem =
                (SystemCombo.SelectedItem as ISystemModule)?.DisplayName
                ?? "Not selected";

            var selectedVariant =
                (VariantCombo.SelectedItem as SystemVariant)?.DisplayName
                ?? "Not selected";

            var result = await _supportBundleService.CreateAsync(
                ActivityLog,
                new SupportBundleContext(
                    _workflowStateMachine.Current,
                    selectedSystem,
                    selectedVariant,
                    _hardwareReport,
                    _compatibilityReport,
                    _automationProfile,
                    _usbSafetyReport,
                    _opCoreStage));

            PlanStatus =
                $"Support bundle ready ✅ {result.FileCount} files · " +
                $"{result.SizeBytes / 1024d / 1024d:0.0} MB · " +
                result.BundlePath;

            ActivityLog.Success("Support Bundle", PlanStatus);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{result.BundlePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            PlanStatus = $"Support bundle failed: {ex.Message}";
            ActivityLog.Error("Support Bundle", PlanStatus, ex);
        }
    }

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
        RefreshActionAvailability();
    }

    private void InvalidateWorkflowAfter(
        MacOSWorkflowPhase preserveThrough,
        string reason)
    {
        _workflowStateMachine.InvalidateAfter(preserveThrough, reason);
        ActivityLog.Info("Workflow", $"Invalidated after {preserveThrough}: {reason}");
        UpdateWorkflowStatus();
    }

    private void AdvanceWorkflow(
        MacOSWorkflowPhase phase,
        string reason,
        DateTimeOffset? authorizationExpiresAt = null)
    {
        _workflowStateMachine.Advance(phase, reason, authorizationExpiresAt);
        ActivityLog.Info("Workflow", $"{phase}: {reason}");
        UpdateWorkflowStatus();
    }

    private async void DeepScan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DeepScanStatus = "Starting Hardware Sniffer…";
            ActivityLog.Start("Deep Scan", DeepScanStatus);
            var progress = new Progress<string>(message =>
            {
                DeepScanStatus = message;
                ActivityLog.Progress("Deep Scan", message);
            });
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
            ActivityLog.Success("Deep Scan", DeepScanStatus);
        }
        catch (Exception ex)
        {
            DeepScanStatus = $"Deep scan failed: {ex.Message}";
            ActivityLog.Error("Deep Scan", DeepScanStatus, ex);
        }
    }

    private async Task ScanHardwareAsync()
    {
        try
        {
            ScanStatus = "Scanning…";
            ActivityLog.Start("Hardware", "Scanning local hardware…");
            _hardwareReport = await _scanner.ScanAsync();
            _deepScanExport = null;
            _workflowStateMachine.Reset("Local hardware scan refreshed; previous downstream state revoked.");
            AdvanceWorkflow(
                MacOSWorkflowPhase.HardwareScanned,
                "Local hardware scan complete.");
            RefreshHardwareView();
            ScanStatus = $"Detected {HardwareItems.Count} hardware items";
            ActivityLog.Success("Hardware", ScanStatus);
            RunCompatibilityAnalysis();
        }
        catch (Exception ex)
        {
            ScanStatus = "Scan failed";
            PlanStatus = $"Hardware scan failed: {ex.Message}";
            ActivityLog.Error("Hardware", PlanStatus, ex);
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
        RefreshActionAvailability();
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
            ActivityLog.Start("USB Safety", UsbSafetyStatus);
            var report = await _usbSafetyInspector.InspectAsync(usb);
            _usbSafetyReport = report;
            _lastUsbWritePlan = null;
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;
            InvalidateWorkflowAfter(
                MacOSWorkflowPhase.ManifestVerified,
                "USB safety inspection refreshed; previous target authorization revoked.");
            UsbSafetyStatus = report.Summary;
            ActivityLog.Success("USB Safety", report.Summary);

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
            ActivityLog.Error("USB Safety", UsbSafetyStatus, ex);
        }
    }

    private async Task RefreshDrivesAsync()
    {
        try
        {
            ActivityLog.Start("Drives", "Refreshing physical disk list…");
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
            {
                PlanStatus = "No USB disk detected. Connect a USB flash drive and press Refresh drives.";
                ActivityLog.Warning("Drives", PlanStatus);
            }
            else
            {
                ActivityLog.Success("Drives", $"Detected {UsbDrives.Count} USB drive(s).");
            }
        }
        catch (Exception ex)
        {
            PlanStatus = $"Disk scan failed: {ex.Message}";
            ActivityLog.Error("Drives", PlanStatus, ex);
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
        RefreshActionAvailability();
    }

    private void VariantCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RunCompatibilityAnalysis();
        RefreshActionAvailability();
    }

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
            ActivityLog.Start("Plan", PlanStatus);
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
            ActivityLog.Success("Plan", PlanStatus);
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

            ActivityLog.Start("EFI Build", "Building and validating OpenCore EFI…");
            var progress = new Progress<string>(message =>
            {
                PlanStatus = message;
                ActivityLog.Progress("EFI Build", message);
            });
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
            AdvanceWorkflow(
                MacOSWorkflowPhase.EfiValidated,
                "EFI generated and passed ocvalidate + structural validation.");
            ActivityLog.Success("EFI Build", PlanStatus);
        }
        catch (Exception ex)
        {
            PlanStatus = $"EFI build failed: {ex.Message}";
            ActivityLog.Error("EFI Build", PlanStatus, ex);
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

            ActivityLog.Start("Recovery", "Downloading and verifying Apple Recovery…");
            var progress = new Progress<string>(message =>
            {
                PlanStatus = message;
                ActivityLog.Progress("Recovery", message);
            });
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
            AdvanceWorkflow(
                MacOSWorkflowPhase.RecoveryVerified,
                "Apple Recovery downloaded and verified.");
            AdvanceWorkflow(
                MacOSWorkflowPhase.ManifestVerified,
                "Final installer manifest created and re-verified.");

            PlanStatus =
                $"Apple Recovery + installer manifest ready ✅ " +
                $"{manifest.FileCount} files · " +
                $"manifest SHA256 {manifest.ManifestSha256[..16]}… · " +
                $"{result.OutputDirectory}.";
            ActivityLog.Success("Recovery", PlanStatus);
        }
        catch (Exception ex)
        {
            PlanStatus = $"Apple Recovery failed: {ex.Message}";
            ActivityLog.Error("Recovery", PlanStatus, ex);
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
            ActivityLog.Start("USB Dry-run", PlanStatus);
            var freshReport = await _usbSafetyInspector.InspectAsync(usb);

            if (!freshReport.IdentityFingerprint.Equals(
                    _usbSafetyReport.IdentityFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                _usbSafetyReport = freshReport;
                _lastUsbWritePlan = null;
                UsbSafetyStatus = freshReport.Summary;
                PlanStatus = "USB identity changed since the previous inspection. Dry-run plan was rejected; inspect the target again.";
                ActivityLog.Warning("USB Dry-run", PlanStatus);
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
            ActivityLog.Success("USB Dry-run", PlanStatus);
        }
        catch (Exception ex)
        {
            _lastUsbWritePlan = null;
            PlanStatus = $"USB dry-run failed: {ex.Message}";
            ActivityLog.Error("USB Dry-run", PlanStatus, ex);
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
            ActivityLog.Start("Preflight", PlanStatus);
            var freshTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (freshTarget.IsBlocked)
            {
                _lastUsbExecutionPreflight = null;
                _lastUsbTypedConfirmation = null;
                _usbSafetyReport = freshTarget;
                UsbSafetyStatus = freshTarget.Summary;
                PlanStatus = $"Execution preflight BLOCKED: {freshTarget.Summary}";
                ActivityLog.Warning("Preflight", PlanStatus);
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
            ActivityLog.Success("Preflight", PlanStatus);
        }
        catch (Exception ex)
        {
            _lastUsbExecutionPreflight = null;
            _lastUsbTypedConfirmation = null;
            PlanStatus = $"Execution preflight failed: {ex.Message}";
            ActivityLog.Error("Preflight", PlanStatus, ex);
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
            ActivityLog.Start("Confirmation", PlanStatus);
            var freshTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (freshTarget.IsBlocked)
            {
                _lastUsbTypedConfirmation = null;
                _lastUsbExecutionPreflight = null;
                _usbSafetyReport = freshTarget;
                UsbSafetyStatus = freshTarget.Summary;
                PlanStatus = $"Confirmation BLOCKED: {freshTarget.Summary}";
                ActivityLog.Warning("Confirmation", PlanStatus);
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
            ActivityLog.Success("Confirmation", PlanStatus);
        }
        catch (Exception ex)
        {
            _lastUsbTypedConfirmation = null;
            PlanStatus = $"Confirmation rejected: {ex.Message}";
            ActivityLog.Error("Confirmation", PlanStatus, ex);
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
            ActivityLog.Start("Write Simulation", PlanStatus);
            var freshTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (freshTarget.IsBlocked)
            {
                PlanStatus = $"Simulation BLOCKED: {freshTarget.Summary}";
                ActivityLog.Warning("Write Simulation", PlanStatus);
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
            ActivityLog.Success("Write Simulation", PlanStatus);
        }
        catch (Exception ex)
        {
            PlanStatus = $"Write simulation failed: {ex.Message}";
            ActivityLog.Error("Write Simulation", PlanStatus, ex);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
