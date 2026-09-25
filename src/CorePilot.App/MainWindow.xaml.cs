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
    private readonly MacOSCompatibilityAnalyzer _macAnalyzer = new();
    private HardwareReport? _hardwareReport;
    private CompatibilityReport? _compatibilityReport;

    private string _scanStatus = "Not scanned";
    private string _deepScanStatus = "Deep scan not run. It downloads the official Hardware-Sniffer-CLI release on first use.";
    private string _planStatus = "Select a system and USB drive, then prepare an installation plan.";
    private string _compatibilitySummary = "Scan hardware and select macOS to run compatibility checks.";
    private string _macPlanDetails = "";

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

    private async void DeepScan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DeepScanStatus = "Starting Hardware Sniffer…";
            var progress = new Progress<string>(message => DeepScanStatus = message);
            var result = await _hardwareSniffer.ExportAsync(progress);

            DeepScanStatus =
                $"Hardware Sniffer {result.Version} complete. " +
                $"Report: {result.ReportPath} · SHA256: {result.ToolSha256[..16]}…";
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

            HardwareItems.Clear();
            foreach (var item in _hardwareReport.ToDisplayItems())
                HardwareItems.Add(item);

            ScanStatus = $"Detected {HardwareItems.Count} hardware items";
            RunCompatibilityAnalysis();
        }
        catch (Exception ex)
        {
            ScanStatus = "Scan failed";
            PlanStatus = $"Hardware scan failed: {ex.Message}";
        }
    }

    private async void RefreshDrives_OnClick(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    private async Task RefreshDrivesAsync()
    {
        try
        {
            var current = UsbCombo.SelectedItem as UsbDriveInfo;
            var disks = await _scanner.ScanDisksAsync();

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
        _compatibilityReport = null;
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
        foreach (var finding in _compatibilityReport.Findings)
            CompatibilityItems.Add(finding);

        CompatibilitySummary = _compatibilityReport.Summary;
        MacPlanDetails = BuildPlanText(_compatibilityReport);
        OnPropertyChanged(nameof(MacPlanVisibility));
    }

    private static string BuildPlanText(CompatibilityReport report)
    {
        var parts = new List<string>();

        if (report.RequiredKexts.Count > 0)
            parts.Add("Kexts: " + string.Join(", ", report.RequiredKexts));

        if (report.RequiredPatches.Count > 0)
            parts.Add("Patches: " + string.Join(", ", report.RequiredPatches));

        if (report.BootArguments.Count > 0)
            parts.Add("Boot args: " + string.Join(" ", report.BootArguments));

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private void PreparePlan_OnClick(object sender, RoutedEventArgs e)
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

        PlanStatus = $"Plan ready: {variant.DisplayName} → {usb.DisplayName}. " +
                     $"Next milestone: OpenCore/EFI generation, downloads and guarded USB writing for {system.DisplayName}.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
