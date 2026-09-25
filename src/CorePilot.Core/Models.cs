namespace CorePilot.Core;

public sealed record HardwareDisplayItem(string Category, string Name, string Details);

public sealed record HardwareDeviceInfo(
    string Category,
    string Name,
    string PnpDeviceId = "",
    string DeviceId = "",
    string SubsystemId = "",
    string BusType = "",
    string PciPath = "",
    string AcpiPath = "")
{
    public string Details
    {
        get
        {
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(DeviceId))
                details.Add($"ID {DeviceId}");

            if (!string.IsNullOrWhiteSpace(SubsystemId))
                details.Add($"SUBSYS {SubsystemId}");

            if (!string.IsNullOrWhiteSpace(BusType))
                details.Add(BusType);

            if (!string.IsNullOrWhiteSpace(PciPath))
                details.Add(PciPath);
            else if (!string.IsNullOrWhiteSpace(AcpiPath))
                details.Add(AcpiPath);
            else if (!string.IsNullOrWhiteSpace(PnpDeviceId))
                details.Add(PnpDeviceId);

            return string.Join(" · ", details);
        }
    }
}

public sealed record DiskVolumeInfo(
    string DriveLetter,
    string Label,
    string FileSystem,
    long SizeBytes,
    long FreeBytes,
    bool IsSystemVolume)
{
    public string SizeText => SizeBytes <= 0
        ? "Unknown size"
        : $"{SizeBytes / 1024d / 1024d / 1024d:0.#} GB";

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Label)
                ? DriveLetter
                : $"{DriveLetter} ({Label})";

            var fileSystem = string.IsNullOrWhiteSpace(FileSystem)
                ? ""
                : $" · {FileSystem}";

            var system = IsSystemVolume ? " · SYSTEM" : "";
            return $"{name} · {SizeText}{fileSystem}{system}";
        }
    }
}

public enum UsbTargetSafetyState
{
    SafeCandidate,
    Warning,
    Blocked
}

public sealed record UsbDriveInfo(
    string DeviceId,
    int DiskNumber,
    string Model,
    string SerialNumber,
    string InterfaceType,
    string MediaType,
    long SizeBytes,
    bool IsUsb,
    bool IsRemovable,
    bool IsSystemDisk,
    IReadOnlyList<DiskVolumeInfo> Volumes)
{
    public string SizeText => SizeBytes <= 0
        ? "Unknown size"
        : $"{SizeBytes / 1024d / 1024d / 1024d:0.#} GB";

    public UsbTargetSafetyState SafetyState
    {
        get
        {
            if (IsSystemDisk)
                return UsbTargetSafetyState.Blocked;

            if (!IsUsb || SizeBytes <= 0 || string.IsNullOrWhiteSpace(DeviceId))
                return UsbTargetSafetyState.Warning;

            return UsbTargetSafetyState.SafeCandidate;
        }
    }

    public string SafetyText => SafetyState switch
    {
        UsbTargetSafetyState.SafeCandidate => "SAFE CANDIDATE",
        UsbTargetSafetyState.Warning => "WARNING",
        _ => "BLOCKED"
    };

    public string DisplayName =>
        $"Disk {DiskNumber} · {Model} — {SizeText}{(IsSystemDisk ? " [SYSTEM]" : "")}";

    public string SafetyDetails
    {
        get
        {
            var details = new List<string>
            {
                $"Physical disk: {DeviceId}",
                $"Bus: {(string.IsNullOrWhiteSpace(InterfaceType) ? "Unknown" : InterfaceType)}",
                $"Media: {(string.IsNullOrWhiteSpace(MediaType) ? "Unknown" : MediaType)}",
                $"Removable flag: {(IsRemovable ? "Yes" : "No")}",
                $"System disk: {(IsSystemDisk ? "YES" : "No")}",
                $"Serial: {(string.IsNullOrWhiteSpace(SerialNumber) ? "Unavailable" : SerialNumber)}"
            };

            if (Volumes.Count == 0)
                details.Add("Volumes: none mounted");
            else
                details.Add("Volumes: " + string.Join("; ", Volumes.Select(x => x.DisplayName)));

            if (SafetyState == UsbTargetSafetyState.Blocked)
                details.Add("CorePilot will never allow destructive writes to this target.");
            else if (SafetyState == UsbTargetSafetyState.Warning)
                details.Add("Target identity is incomplete or not clearly USB; destructive writes must remain blocked.");
            else
                details.Add("Read-only inspection passed. This does NOT enable USB writing.");

            return string.Join(Environment.NewLine, details);
        }
    }
}

public sealed record HardwareReport(
    string ComputerName,
    string Manufacturer,
    string Model,
    string FormFactor,
    string Cpu,
    int CpuCores,
    int CpuThreads,
    string Motherboard,
    string FirmwareMode,
    bool? SecureBoot,
    long MemoryBytes,
    IReadOnlyList<HardwareDeviceInfo> Devices,
    IReadOnlyList<UsbDriveInfo> Disks)
{
    public IEnumerable<HardwareDisplayItem> ToDisplayItems()
    {
        yield return new("System", $"{Manufacturer} {Model}".Trim(), $"{ComputerName} · {FormFactor}");
        yield return new("CPU", Cpu, CpuCores > 0 ? $"{CpuCores} cores / {CpuThreads} threads" : "");
        yield return new("Motherboard", Motherboard, "");
        yield return new("Firmware", FirmwareMode, $"Secure Boot: {FormatBool(SecureBoot)}");
        yield return new("Memory", $"{MemoryBytes / 1024d / 1024d / 1024d:0.#} GB", "");

        foreach (var device in Devices)
            yield return new(device.Category, device.Name, device.Details);
    }

    public IReadOnlyList<HardwareDeviceInfo> DevicesByCategory(string category) =>
        Devices.Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static string FormatBool(bool? value) => value switch
    {
        true => "Enabled",
        false => "Disabled",
        null => "Unknown"
    };
}

public enum CompatibilityState
{
    Supported,
    ActionRequired,
    Warning,
    Blocked,
    Unknown
}

public sealed record CompatibilityFinding(
    CompatibilityState State,
    string Component,
    string Title,
    string Details,
    string? SuggestedAction = null,
    string? Reference = null)
{
    public string StateText => State switch
    {
        CompatibilityState.Supported => "OK",
        CompatibilityState.ActionRequired => "ACTION",
        CompatibilityState.Warning => "WARNING",
        CompatibilityState.Blocked => "BLOCKED",
        _ => "UNKNOWN"
    };
}

public sealed record CompatibilityReport(
    string TargetId,
    IReadOnlyList<CompatibilityFinding> Findings,
    IReadOnlyList<string> RequiredKexts,
    IReadOnlyList<string> RequiredPatches,
    IReadOnlyList<string> BootArguments)
{
    public bool CanProceed => Findings.All(x => x.State != CompatibilityState.Blocked);
    public int BlockerCount => Findings.Count(x => x.State == CompatibilityState.Blocked);
    public string Summary => CanProceed
        ? $"No blocking issue detected · {Findings.Count} checks"
        : $"{BlockerCount} blocking issue{(BlockerCount == 1 ? "" : "s")} detected";
}

public sealed record SystemVariant(string Id, string DisplayName);

public interface ISystemModule
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<SystemVariant> Variants { get; }
}
