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
    private readonly OnlineSourceCatalogService _onlineSources = new();
    private readonly HardwareSnifferBridge _hardwareSniffer = new();
    private readonly HardwareSnifferReportParser _hardwareSnifferParser = new();
    private readonly MacOSCompatibilityAnalyzer _macAnalyzer = new();
    private readonly GenericCompatibilityAnalyzer _genericAnalyzer = new();
    private readonly MacOSAutomationPlanner _macAutomationPlanner = new();
    private readonly OpCoreSimplifyStager _opCoreStager = new();
    private readonly OpCoreSimplifyBuilder _opCoreBuilder = new();
    private readonly OpCoreDownloadedComponentAuditService _componentAuditService = new();
    private readonly AppleRecoveryDownloader _appleRecoveryDownloader = new();
    private readonly InstallerManifestService _installerManifestService = new();
    private readonly UsbTargetSafetyInspector _usbSafetyInspector = new();
    private readonly MacOSUsbWritePlanService _usbWritePlanService = new();
    private readonly MacOSUsbExecutionPreflightService _usbExecutionPreflightService = new();
    private readonly MacOSUsbTypedConfirmationService _usbTypedConfirmationService = new();
    private readonly MacOSUsbPhysicalWriteService _usbPhysicalWriteService = new();
    private readonly MacOSWorkflowStateMachine _workflowStateMachine = new();
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
    private OnlineSourceSnapshot? _lastOnlineSourceSnapshot;
    private bool _verificationCompleted;

    private string _scanStatus = "Not scanned";
    private string _deepScanStatus = "Deep scan not run. It downloads the official Hardware-Sniffer-CLI release on first use.";
    private string _planStatus = "Select a system and USB drive, then prepare an installation plan.";
    private string _usbSafetyStatus = "USB target not inspected. No physical-disk writes are enabled.";
    private string _compatibilitySummary = "Scan hardware and select macOS to run compatibility checks.";
    private string _compatibilityVerdict = "NOT CHECKED";
    private string _compatibilityInstallPath = "Press Verify to determine whether the selected system is installable on this computer.";
    private string _compatibilityRequirements = "Required fixes, patches, drivers and boot arguments will appear here.";
    private string _workflowStatus = "Workflow · IDLE · Not started.";

    public ObservableCollection<ISystemModule> Systems { get; } = [];
    public ObservableCollection<SystemVariant> Variants { get; } = [];
    public ObservableCollection<UsbDriveInfo> UsbDrives { get; } = [];
    public ObservableCollection<HardwareDisplayItem> HardwareItems { get; } = [];
    public ObservableCollection<CompatibilityFinding> CompatibilityItems { get; } = [];
    public ActivityLogService ActivityLog => App.Log;

    public bool CanChangeInputs => !ActivityLog.IsBusy;
    public bool CanCreateSupportBundle => !ActivityLog.IsBusy;
    public bool CanVerify => !ActivityLog.IsBusy;
    public bool CanWriteToDisk =>
        !ActivityLog.IsBusy &&
        _verificationCompleted &&
        _compatibilityReport?.CanProceed == true &&
        SystemCombo.SelectedItem is ISystemModule { Id: "macos" } &&
        _automationProfile is { CanBuildEfi: true, RequiresReview: false };
    public string WriteToDiskToolTip =>
        !_verificationCompleted
            ? "Run Verify first. Critical online sources must be live before writing."
            : _compatibilityReport?.CanProceed != true
                ? "The selected system did not pass compatibility verification."
                : SystemCombo.SelectedItem is not ISystemModule { Id: "macos" }
                    ? "Windows/Linux verification is available, but their physical media writer is not enabled in this build."
                    : _automationProfile is null
                        ? "This Apple-Mac target needs the dedicated native-media path; the OpenCore writer is intentionally not used."
                        : _automationProfile.RequiresReview || !_automationProfile.CanBuildEfi
                            ? "The macOS automation profile requires review before physical writing."
                            : "Build all hidden safety stages, require exact typed confirmation, then write the verified macOS installer to the selected USB.";

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

    public string CompatibilityVerdict
    {
        get => _compatibilityVerdict;
        private set { _compatibilityVerdict = value; OnPropertyChanged(); }
    }

    public string CompatibilityInstallPath
    {
        get => _compatibilityInstallPath;
        private set { _compatibilityInstallPath = value; OnPropertyChanged(); }
    }

    public string CompatibilityRequirements
    {
        get => _compatibilityRequirements;
        private set { _compatibilityRequirements = value; OnPropertyChanged(); }
    }

    public string WorkflowStatus
    {
        get => _workflowStatus;
        private set { _workflowStatus = value; OnPropertyChanged(); }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ActivityLog.Info("UI", "Main window initialized.");
        ActivityLog.PropertyChanged += ActivityLog_OnPropertyChanged;
        ActivityLog.EntryAdded += ActivityLog_OnEntryAdded;
        _authorizationTimer.Tick += AuthorizationTimer_OnTick;
        _authorizationTimer.Start();
        Closed += (_, _) =>
        {
            _authorizationTimer.Stop();
            _authorizationTimer.Tick -= AuthorizationTimer_OnTick;
            ActivityLog.PropertyChanged -= ActivityLog_OnPropertyChanged;
            ActivityLog.EntryAdded -= ActivityLog_OnEntryAdded;
        };

        Systems.Add(new MacOSModule());
        Systems.Add(new WindowsModule());
        Systems.Add(new LinuxModule());

        SystemCombo.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            await RefreshDrivesAsync(silentNoUsb: true);
            PlanStatus = "Choose a system and version, then press Verify. USB is not required for verification.";
            UsbSafetyStatus = "USB is optional for verification. Connect it only when you are ready to write.";
        };
    }

    private async void Verify_OnClick(object sender, RoutedEventArgs e)
    {
        if (SystemCombo.SelectedItem is not ISystemModule module ||
            VariantCombo.SelectedItem is not SystemVariant target)
        {
            PlanStatus = "Choose a system and version first.";
            return;
        }

        _verificationCompleted = false;
        _lastOnlineSourceSnapshot = null;
        RefreshActionAvailability();

        try
        {
            ActivityLog.Start(
                "Online sources",
                $"Refreshing current {module.DisplayName} sources…");

            _lastOnlineSourceSnapshot =
                await _onlineSources.ResolveForSystemAsync(module.Id);

            foreach (var source in _lastOnlineSourceSnapshot.Sources)
            {
                var state = source.Live && source.Success
                    ? "LIVE"
                    : source.Success
                        ? "CACHE"
                        : "FAILED";

                var version = string.IsNullOrWhiteSpace(source.Version)
                    ? ""
                    : $" · {source.Version}";

                ActivityLog.Info(
                    "Online source",
                    $"{state} · {source.Name}{version} · {source.SourceUrl ?? source.Repository ?? "no URL"}");
            }

            ActivityLog.Progress(
                "Verification",
                $"Online sources refreshed: {_lastOnlineSourceSnapshot.LiveCount}/{_lastOnlineSourceSnapshot.Sources.Count} live.");
        }
        catch (Exception ex)
        {
            ActivityLog.Info(
                "Online sources",
                $"Online source refresh could not complete: {ex.Message}");
        }

        PlanStatus = $"Verifying {target.DisplayName} against this computer…";
        await ScanHardwareAsync();

        if (_compatibilityReport is null)
        {
            PlanStatus = "Verification could not be completed. Open the Activity Log for details.";
            RefreshActionAvailability();
            return;
        }

        var onlineReady =
            _lastOnlineSourceSnapshot is not null &&
            _lastOnlineSourceSnapshot.CriticalFailures == 0;

        _verificationCompleted =
            _compatibilityReport.CanProceed &&
            onlineReady;

        RefreshActionAvailability();

        if (_compatibilityReport.CanProceed && onlineReady)
        {
            var nativeApple =
                _hardwareReport is not null &&
                MacOSCompatibilityAnalyzer.IsGenuineAppleMac(_hardwareReport);

            var sourceSuffix =
                $" Online sources: {_lastOnlineSourceSnapshot!.LiveCount}/{_lastOnlineSourceSnapshot.Sources.Count} live.";

            PlanStatus = (nativeApple
                ? $"Verified ✅ {target.DisplayName} is compatible with this Apple Mac. USB was not required for this check."
                : $"Verified ✅ {_compatibilityReport.Summary}. USB was not required for this check.")
                + sourceSuffix;

            ActivityLog.Success("Verification", PlanStatus);
        }
        else if (_compatibilityReport.CanProceed)
        {
            var failures = _lastOnlineSourceSnapshot?.CriticalFailures ?? 1;
            PlanStatus =
                $"Compatibility passed, but writing is not ready: {failures} critical online source(s) were not refreshed live. " +
                "Reconnect to the internet and run Verify again.";
            ActivityLog.Warning("Verification", PlanStatus);
        }
        else
        {
            PlanStatus =
                $"Verification finished: {CompatibilityVerdict}. {_compatibilityReport.Summary}. " +
                "Open Compatibility to see the exact required fixes and installation path.";
            ActivityLog.Warning("Verification", PlanStatus);
        }
    }

    private async void WriteToDisk_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_verificationCompleted || _compatibilityReport?.CanProceed != true)
        {
            PlanStatus = "Run Verify successfully before writing to a disk.";
            return;
        }

        if (SystemCombo.SelectedItem is not ISystemModule { Id: "macos" } ||
            VariantCombo.SelectedItem is not SystemVariant variant)
        {
            PlanStatus =
                "Windows/Linux verification is available, but their physical media writer is not enabled in this build.";
            return;
        }

        if (_automationProfile is null)
        {
            PlanStatus =
                "This target uses the genuine-Apple/native path. CorePilot will not substitute the Hackintosh/OpenCore writer.";
            return;
        }

        if (!_automationProfile.CanBuildEfi || _automationProfile.RequiresReview)
        {
            PlanStatus =
                "Automatic physical writing is blocked because the macOS automation profile requires review.";
            return;
        }

        if (UsbCombo.SelectedItem is not UsbDriveInfo)
            await RefreshDrivesAsync(silentNoUsb: true);

        if (UsbCombo.SelectedItem is not UsbDriveInfo usb)
        {
            PlanStatus = "Connect a USB drive, select it, then press Write to disk again.";
            UsbSafetyStatus = "No USB target selected.";
            return;
        }

        try
        {
            ActivityLog.Start(
                "Write workflow",
                $"Re-validating live sources for {variant.DisplayName}…");

            _lastOnlineSourceSnapshot =
                await _onlineSources.ResolveForSystemAsync("macos");

            if (_lastOnlineSourceSnapshot.CriticalFailures != 0)
                throw new InvalidOperationException(
                    $"{_lastOnlineSourceSnapshot.CriticalFailures} critical online source(s) are not live. Physical writing was blocked.");

            if (_deepScanExport is null)
            {
                DeepScanStatus = "Running automatic Hardware Sniffer deep scan…";
                ActivityLog.Progress("Write workflow", DeepScanStatus);

                var deepProgress = new Progress<string>(message =>
                {
                    DeepScanStatus = message;
                    ActivityLog.Progress("Deep Scan", message);
                });

                _deepScanExport = await _hardwareSniffer.ExportAsync(deepProgress);
                _hardwareReport ??= await _scanner.ScanAsync();
                _hardwareReport = await _hardwareSnifferParser.MergeAsync(
                    _deepScanExport.ReportPath,
                    _hardwareReport);

                RefreshHardwareView();
                _workflowStateMachine.Reset(
                    "Automatic Deep Scan refreshed hardware identity.");
                AdvanceWorkflow(
                    MacOSWorkflowPhase.DeepScanned,
                    "Hardware Sniffer Report.json + ACPI imported.");
                RunCompatibilityAnalysis();

                if (_compatibilityReport?.CanProceed != true ||
                    _automationProfile is not { CanBuildEfi: true, RequiresReview: false })
                    throw new InvalidOperationException(
                        "Deep Scan changed the compatibility result. Run Verify again and review the Compatibility tab.");
            }

            ActivityLog.Progress(
                "Write workflow",
                "Staging the current verified OpenCore automation engine…");

            _opCoreStage = await _opCoreStager.StageAsync(
                _deepScanExport.ReportPath,
                _deepScanExport.AcpiDirectory,
                _automationProfile,
                new Progress<string>(message =>
                    ActivityLog.Progress("Workspace", message)));

            AdvanceWorkflow(
                MacOSWorkflowPhase.WorkspaceStaged,
                $"OpenCore workspace staged for {variant.DisplayName}.");

            ActivityLog.Progress(
                "Write workflow",
                "Building and validating EFI…");

            _lastEfiBuild = await _opCoreBuilder.BuildAsync(
                _opCoreStage,
                _automationProfile,
                new Progress<string>(message =>
                    ActivityLog.Progress("EFI Build", message)));

            ActivityLog.Progress(
                "Supply chain",
                "Auditing downloaded OpenCore/kext source URLs and SHA-256 metadata…");

            var componentAudit = await _componentAuditService.AuditAsync(
                _opCoreStage,
                _lastOnlineSourceSnapshot);

            ActivityLog.Info(
                "Supply chain",
                $"{componentAudit.ComponentCount} downloaded component(s) have HTTPS sources, SHA-256 metadata and integrity manifests · audit {componentAudit.AuditSha256[..16]}…");

            AdvanceWorkflow(
                MacOSWorkflowPhase.EfiValidated,
                "EFI passed ocvalidate, structural validation and downloaded-component integrity audit.");

            ActivityLog.Progress(
                "Write workflow",
                "Downloading and verifying Apple Recovery…");

            _lastRecovery = await _appleRecoveryDownloader.DownloadAsync(
                _opCoreStage,
                _automationProfile,
                _lastEfiBuild,
                new Progress<string>(message =>
                    ActivityLog.Progress("Recovery", message)));

            AdvanceWorkflow(
                MacOSWorkflowPhase.RecoveryVerified,
                "Apple Recovery downloaded and verified.");

            _lastInstallerManifest = await _installerManifestService.CreateAsync(
                _opCoreStage,
                _automationProfile,
                _lastEfiBuild,
                _lastRecovery);

            var manifestVerification = await _installerManifestService.VerifyAsync(
                _lastInstallerManifest.ManifestPath,
                _opCoreStage.WorkspaceDirectory);

            if (!manifestVerification.Success)
                throw new InvalidOperationException(
                    "Installer manifest verification failed: " +
                    string.Join("; ", manifestVerification.Errors.Take(4)));

            AdvanceWorkflow(
                MacOSWorkflowPhase.ManifestVerified,
                "Final installer manifest created and re-verified.");

            ActivityLog.Progress(
                "Supply chain",
                "Re-validating all live sources immediately before destructive USB authorization…");

            var finalSourceSnapshot =
                await _onlineSources.ResolveForSystemAsync("macos");

            if (finalSourceSnapshot.CriticalFailures != 0)
                throw new InvalidOperationException(
                    $"{finalSourceSnapshot.CriticalFailures} critical online source(s) are no longer live. Physical writing was blocked.");

            var currentOpCoreSimplify = finalSourceSnapshot.Sources.SingleOrDefault(x =>
                x.Id.Equals(
                    "macos.opcore-simplify",
                    StringComparison.OrdinalIgnoreCase));

            if (currentOpCoreSimplify?.ResolvedRef is null ||
                !currentOpCoreSimplify.ResolvedRef.Equals(
                    _opCoreStage.UpstreamCommit,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "OpCore-Simplify changed upstream while the installer was being prepared. Run Write to disk again so CorePilot rebuilds from the new verified commit.");

            _ = await _componentAuditService.AuditAsync(
                _opCoreStage,
                finalSourceSnapshot);

            _lastOnlineSourceSnapshot = finalSourceSnapshot;

            ActivityLog.Progress(
                "Write workflow",
                "Inspecting the exact physical USB target…");

            _usbSafetyReport = await _usbSafetyInspector.InspectAsync(usb);
            UsbSafetyStatus = _usbSafetyReport.Summary;

            if (_usbSafetyReport.IsBlocked)
                throw new InvalidOperationException(
                    $"USB target is blocked: {_usbSafetyReport.Summary}");

            AdvanceWorkflow(
                MacOSWorkflowPhase.UsbInspected,
                $"USB safety inspected: {_usbSafetyReport.LevelText}.");

            _lastUsbWritePlan = await _usbWritePlanService.CreateDryRunAsync(
                _opCoreStage,
                _lastInstallerManifest,
                _usbSafetyReport);

            AdvanceWorkflow(
                MacOSWorkflowPhase.DryRunPlanned,
                "Manifest-bound USB write plan created and verified.");

            var preflightTarget = await _usbSafetyInspector.InspectAsync(usb);
            if (preflightTarget.IsBlocked ||
                !preflightTarget.IdentityFingerprint.Equals(
                    _usbSafetyReport.IdentityFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "USB identity or safety state changed before execution preflight.");

            _usbSafetyReport = preflightTarget;
            _lastUsbExecutionPreflight =
                await _usbExecutionPreflightService.CreateAsync(
                    _opCoreStage,
                    _lastInstallerManifest,
                    _lastUsbWritePlan,
                    preflightTarget);

            AdvanceWorkflow(
                MacOSWorkflowPhase.PreflightReady,
                "Atomic execution preflight passed.",
                _lastUsbExecutionPreflight.ExpiresAt);

            var phrase = _lastUsbExecutionPreflight.RequiredConfirmationPhrase;
            var typed = Microsoft.VisualBasic.Interaction.InputBox(
                "CorePilot is ready to ERASE the selected USB disk.\n\n" +
                $"Disk: {preflightTarget.DiskIndex} · {preflightTarget.Model}\n" +
                $"Size: {preflightTarget.SizeBytes / 1024d / 1024d / 1024d:0.#} GB\n\n" +
                "Type this exact phrase to continue:\n\n" +
                phrase,
                "CorePilot — confirm physical USB erase",
                "");

            if (!typed.Equals(phrase, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Exact destructive confirmation phrase was not entered. Nothing was written.");

            var confirmationTarget = await _usbSafetyInspector.InspectAsync(usb);
            if (confirmationTarget.IsBlocked)
                throw new InvalidOperationException(
                    $"USB became unsafe before confirmation: {confirmationTarget.Summary}");

            _lastUsbTypedConfirmation =
                await _usbTypedConfirmationService.CreateAsync(
                    _opCoreStage,
                    _lastInstallerManifest,
                    _lastUsbWritePlan,
                    _lastUsbExecutionPreflight,
                    confirmationTarget,
                    typed);

            AdvanceWorkflow(
                MacOSWorkflowPhase.Confirmed,
                "Exact typed confirmation accepted.",
                _lastUsbTypedConfirmation.ExpiresAt);

            var finalTarget = await _usbSafetyInspector.InspectAsync(usb);
            if (finalTarget.IsBlocked ||
                !finalTarget.IdentityFingerprint.Equals(
                    confirmationTarget.IdentityFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "USB identity changed immediately before physical writing.");

            PlanStatus =
                $"Writing verified installer to Disk {finalTarget.DiskIndex}…";
            ActivityLog.Progress("Physical write", PlanStatus);

            var physicalResult = await _usbPhysicalWriteService.WriteAsync(
                _opCoreStage,
                _lastEfiBuild,
                _lastRecovery,
                _lastInstallerManifest,
                _lastUsbWritePlan,
                _lastUsbExecutionPreflight,
                _lastUsbTypedConfirmation,
                finalTarget,
                new Progress<string>(message =>
                {
                    PlanStatus = message;
                    ActivityLog.Progress("Physical write", message);
                }));

            AdvanceWorkflow(
                MacOSWorkflowPhase.Written,
                "Verified installer was physically written to the confirmed USB target.");

            PlanStatus =
                $"USB ready ✅ Disk {physicalResult.TargetDiskIndex} · {physicalResult.DriveLetter} · " +
                $"{physicalResult.VerifiedFiles} files re-verified · " +
                $"write transcript SHA256 {physicalResult.TranscriptSha256[..16]}….";
            ActivityLog.Success("Physical write", PlanStatus);
        }
        catch (Exception ex)
        {
            PlanStatus = $"Write stopped safely: {ex.Message}";
            ActivityLog.Error("Write workflow", PlanStatus, ex);
        }
        finally
        {
            RefreshActionAvailability();
        }
    }

    private async void UsbCombo_OnDropDownOpened(object sender, EventArgs e) =>
        await RefreshDrivesAsync(silentNoUsb: true);

    private void ActivityLog_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActivityLogService.IsBusy) or
            nameof(ActivityLogService.CurrentStatus) or
            nameof(ActivityLogService.CurrentArea))
        {
            Dispatcher.BeginInvoke(RefreshActionAvailability);
        }
    }

    private void ActivityLog_OnEntryAdded(
        object? sender,
        ActivityLogEntry entry)
    {
        if (entry.Level != ActivityLogLevel.Error)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded)
                return;

            ShowLogWindow(entry);
        });
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
        OnPropertyChanged(nameof(CanCreateSupportBundle));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(CanWriteToDisk));
        OnPropertyChanged(nameof(WriteToDiskToolTip));
    }

    private void OpenLog_OnClick(object sender, RoutedEventArgs e) =>
        ShowLogWindow();

    private void ShowLogWindow(ActivityLogEntry? focusEntry = null)
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
        }

        if (_logWindow.WindowState == WindowState.Minimized)
            _logWindow.WindowState = WindowState.Normal;

        if (focusEntry is not null)
            _logWindow.FocusEntry(focusEntry);

        _logWindow.Activate();
        _logWindow.Topmost = true;
        _logWindow.Topmost = false;
        _logWindow.Focus();
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
                    _opCoreStage,
                    _lastOnlineSourceSnapshot));

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

    private async Task ScanHardwareAsync()
    {
        try
        {
            ScanStatus = "Scanning…";
            ActivityLog.Start("Hardware", "Scanning local hardware…");
            _hardwareReport = await _scanner.ScanAsync();
            _deepScanExport = null;
            _verificationCompleted = false;
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

    private async Task RefreshDrivesAsync(bool silentNoUsb = false)
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
                UsbSafetyStatus = "USB is optional for verification. Connect it only when you are ready to write.";

                if (!silentNoUsb)
                    PlanStatus = "No USB disk detected. Verification still works without one.";

                ActivityLog.Success(
                    "Drives",
                    "Physical disk list refreshed. No USB disk detected; verification can continue without a target disk.");
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

        _verificationCompleted = false;
        CompatibilityItems.Clear();
        _compatibilityReport = null;
        _automationProfile = null;
        CompatibilitySummary = "Press Verify to check this system against the detected hardware.";
        ResetCompatibilityDecision("Press Verify to evaluate this system.");
        PlanStatus = "Press Verify. USB is not required for compatibility checking.";
        InvalidateWorkflowAfter(
            HardwareBaselinePhase,
            "System selection changed; verification required.");
        RefreshActionAvailability();
    }

    private void VariantCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _verificationCompleted = false;
        CompatibilityItems.Clear();
        _compatibilityReport = null;
        _automationProfile = null;
        CompatibilitySummary = "Press Verify to check this version against the detected hardware.";
        ResetCompatibilityDecision("Press Verify to evaluate this version.");
        PlanStatus = "Press Verify. USB is not required for compatibility checking.";
        InvalidateWorkflowAfter(
            HardwareBaselinePhase,
            "Version selection changed; verification required.");
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

        if (_hardwareReport is null)
        {
            CompatibilitySummary = "Scan hardware first.";
            ResetCompatibilityDecision("Hardware has not been scanned yet.");
            return;
        }

        if (SystemCombo.SelectedItem is not ISystemModule system ||
            VariantCombo.SelectedItem is not SystemVariant target)
        {
            CompatibilitySummary = "Choose a system and version first.";
            ResetCompatibilityDecision("Choose a system and version first.");
            return;
        }

        if (system.Id == "macos")
        {
            _compatibilityReport = _macAnalyzer.Analyze(_hardwareReport, target);
            _automationProfile = MacOSCompatibilityAnalyzer.IsGenuineAppleMac(_hardwareReport)
                ? null
                : _macAutomationPlanner.Build(_hardwareReport, target, _compatibilityReport);

            AdvanceWorkflow(
                MacOSWorkflowPhase.CompatibilityReady,
                $"Compatibility evaluated for {target.DisplayName}.");
        }
        else
        {
            _compatibilityReport = _genericAnalyzer.Analyze(
                system.Id,
                _hardwareReport,
                target);
            _automationProfile = null;
            }

        foreach (var finding in _compatibilityReport.Findings)
            CompatibilityItems.Add(finding);

        CompatibilitySummary = _compatibilityReport.Summary;
        UpdateCompatibilityDecision(system, target);
    }

    private void ResetCompatibilityDecision(string message)
    {
        CompatibilityVerdict = "NOT CHECKED";
        CompatibilityInstallPath = message;
        CompatibilityRequirements =
            "Required fixes, patches, drivers and boot arguments will appear here after Verify.";
    }

    private void UpdateCompatibilityDecision(
        ISystemModule system,
        SystemVariant target)
    {
        if (_compatibilityReport is null || _hardwareReport is null)
        {
            ResetCompatibilityDecision("Compatibility analysis has not completed.");
            return;
        }

        var blockerCount = _compatibilityReport.Findings.Count(x =>
            x.State == CompatibilityState.Blocked);
        var unknownCount = _compatibilityReport.Findings.Count(x =>
            x.State == CompatibilityState.Unknown);
        var actionCount = _compatibilityReport.Findings.Count(x =>
            x.State == CompatibilityState.ActionRequired);
        var warningCount = _compatibilityReport.Findings.Count(x =>
            x.State == CompatibilityState.Warning);

        CompatibilityVerdict = blockerCount > 0
            ? "NOT READY TO INSTALL"
            : unknownCount > 0
                ? "REVIEW REQUIRED"
                : actionCount > 0
                    ? "INSTALLABLE WITH REQUIRED FIXES"
                    : warningCount > 0
                        ? "COMPATIBLE WITH WARNINGS"
                        : "READY TO INSTALL";

        if (system.Id == "macos" &&
            MacOSCompatibilityAnalyzer.IsGenuineAppleMac(_hardwareReport))
        {
            if (target.Id == "ventura-13" &&
                _compatibilityReport.CanProceed)
            {
                CompatibilityInstallPath =
                    "Installation path: native Apple installer. OpenCore/OCLP patches are not required for this target.";
            }
            else
            {
                var oclp = _lastOnlineSourceSnapshot?.Sources.FirstOrDefault(x =>
                    x.Id.Equals("macos.oclp", StringComparison.OrdinalIgnoreCase));

                var oclpStatus = oclp is { Success: true }
                    ? $" Current online OCLP source: {(string.IsNullOrWhiteSpace(oclp.Version) ? "current release" : oclp.Version)} · {(oclp.Live ? "LIVE" : "CACHE")}."
                    : "";

                CompatibilityInstallPath =
                    "Installation path: OpenCore Legacy Patcher is required for this newer macOS. CorePilot keeps Write to disk blocked until that legacy-Mac path is explicitly supported and verified." +
                    oclpStatus;
            }
        }
        else if (system.Id == "macos")
        {
            CompatibilityInstallPath =
                "Installation path: CorePilot OpenCore/Hackintosh workflow. Deep Scan resolves the exact EFI, kexts, patches and boot arguments before writing.";
        }
        else
        {
            CompatibilityInstallPath =
                $"Installation path: {system.DisplayName} compatibility is verified here; physical media writing for this system is not enabled in this build.";
        }

        var requirements = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRequirement(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var normalized = value.Trim();
            if (seen.Add(normalized))
                requirements.Add(normalized);
        }

        foreach (var finding in _compatibilityReport.Findings.Where(x =>
                     x.State is CompatibilityState.Blocked
                         or CompatibilityState.ActionRequired
                         or CompatibilityState.Unknown))
            AddRequirement(finding.SuggestedAction);

        foreach (var patch in _compatibilityReport.RequiredPatches)
            AddRequirement($"Patch: {patch}");

        foreach (var kext in _compatibilityReport.RequiredKexts)
            AddRequirement($"Driver/kext: {kext}");

        foreach (var argument in _compatibilityReport.BootArguments)
            AddRequirement($"Boot argument: {argument}");

        CompatibilityRequirements = requirements.Count == 0
            ? "No additional fixes, patches, kexts or boot arguments are required by the current compatibility result."
            : string.Join(
                Environment.NewLine,
                requirements.Select(x => "• " + x));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
