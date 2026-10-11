using System.Collections.ObjectModel;
using System.IO;
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
    private readonly WindowsIsoPreparationService _windowsIsoPreparation;
    private readonly LinuxIsoPreparationService _linuxIsoPreparation = new();
    private readonly WindowsInstallerUsbWriter _windowsUsbWriter = new();
    private readonly LinuxRawUsbWriter _linuxUsbWriter = new();
    private readonly MacOSAutomationPlanner _macAutomationPlanner = new();
    private readonly MacOSAutoResolutionService _macAutoResolver;
    private readonly MacOSAutomationProfileStore _automationProfileStore = new();
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
    private MacOSAutoResolutionResult? _autoResolution;
    private InstallationPreparationResult? _preparationResult;
    private PreparedIsoImage? _preparedIso;
    private WindowsMediaOptions? _preparedWindowsMediaOptions;
    private GenericUsbWriteResult? _lastGenericUsbWrite;
    private string? _preparationFailure;
    private OnlineSourceSnapshot? _lastOnlineSourceSnapshot;
    private bool _verificationCompleted;
    private InstallationTargetMode _targetMode = InstallationTargetMode.ThisComputer;
    private bool _windowsCompatibilityMedia;

    private string _scanStatus = "Not scanned";
    private string _deepScanStatus = "Deep scan not run. It downloads the official Hardware-Sniffer-CLI release on first use.";
    private string _planStatus = "Choose a system and version, then press Verify. USB is only needed after CorePilot reports READY TO WRITE.";
    private string _usbSafetyStatus = "USB is optional during Verify. Connect/select it only when the prepared system is ready to write.";
    private string _compatibilitySummary = "Select a system and press Verify. CorePilot will scan hardware, search for solutions, configure and validate an installation path.";
    private string _compatibilityVerdict = "NOT PREPARED";
    private string _compatibilityInstallPath = "No installation path has been prepared yet.";
    private string _compatibilityRequirements = "Required fixes, patches, drivers and boot arguments will appear here.";
    private string _compatibilityAutoConfiguration = "Automatic configuration has not run yet.";
    private string _workflowStatus = "Workflow · IDLE · Not started.";
    private string _targetModeStatus = "This computer · Verify uses the hardware detected on this PC.";

    public ObservableCollection<ISystemModule> Systems { get; } = [];
    public ObservableCollection<SystemVariant> Variants { get; } = [];
    public ObservableCollection<UsbDriveInfo> UsbDrives { get; } = [];
    public ObservableCollection<HardwareDisplayItem> HardwareItems { get; } = [];
    public ObservableCollection<CompatibilityFinding> CompatibilityItems { get; } = [];
    public ObservableCollection<PreparationItem> PreparationItems { get; } = [];
    public ActivityLogService ActivityLog => App.Log;

    public bool CanChangeInputs => !ActivityLog.IsBusy;
    public bool CanCreateSupportBundle => !ActivityLog.IsBusy;
    public bool CanVerify => !ActivityLog.IsBusy;
    public bool CanWriteToDisk =>
        !ActivityLog.IsBusy &&
        _verificationCompleted &&
        _preparationResult?.ReadyToWrite == true;
    public string WriteToDiskToolTip =>
        _preparationResult is null
            ? "Run Verify first. CorePilot must scan the hardware, search for solutions, configure the target and validate the result."
            : _preparationResult.ReadyToWrite
                ? "The selected installation target is prepared and validated. Write the prepared system to the selected USB."
                : _preparationResult.SystemPrepared
                    ? "The system configuration is prepared, but a guarded physical writer for this installation path is not available yet."
                    : _preparationResult.ManualCount > 0
                        ? "CorePilot found a possible installation path, but a required manual hardware or firmware action remains."
                        : "CorePilot searched the available paths but unresolved items still prevent a safe prepared installation.";

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

    public string CompatibilityAutoConfiguration
    {
        get => _compatibilityAutoConfiguration;
        private set { _compatibilityAutoConfiguration = value; OnPropertyChanged(); }
    }

    public string WorkflowStatus
    {
        get => _workflowStatus;
        private set { _workflowStatus = value; OnPropertyChanged(); }
    }

    public string TargetModeStatus
    {
        get => _targetModeStatus;
        private set { _targetModeStatus = value; OnPropertyChanged(); }
    }

    public MainWindow()
    {
        InitializeComponent();
        _macAutoResolver = new MacOSAutoResolutionService(_onlineSources);
        _windowsIsoPreparation = new WindowsIsoPreparationService(_onlineSources);
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
        UpdateTargetModeUi();
        Loaded += async (_, _) =>
        {
            await RefreshDrivesAsync(silentNoUsb: true);
            PlanStatus = "Choose a system, version and target computer, then press Verify. CorePilot will prepare and validate the selected installation path automatically.";
            UsbSafetyStatus = "USB is optional while preparing the system. Connect it when CorePilot reports READY TO WRITE.";
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
        _preparationResult = null;
        _preparedIso = null;
        _preparedWindowsMediaOptions = null;
        _lastGenericUsbWrite = null;
        _preparationFailure = null;
        _lastOnlineSourceSnapshot = null;
        RefreshActionAvailability();

        if (_targetMode == InstallationTargetMode.OtherComputer &&
            module.Id == "macos")
        {
            PrepareCompatibilityForOtherComputer(module, target);
            _preparationResult = BuildPreparationResult(module, target);
            _verificationCompleted = true;
            ApplyPreparationResultToUi(module, target);
            RefreshActionAvailability();

            PlanStatus =
                "TARGET HARDWARE REQUIRED · macOS preparation is hardware-specific. " +
                "Run CorePilot on the target Mac/PC in This computer mode, or use a future target-hardware import path.";
            ActivityLog.Warning("Preparation", PlanStatus);
            return;
        }

        try
        {
            ActivityLog.Start(
                "Online sources",
                $"Refreshing current {module.DisplayName} sources…");

            _lastOnlineSourceSnapshot =
                await _onlineSources.ResolveForTargetAsync(module.Id, target.Id);

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

            var preparationKnowledge = (
                await Task.WhenAll(
                    _onlineSources.GetKnowledgeSourcesAsync(module.Id, "installation"),
                    _onlineSources.GetKnowledgeSourcesAsync(module.Id, "configuration"),
                    _onlineSources.GetKnowledgeSourcesAsync(module.Id, "remediation"),
                    _onlineSources.GetKnowledgeSourcesAsync(module.Id, "media-writer")))
                .SelectMany(x => x)
                .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (preparationKnowledge.Length > 0)
            {
                ActivityLog.Info(
                    "Preparation knowledge",
                    $"Loaded {preparationKnowledge.Length} applicable guide/tool source(s): " +
                    string.Join(", ", preparationKnowledge.Select(x => x.Name).Take(12)));
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Info(
                "Online sources",
                $"Online source refresh could not complete: {ex.Message}");
        }

        if (_targetMode == InstallationTargetMode.ThisComputer)
        {
            PlanStatus = $"Preparing {target.DisplayName} for this computer: scanning hardware and searching for usable installation paths…";
            await ScanHardwareAsync();

            if (module.Id == "windows" && target.Id == "windows-11")
                ApplyAutomaticWindows11CompatibilityRemediation();
        }
        else
        {
            PlanStatus = $"Preparing {target.DisplayName} for another computer without using this PC's hardware as a compatibility gate…";
            PrepareCompatibilityForOtherComputer(module, target);
        }

        if (module.Id == "macos" &&
            _targetMode == InstallationTargetMode.ThisComputer &&
            _hardwareReport is not null)
        {
            try
            {
                await TryAutomaticMacResolutionAsync(target);

                if (_autoResolution?.AutomaticConfigurationReady == true &&
                    _automationProfile is { CanBuildEfi: true, RequiresReview: false })
                {
                    await PrepareMacOSPayloadAsync(target);
                }
            }
            catch (Exception ex)
            {
                _preparationFailure = ex.Message;
                ActivityLog.Error(
                    "Preparation",
                    $"Automatic macOS preparation stopped safely: {ex.Message}",
                    ex);
            }
        }
        else if (_compatibilityReport?.CanProceed == true)
        {
            try
            {
                await PrepareGenericIsoAsync(module, target);
            }
            catch (Exception ex)
            {
                _preparationFailure = ex.Message;
                ActivityLog.Error(
                    "Preparation",
                    $"Automatic {module.DisplayName} media preparation stopped safely: {ex.Message}",
                    ex);
            }
        }

        if (_compatibilityReport is null)
        {
            PlanStatus = "Verification could not be completed. Open the Activity Log for details.";
            ActivityLog.Warning("Verification", PlanStatus);
            RefreshActionAvailability();
            return;
        }

        var relevantCriticalFailures =
            CountRelevantCriticalSourceFailures(module, target);
        var onlineReady =
            _lastOnlineSourceSnapshot is not null &&
            relevantCriticalFailures == 0;

        _preparationResult = BuildPreparationResult(module, target);
        _verificationCompleted = true;

        foreach (var item in _preparationResult.Items)
        {
            var message =
                $"{item.StateText} · {item.Category} · {item.Problem} — {item.Resolution}" +
                (string.IsNullOrWhiteSpace(item.Source)
                    ? ""
                    : $" · {item.Source}");

            if (item.State == PreparationItemState.Unresolved)
                ActivityLog.Warning("Preparation item", message);
            else
                ActivityLog.Info("Preparation item", message);
        }

        ApplyPreparationResultToUi(module, target);
        RefreshActionAvailability();

        if (!onlineReady)
        {
            var failures = relevantCriticalFailures;
            PlanStatus =
                $"NOT READY · {failures} critical online source(s) could not be refreshed live. " +
                "CorePilot will not prepare writable media from stale critical data.";
            ActivityLog.Warning("Preparation", PlanStatus);
        }
        else if (_preparationResult.ReadyToWrite)
        {
            var compatibilityWarnings =
                _targetMode == InstallationTargetMode.ThisComputer &&
                _compatibilityReport.Findings.Any(x =>
                    x.State is CompatibilityState.Warning or CompatibilityState.Unknown);

            PlanStatus = compatibilityWarnings
                ? $"READY TO WRITE ⚠ {target.DisplayName} installer media is prepared for {TargetComputerLabel}, but compatibility warnings remain. " +
                  $"{_preparationResult.Summary}. Review Preparation before writing."
                : $"READY TO WRITE ✅ {target.DisplayName} is prepared for {TargetComputerLabel}. " +
                  $"{_preparationResult.Summary}. Connect/select USB and press Write to disk.";

            if (compatibilityWarnings)
                ActivityLog.Warning("Preparation", PlanStatus);
            else
                ActivityLog.Success("Preparation", PlanStatus);
        }
        else if (_preparationResult.SystemPrepared)
        {
            PlanStatus =
                $"SYSTEM PREPARED ✅ {target.DisplayName} has a validated installation path for {TargetComputerLabel}, " +
                "but this path does not yet have a guarded physical writer. " +
                $"{_preparationResult.Summary}.";
            ActivityLog.Warning("Preparation", PlanStatus);
        }
        else
        {
            PlanStatus =
                $"NOT READY · CorePilot exhausted the currently implemented safe paths: " +
                $"{_preparationResult.Summary}. Open Preparation to see what remains unresolved.";
            ActivityLog.Warning("Preparation", PlanStatus);
        }
    }

    private int CountRelevantCriticalSourceFailures(
        ISystemModule module,
        SystemVariant target)
    {
        if (_lastOnlineSourceSnapshot is null)
            return 1;

        if (module.Id == "macos" &&
            _targetMode == InstallationTargetMode.ThisComputer &&
            _hardwareReport is not null &&
            MacOSCompatibilityAnalyzer.IsGenuineAppleMac(_hardwareReport))
        {
            var requiredIds = new HashSet<string>(
                new[]
                {
                    "macos.apple-download-install",
                    "macos.apple-version-index"
                },
                StringComparer.OrdinalIgnoreCase);

            return requiredIds.Count(id =>
            {
                var source = _lastOnlineSourceSnapshot.Sources.FirstOrDefault(x =>
                    x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

                return source is null ||
                       !source.Success ||
                       !source.Live;
            });
        }

        return _lastOnlineSourceSnapshot.CriticalFailures;
    }

    private async void WriteToDisk_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_verificationCompleted || _preparationResult?.ReadyToWrite != true)
        {
            PlanStatus = "Run Verify first and wait until CorePilot reports READY TO WRITE.";
            return;
        }

        if (SystemCombo.SelectedItem is not ISystemModule module ||
            VariantCombo.SelectedItem is not SystemVariant variant)
        {
            PlanStatus = "Choose the prepared system and version first.";
            return;
        }

        if (module.Id is "windows" or "linux")
        {
            await WritePreparedGenericMediaAsync(module, variant);
            return;
        }

        if (module.Id != "macos")
        {
            PlanStatus = "This prepared media path does not have a writer.";
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
                await _onlineSources.ResolveForTargetAsync("macos", variant.Id);

            if (_lastOnlineSourceSnapshot.CriticalFailures != 0)
                throw new InvalidOperationException(
                    $"{_lastOnlineSourceSnapshot.CriticalFailures} critical online source(s) are not live. Physical writing was blocked.");

            if (_deepScanExport is null ||
                _opCoreStage is null ||
                _lastEfiBuild is null ||
                _lastRecovery is null ||
                _lastInstallerManifest is null)
                throw new InvalidOperationException(
                    "Prepared installer payload is missing. Run Verify again; Write to disk never rebuilds the system implicitly.");

            ActivityLog.Progress(
                "Write workflow",
                "Re-validating the prepared payload before destructive USB authorization…");

            var manifestVerification = await _installerManifestService.VerifyAsync(
                _lastInstallerManifest.ManifestPath,
                _opCoreStage.WorkspaceDirectory);

            if (!manifestVerification.Success)
                throw new InvalidOperationException(
                    "Prepared installer manifest no longer verifies: " +
                    string.Join("; ", manifestVerification.Errors.Take(4)));

            var componentAudit = await _componentAuditService.AuditAsync(
                _opCoreStage,
                _lastOnlineSourceSnapshot);

            ActivityLog.Info(
                "Supply chain",
                $"{componentAudit.ComponentCount} prepared component(s) were re-audited before writing.");

            ActivityLog.Progress(
                "Supply chain",
                "Re-validating all live sources immediately before destructive USB authorization…");

            var finalSourceSnapshot =
                await _onlineSources.ResolveForTargetAsync("macos", variant.Id);

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
                    _targetMode.ToString(),
                    selectedSystem == "Windows"
                        ? _preparedWindowsMediaOptions?.ModeText
                        : null,
                    _hardwareReport,
                    _compatibilityReport,
                    _automationProfile,
                    _usbSafetyReport,
                    _opCoreStage,
                    _lastOnlineSourceSnapshot,
                    _preparedIso,
                    _lastGenericUsbWrite));

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

    private string TargetComputerLabel =>
        _targetMode == InstallationTargetMode.ThisComputer
            ? "this computer"
            : "another computer";

    private WindowsMediaOptions CurrentWindowsMediaOptions =>
        SystemCombo.SelectedItem is ISystemModule { Id: "windows" } &&
        VariantCombo.SelectedItem is SystemVariant { Id: "windows-11" } &&
        _windowsCompatibilityMedia
            ? WindowsMediaOptions.Compatibility
            : WindowsMediaOptions.Standard;

    private void ThisComputer_OnClick(object sender, RoutedEventArgs e)
    {
        if (_targetMode == InstallationTargetMode.ThisComputer)
            return;

        _targetMode = InstallationTargetMode.ThisComputer;
        _windowsCompatibilityMedia = false;
        ResetPreparationForTargetModeChange(
            "Target changed to this computer; verification is required.");
        UpdateTargetModeUi();
    }

    private void OtherComputer_OnClick(object sender, RoutedEventArgs e)
    {
        if (_targetMode == InstallationTargetMode.OtherComputer)
            return;

        _targetMode = InstallationTargetMode.OtherComputer;
        _windowsCompatibilityMedia = false;
        ResetPreparationForTargetModeChange(
            "Target changed to another computer; local hardware compatibility is no longer used.");
        UpdateTargetModeUi();
    }

    private void ResetPreparationForTargetModeChange(string reason)
    {
        _verificationCompleted = false;
        CompatibilityItems.Clear();
        PreparationItems.Clear();
        _compatibilityReport = null;
        _automationProfile = null;
        _autoResolution = null;
        _preparationResult = null;
        _preparedIso = null;
        _preparedWindowsMediaOptions = null;
        _lastGenericUsbWrite = null;
        _preparationFailure = null;
        _lastOnlineSourceSnapshot = null;
        _hardwareReport = null;
        _deepScanExport = null;
        _opCoreStage = null;
        _lastEfiBuild = null;
        _lastRecovery = null;
        _lastInstallerManifest = null;
        _usbSafetyReport = null;
        _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;
        HardwareItems.Clear();

        if (_targetMode == InstallationTargetMode.OtherComputer)
        {
            ScanStatus = "Other computer · target hardware not scanned";
            DeepScanStatus =
                "Universal Windows/Linux media preparation does not use this PC's hardware. macOS requires target hardware.";
        }
        else
        {
            ScanStatus = "Not scanned";
            DeepScanStatus =
                "Deep scan not run. It downloads the official Hardware-Sniffer-CLI release on first use.";
        }

        _workflowStateMachine.Reset(reason);
        UpdateWorkflowStatus();
        ResetCompatibilityDecision("Press Verify to prepare the selected target mode.");
        PlanStatus = reason;
        RefreshActionAvailability();
    }

    private void UpdateTargetModeUi()
    {
        var thisComputerSelected =
            _targetMode == InstallationTargetMode.ThisComputer;

        SetSegmentButtonState(ThisComputerButton, thisComputerSelected);
        SetSegmentButtonState(OtherComputerButton, !thisComputerSelected);

        if (thisComputerSelected)
        {
            TargetModeStatus =
                "This computer · Verify scans this PC and may automatically resolve supported compatibility issues for the selected system.";
            return;
        }

        TargetModeStatus =
            SystemCombo.SelectedItem is ISystemModule { Id: "macos" }
                ? "Other computer · macOS needs the target computer's hardware before CorePilot can safely build a hardware-specific installer."
                : "Other computer · Windows/Linux media is prepared without using this PC's TPM, CPU, Secure Boot or firmware state as a compatibility gate.";
    }

    private void SetSegmentButtonState(Button button, bool selected)
    {
        button.SetResourceReference(
            BackgroundProperty,
            selected ? "AccentBrush" : "PanelBrush");
        button.SetResourceReference(
            BorderBrushProperty,
            selected ? "AccentBrush" : "BorderBrush");
        button.SetResourceReference(
            ForegroundProperty,
            selected ? "TextBrush" : "MutedBrush");
    }

    private void PrepareCompatibilityForOtherComputer(
        ISystemModule module,
        SystemVariant target)
    {
        CompatibilityItems.Clear();
        PreparationItems.Clear();
        _hardwareReport = null;
        _deepScanExport = null;
        _automationProfile = null;
        _autoResolution = null;

        _compatibilityReport =
            InstallationTargetCompatibilityBuilder.ForOtherComputer(
                module.Id,
                module.DisplayName,
                target);

        foreach (var finding in _compatibilityReport.Findings)
            CompatibilityItems.Add(finding);

        CompatibilitySummary = _compatibilityReport.Summary;
        ScanStatus = "Other computer · target hardware not scanned";
        DeepScanStatus =
            module.Id == "macos"
                ? "macOS automatic preparation needs target hardware and is blocked in Other computer mode."
                : "Target hardware scan is intentionally skipped for universal Windows/Linux media.";
        UpdateCompatibilityDecisionWithoutHardware(module, target);
    }

    private void ApplyAutomaticWindows11CompatibilityRemediation()
    {
        if (_compatibilityReport is null)
            return;

        var blockers = _compatibilityReport.Findings
            .Where(x => x.State == CompatibilityState.Blocked)
            .ToArray();

        if (blockers.Length == 0)
        {
            _windowsCompatibilityMedia = false;
            UpdateTargetModeUi();
            return;
        }

        var bypassable = blockers.All(x =>
            x.Component == "TPM");

        if (!bypassable)
        {
            _windowsCompatibilityMedia = false;
            UpdateTargetModeUi();
            return;
        }

        _windowsCompatibilityMedia = true;

        var remediated = _compatibilityReport.Findings
            .Select(finding =>
            {
                if (finding.State == CompatibilityState.Blocked &&
                    finding.Component == "TPM")
                {
                    return finding with
                    {
                        State = CompatibilityState.Supported,
                        Title = finding.Title + " · resolved by Windows 11 compatibility media",
                        Details = finding.Details +
                                  " CorePilot will apply the documented Windows Setup compatibility path while keeping the normal UEFI media layout."
                    };
                }

                if (finding.Component == "Secure Boot" &&
                    finding.State != CompatibilityState.Supported)
                {
                    return finding with
                    {
                        State = CompatibilityState.Supported,
                        Title = "Secure Boot requirement handled by Windows 11 compatibility media",
                        Details = finding.Details +
                                  " CorePilot will apply the documented Secure Boot installation bypass."
                    };
                }

                return finding;
            })
            .ToList();

        remediated.Add(new(
            CompatibilityState.Supported,
            "Installer media",
            "Windows 11 compatibility media enabled automatically",
            "CorePilot will keep the normal UEFI/FAT32 media layout and apply the implemented TPM/Secure Boot Windows Setup compatibility settings. Legacy BIOS is not treated as resolved. CPU-specific requirements are not falsely claimed as bypassed."));

        remediated.Add(new(
            CompatibilityState.Warning,
            "Microsoft support",
            "This Windows 11 path does not meet the standard minimum-requirements policy",
            "Microsoft does not recommend Windows 11 on ineligible hardware and does not guarantee support or updates for devices that do not meet the minimum requirements.",
            "Use this compatibility path only if you accept the unsupported-hardware risk.",
            "https://support.microsoft.com/windows/experience/compatibility/windows-11-on-devices-that-don-t-meet-minimum-system-requirements"));

        _compatibilityReport = _compatibilityReport with
        {
            Findings = remediated
        };

        CompatibilityItems.Clear();
        foreach (var finding in _compatibilityReport.Findings)
            CompatibilityItems.Add(finding);

        CompatibilitySummary = _compatibilityReport.Summary;
        UpdateCompatibilityDecision(
            (ISystemModule)SystemCombo.SelectedItem,
            (SystemVariant)VariantCombo.SelectedItem);
        UpdateTargetModeUi();
    }

    private void UpdateCompatibilityDecisionWithoutHardware(
        ISystemModule system,
        SystemVariant target)
    {
        if (_compatibilityReport is null)
            return;

        CompatibilityVerdict = _compatibilityReport.CanProceed
            ? "MEDIA PREPARATION AVAILABLE"
            : "TARGET HARDWARE REQUIRED";

        CompatibilityInstallPath = system.Id == "macos"
            ? "Installation path: macOS needs the actual target hardware before CorePilot can build a safe hardware-specific configuration."
            : $"Installation path: prepare the official {system.DisplayName} image for another computer without using this PC's hardware as a blocker.";

        CompatibilityRequirements = _compatibilityReport.CanProceed
            ? "Target hardware compatibility is not asserted in Other computer mode. CorePilot validates the installer media and writer path instead."
            : string.Join(
                Environment.NewLine,
                _compatibilityReport.Findings
                    .Where(x => x.State == CompatibilityState.Blocked)
                    .Select(x => "• " + (x.SuggestedAction ?? x.Details)));
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

        _windowsCompatibilityMedia = false;
        UpdateTargetModeUi();

        _verificationCompleted = false;
        CompatibilityItems.Clear();
        PreparationItems.Clear();
        _compatibilityReport = null;
        _automationProfile = null;
        _autoResolution = null;
        _preparationResult = null;
        _preparedIso = null;
        _preparedWindowsMediaOptions = null;
        _lastGenericUsbWrite = null;
        _preparationFailure = null;
        _lastOnlineSourceSnapshot = null;
        _opCoreStage = null;
        _lastEfiBuild = null;
        _lastRecovery = null;
        _lastInstallerManifest = null;
        _usbSafetyReport = null;
        _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;
        CompatibilitySummary = _targetMode == InstallationTargetMode.ThisComputer
            ? "Press Verify to prepare this system for the detected hardware."
            : "Press Verify to prepare universal Windows/Linux media for another computer; macOS requires target hardware.";
        ResetCompatibilityDecision("Press Verify to search for and prepare an installation path.");
        PlanStatus = _targetMode == InstallationTargetMode.ThisComputer
            ? "Press Verify. CorePilot will scan this PC, search, configure and validate before USB writing."
            : "Press Verify. CorePilot will prepare the selected system without using this PC's hardware as the target.";
        InvalidateWorkflowAfter(
            HardwareBaselinePhase,
            "System selection changed; verification required.");
        RefreshActionAvailability();
    }

    private void VariantCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _windowsCompatibilityMedia = false;
        UpdateTargetModeUi();
        _verificationCompleted = false;
        CompatibilityItems.Clear();
        PreparationItems.Clear();
        _compatibilityReport = null;
        _automationProfile = null;
        _autoResolution = null;
        _preparationResult = null;
        _preparedIso = null;
        _preparedWindowsMediaOptions = null;
        _lastGenericUsbWrite = null;
        _preparationFailure = null;
        _lastOnlineSourceSnapshot = null;
        _opCoreStage = null;
        _lastEfiBuild = null;
        _lastRecovery = null;
        _lastInstallerManifest = null;
        _usbSafetyReport = null;
        _lastUsbWritePlan = null;
        _lastUsbExecutionPreflight = null;
        _lastUsbTypedConfirmation = null;
        CompatibilitySummary = _targetMode == InstallationTargetMode.ThisComputer
            ? "Press Verify to prepare this version for the detected hardware."
            : "Press Verify to prepare this version for another computer without using this PC as the compatibility target.";
        ResetCompatibilityDecision("Press Verify to search for and prepare an installation path.");
        PlanStatus = _targetMode == InstallationTargetMode.ThisComputer
            ? "Press Verify. CorePilot will scan this PC, search, configure and validate before USB writing."
            : "Press Verify. CorePilot will prepare the selected version for another computer.";
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
        _autoResolution = null;
        _preparationResult = null;
        _preparedIso = null;
        _preparedWindowsMediaOptions = null;
        _lastGenericUsbWrite = null;
        _preparationFailure = null;
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
        PreparationItems.Clear();
        CompatibilityVerdict = "NOT PREPARED";
        CompatibilityInstallPath = message;
        CompatibilityRequirements =
            "Required fixes, patches, drivers and boot arguments will appear here after Verify.";
        CompatibilityAutoConfiguration =
            "Automatic configuration has not run yet.";
    }

    private async Task TryAutomaticMacResolutionAsync(SystemVariant target)
    {
        if (_hardwareReport is null || _compatibilityReport is null)
            return;

        var genuineApple =
            MacOSCompatibilityAnalyzer.IsGenuineAppleMac(_hardwareReport);

        if (!genuineApple && _deepScanExport is null)
        {
            try
            {
                DeepScanStatus =
                    "Verify is running the automatic Hardware Sniffer deep scan…";
                ActivityLog.Progress(
                    "Auto configuration",
                    "Resolving exact ACPI/PCI hardware before choosing fixes, kexts and EFI settings…");

                var progress = new Progress<string>(message =>
                {
                    DeepScanStatus = message;
                    ActivityLog.Progress("Deep Scan", message);
                });

                _deepScanExport = await _hardwareSniffer.ExportAsync(progress);
                _hardwareReport = await _hardwareSnifferParser.MergeAsync(
                    _deepScanExport.ReportPath,
                    _hardwareReport);

                RefreshHardwareView();
                _workflowStateMachine.Reset(
                    "Automatic Verify deep scan refreshed hardware identity.");
                AdvanceWorkflow(
                    MacOSWorkflowPhase.DeepScanned,
                    "Hardware Sniffer Report.json + ACPI imported automatically during Verify.");

                RunCompatibilityAnalysis();
            }
            catch (Exception ex)
            {
                DeepScanStatus =
                    $"Automatic Deep Scan could not complete: {ex.Message}";
                ActivityLog.Info(
                    "Auto configuration",
                    "Deep Scan could not be completed automatically; unresolved hardware-dependent settings will remain blocked.");
            }
        }

        if (_hardwareReport is null || _compatibilityReport is null)
            return;

        ActivityLog.Progress(
            "Auto configuration",
            "Finding current hardware-specific fixes, drivers, patches and settings…");

        _autoResolution = await _macAutoResolver.ResolveAsync(
            _hardwareReport,
            target,
            _compatibilityReport,
            _automationProfile,
            _lastOnlineSourceSnapshot,
            _deepScanExport is not null);

        if (_automationProfile is not null)
        {
            var profilePath =
                await _automationProfileStore.SaveAsync(_automationProfile);

            ActivityLog.Info(
                "Auto configuration",
                $"Hardware-specific macOS automation profile saved: {profilePath}");
        }

        CompatibilityAutoConfiguration =
            FormatAutoResolution(_autoResolution);

        if (_compatibilityReport.CanProceed)
        {
            if (_autoResolution.AutomaticConfigurationReady)
                CompatibilityVerdict = "READY — AUTO-CONFIGURED";
            else if (_autoResolution.UnresolvedCount > 0)
                CompatibilityVerdict = "REVIEW REQUIRED";
            else if (_autoResolution.ManualCount > 0)
                CompatibilityVerdict = "MANUAL STEP REQUIRED";
        }

        ActivityLog.Progress(
            "Auto configuration",
            _autoResolution.Summary);
    }

    private static string FormatAutoResolution(
        MacOSAutoResolutionResult result)
    {
        if (result.Items.Count == 0)
            return result.Summary + Environment.NewLine +
                   "No hardware-specific software changes were required.";

        var lines = result.Items.Select(x =>
        {
            var version = string.IsNullOrWhiteSpace(x.SourceVersion)
                ? ""
                : $" · {x.SourceVersion}";

            return $"{x.StateText} · {x.Category} · {x.Requirement}{version} — {x.Resolution}";
        });

        return result.Summary +
               Environment.NewLine +
               string.Join(Environment.NewLine, lines);
    }

    private async Task PrepareMacOSPayloadAsync(SystemVariant target)
    {
        if (_deepScanExport is null ||
            _automationProfile is not { CanBuildEfi: true, RequiresReview: false })
            throw new InvalidOperationException(
                "Exact hardware data and a fully automatic macOS profile are required before preparing the installer.");

        ActivityLog.Progress(
            "Preparation",
            "Staging the current verified OpenCore automation engine…");

        _opCoreStage = await _opCoreStager.StageAsync(
            _deepScanExport.ReportPath,
            _deepScanExport.AcpiDirectory,
            _automationProfile,
            new Progress<string>(message =>
                ActivityLog.Progress("Workspace", message)));

        AdvanceWorkflow(
            MacOSWorkflowPhase.WorkspaceStaged,
            $"OpenCore workspace staged for {target.DisplayName}.");

        ActivityLog.Progress(
            "Preparation",
            "Building and validating the hardware-specific EFI…");

        _lastEfiBuild = await _opCoreBuilder.BuildAsync(
            _opCoreStage,
            _automationProfile,
            new Progress<string>(message =>
                ActivityLog.Progress("EFI Build", message)));

        if (_lastOnlineSourceSnapshot is null)
            throw new InvalidOperationException(
                "Live source snapshot is required before auditing the prepared EFI.");

        var componentAudit = await _componentAuditService.AuditAsync(
            _opCoreStage,
            _lastOnlineSourceSnapshot);

        ActivityLog.Info(
            "Supply chain",
            $"{componentAudit.ComponentCount} downloaded OpenCore/kext component(s) passed source, SHA-256 and manifest checks.");

        AdvanceWorkflow(
            MacOSWorkflowPhase.EfiValidated,
            "EFI passed structural validation and downloaded-component integrity audit.");

        ActivityLog.Progress(
            "Preparation",
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
                "Prepared installer manifest verification failed: " +
                string.Join("; ", manifestVerification.Errors.Take(4)));

        AdvanceWorkflow(
            MacOSWorkflowPhase.ManifestVerified,
            "Prepared installer payload and manifest verified.");

        ActivityLog.Progress(
            "Preparation",
            "macOS payload is prepared. USB writing can now remain a separate destructive step.");
    }

    private async Task PrepareGenericIsoAsync(
        ISystemModule module,
        SystemVariant target)
    {
        if (_compatibilityReport?.CanProceed != true)
            return;

        ActivityLog.Progress(
            "Preparation",
            $"Resolving and preparing the current official {target.DisplayName} installer image…");

        var progress = new Progress<string>(message =>
        {
            PlanStatus = message;
            ActivityLog.Progress("Installer image", message);
        });

        _preparedIso = module.Id switch
        {
            "windows" => await _windowsIsoPreparation.PrepareAsync(
                target,
                progress),
            "linux" => await _linuxIsoPreparation.PrepareAsync(
                target,
                progress),
            _ => null
        };

        if (_preparedIso is null)
            throw new InvalidOperationException(
                $"No ISO preparation service exists for {module.DisplayName}.");

        _preparedWindowsMediaOptions =
            module.Id == "windows"
                ? CurrentWindowsMediaOptions
                : null;

        ActivityLog.Info(
            "Installer image",
            $"Prepared {_preparedIso.FileName} · {_preparedIso.SizeBytes / 1024d / 1024d / 1024d:0.00} GB · SHA-256 {_preparedIso.Sha256[..16]}… · {_preparedIso.Provenance}");
    }

    private async Task WritePreparedGenericMediaAsync(
        ISystemModule module,
        SystemVariant target)
    {
        if (_preparedIso is null ||
            !_preparedIso.SystemId.Equals(
                module.Id,
                StringComparison.OrdinalIgnoreCase) ||
            !_preparedIso.TargetId.Equals(
                target.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            PlanStatus =
                "Prepared installer image is missing or no longer matches the selection. Run Verify again.";
            return;
        }

        if (module.Id == "windows" &&
            _preparedWindowsMediaOptions is null)
        {
            PlanStatus =
                "Prepared Windows media mode is missing. Run Verify again so Write to disk consumes the exact verified configuration.";
            return;
        }

        var preparedWindowsOptions =
            _preparedWindowsMediaOptions ?? WindowsMediaOptions.Standard;

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
                $"Re-validating {target.DisplayName} preparation and USB target…");

            var live = await _onlineSources.ResolveForTargetAsync(module.Id, target.Id);
            if (live.CriticalFailures != 0)
                throw new InvalidOperationException(
                    $"{live.CriticalFailures} critical online source(s) are not live. Writing was blocked.");

            var inspected = await _usbSafetyInspector.InspectAsync(usb);
            UsbSafetyStatus = inspected.Summary;

            if (inspected.IsBlocked)
                throw new InvalidOperationException(
                    $"USB target is blocked: {inspected.Summary}");

            var phrase = module.Id == "windows"
                ? WindowsInstallerUsbWriter.RequiredConfirmationPhrase(
                    inspected,
                    _preparedIso,
                    preparedWindowsOptions)
                : LinuxRawUsbWriter.RequiredConfirmationPhrase(
                    inspected,
                    _preparedIso);

            var typed = Microsoft.VisualBasic.Interaction.InputBox(
                "CorePilot is ready to ERASE the selected USB disk.\n\n" +
                $"Disk: {inspected.DiskIndex} · {inspected.Model}\n" +
                $"Size: {inspected.SizeBytes / 1024d / 1024d / 1024d:0.#} GB\n\n" +
                $"Prepared image: {_preparedIso.FileName}\n" +
                $"SHA-256: {_preparedIso.Sha256[..16]}…\n\n" +
                "Type this exact phrase to continue:\n\n" +
                phrase,
                $"CorePilot — confirm {module.DisplayName} USB erase",
                "");

            if (!typed.Equals(phrase, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Exact destructive confirmation phrase was not entered. Nothing was written.");

            var finalTarget = await _usbSafetyInspector.InspectAsync(usb);

            if (finalTarget.IsBlocked ||
                !finalTarget.IdentityFingerprint.Equals(
                    inspected.IdentityFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "USB identity or safety state changed after confirmation.");

            var progress = new Progress<string>(message =>
            {
                PlanStatus = message;
                ActivityLog.Progress("Physical write", message);
            });

            _lastGenericUsbWrite = module.Id == "windows"
                ? await _windowsUsbWriter.WriteAsync(
                    _preparedIso,
                    finalTarget,
                    typed,
                    progress,
                    options: preparedWindowsOptions)
                : await _linuxUsbWriter.WriteAsync(
                    _preparedIso,
                    finalTarget,
                    typed,
                    progress);

            if (!_lastGenericUsbWrite.Verified)
                throw new InvalidOperationException(
                    "Physical writer completed without a verified result.");

            PlanStatus =
                $"USB READY ✅ {target.DisplayName} · Disk {_lastGenericUsbWrite.TargetDiskIndex} · " +
                $"verified write transcript {_lastGenericUsbWrite.TranscriptSha256[..16]}….";
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

    private InstallationPreparationResult BuildPreparationResult(
        ISystemModule system,
        SystemVariant target)
    {
        if (_compatibilityReport is null)
            throw new InvalidOperationException(
                "Compatibility analysis must run before installation preparation.");

        if (system.Id != "macos")
        {
            var imagePrepared =
                _preparedIso is not null &&
                _preparedIso.SystemId.Equals(
                    system.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                _preparedIso.TargetId.Equals(
                    target.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(_preparedIso.IsoPath);

            var generic = InstallationPreparationBuilder.FromGenericCompatibility(
                system.Id,
                target,
                _compatibilityReport,
                _lastOnlineSourceSnapshot,
                mediaWriterAvailable: imagePrepared);

            var genericItems = generic.Items.ToList();

            if (imagePrepared)
            {
                genericItems.Add(new(
                    PreparationItemState.ResolvedAutomatically,
                    "Installer image",
                    _preparedIso!.FileName,
                    _preparedIso.ExpectedSha256 is null
                        ? $"Official image downloaded and locally SHA-256 locked: {_preparedIso.Sha256}."
                        : $"Official image downloaded and verified against publisher SHA-256: {_preparedIso.Sha256}.",
                    _preparedIso.SourceUrl));

                if (system.Id == "windows" &&
                    _preparedWindowsMediaOptions is not null)
                {
                    genericItems.Add(new(
                        PreparationItemState.ResolvedAutomatically,
                        "Windows media mode",
                        _preparedWindowsMediaOptions.ModeText,
                        _preparedWindowsMediaOptions.ExtendedHardwareCompatibility
                            ? "Verify locked the UEFI/FAT32 Windows 11 compatibility path with the implemented Windows Setup TPM/Secure Boot remediation."
                            : "Verify locked the standard Windows media path for Write to disk."));
                }
            }

            if (!string.IsNullOrWhiteSpace(_preparationFailure))
            {
                genericItems.Add(new(
                    PreparationItemState.Unresolved,
                    "Preparation",
                    "Automatic preparation failed",
                    _preparationFailure));
            }

            var unresolved = genericItems.Any(x =>
                x.State == PreparationItemState.Unresolved);
            var manual = genericItems.Any(x =>
                x.State == PreparationItemState.ManualActionRequired);

            return generic with
            {
                Items = genericItems,
                ConfigurationPrepared =
                    generic.ConfigurationPrepared &&
                    imagePrepared &&
                    !unresolved &&
                    !manual,
                MediaWriterAvailable = imagePrepared
            };
        }

        var items = new List<PreparationItem>();

        foreach (var finding in _compatibilityReport.Findings.Where(x =>
                     x.State is CompatibilityState.Blocked or CompatibilityState.Unknown))
        {
            items.Add(new(
                PreparationItemState.Unresolved,
                finding.Component,
                finding.Title,
                finding.SuggestedAction ?? finding.Details,
                finding.Reference));
        }

        if (_autoResolution is not null)
        {
            foreach (var item in _autoResolution.Items)
            {
                items.Add(new(
                    item.State switch
                    {
                        MacOSAutoResolutionState.Prepared =>
                            PreparationItemState.ResolvedAutomatically,
                        MacOSAutoResolutionState.SourceReady =>
                            PreparationItemState.SourceResolved,
                        MacOSAutoResolutionState.ManualAction =>
                            PreparationItemState.ManualActionRequired,
                        _ => PreparationItemState.Unresolved
                    },
                    item.Category,
                    item.Requirement,
                    item.Resolution,
                    item.SourceId));
            }
        }

        if (!string.IsNullOrWhiteSpace(_preparationFailure))
        {
            items.Add(new(
                PreparationItemState.Unresolved,
                "Preparation",
                "Automatic preparation failed",
                _preparationFailure));
        }

        items = items
            .GroupBy(
                x => $"{x.State}|{x.Category}|{x.Problem}|{x.Resolution}",
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        var genuineApple =
            _hardwareReport is not null &&
            MacOSCompatibilityAnalyzer.IsGenuineAppleMac(_hardwareReport);

        var payloadPrepared =
            _opCoreStage is not null &&
            _lastEfiBuild is not null &&
            _lastRecovery is not null &&
            _lastInstallerManifest is not null;

        if (payloadPrepared)
        {
            items.Add(new(
                PreparationItemState.ResolvedAutomatically,
                "Installer payload",
                "Boot environment and recovery payload",
                "Hardware-specific EFI, Apple Recovery and the final integrity manifest were prepared and verified."));
        }

        var autoReady = _autoResolution?.AutomaticConfigurationReady == true;
        var configurationPrepared = genuineApple
            ? autoReady
            : autoReady && payloadPrepared;

        var writerAvailable =
            !genuineApple &&
            payloadPrepared &&
            _automationProfile is { CanBuildEfi: true, RequiresReview: false };

        return new(
            system.Id,
            target.Id,
            items,
            CompatibilityEvaluated: true,
            SearchCompleted: _lastOnlineSourceSnapshot is not null,
            ConfigurationPrepared: configurationPrepared,
            MediaWriterAvailable: writerAvailable);
    }

    private void ApplyPreparationResultToUi(
        ISystemModule system,
        SystemVariant target)
    {
        if (_preparationResult is null)
            return;

        PreparationItems.Clear();
        foreach (var item in _preparationResult.Items)
            PreparationItems.Add(item);

        var compatibilityWarnings =
            _targetMode == InstallationTargetMode.ThisComputer &&
            _compatibilityReport?.Findings.Any(x =>
                x.State is CompatibilityState.Warning or CompatibilityState.Unknown) == true;

        CompatibilityVerdict =
            _preparationResult.ReadyToWrite && compatibilityWarnings
                ? "READY TO WRITE · REVIEW COMPATIBILITY WARNINGS"
                : _preparationResult.Verdict;

        CompatibilityInstallPath = _preparationResult.ReadyToWrite
            ? compatibilityWarnings
                ? $"CorePilot prepared and validated writable {target.DisplayName} installer media for {TargetComputerLabel}, but the remaining compatibility warnings are not claimed as solved."
                : $"CorePilot found, configured and validated a writable {target.DisplayName} installation path for {TargetComputerLabel}."
            : _preparationResult.SystemPrepared
                ? $"CorePilot found and validated an installation path for {target.DisplayName}, but the physical writer for this path is not implemented yet."
                : $"CorePilot searched the currently implemented safe paths for {target.DisplayName}; unresolved or manual items still prevent a validated writable result.";

        if (system.Id != "macos")
        {
            CompatibilityAutoConfiguration = FormatPreparationResult(
                _preparationResult);
        }
        else if (_autoResolution is not null)
        {
            CompatibilityAutoConfiguration =
                FormatAutoResolution(_autoResolution) +
                Environment.NewLine +
                (_preparationResult.ReadyToWrite
                    ? "PREPARED · Boot payload and integrity manifest are ready for USB writing."
                    : "");
        }
    }

    private static string FormatPreparationResult(
        InstallationPreparationResult result)
    {
        if (result.Items.Count == 0)
            return result.Summary;

        return result.Summary +
               Environment.NewLine +
               string.Join(
                   Environment.NewLine,
                   result.Items.Select(x =>
                       $"{x.StateText} · {x.Category} · {x.Problem} — {x.Resolution}"));
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
                $"Installation path: Verify will prepare the selected official {system.DisplayName} installer image for {TargetComputerLabel} before Write to disk is enabled.";
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
