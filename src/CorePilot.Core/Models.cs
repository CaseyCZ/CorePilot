namespace CorePilot.Core;

public sealed record HardwareDisplayItem(string Category, string Name, string Details);

public sealed record UsbDriveInfo(string DeviceId, string Model, long SizeBytes, bool IsUsb)
{
    public string SizeText => SizeBytes <= 0 ? "Unknown size" : $"{SizeBytes / 1024d / 1024d / 1024d:0.#} GB";
    public string DisplayName => $"{Model} — {SizeText}";
}

public sealed record HardwareReport(
    string ComputerName,
    string Manufacturer,
    string Model,
    string Cpu,
    string Motherboard,
    string FirmwareMode,
    bool? SecureBoot,
    long MemoryBytes,
    IReadOnlyList<string> Gpus,
    IReadOnlyList<string> NetworkAdapters,
    IReadOnlyList<string> AudioDevices,
    IReadOnlyList<UsbDriveInfo> Disks)
{
    public IEnumerable<HardwareDisplayItem> ToDisplayItems()
    {
        yield return new("System", $"{Manufacturer} {Model}".Trim(), ComputerName);
        yield return new("CPU", Cpu, "");
        yield return new("Motherboard", Motherboard, "");
        yield return new("Firmware", FirmwareMode, $"Secure Boot: {FormatBool(SecureBoot)}");
        yield return new("Memory", $"{MemoryBytes / 1024d / 1024d / 1024d:0.#} GB", "");
        foreach (var gpu in Gpus) yield return new("GPU", gpu, "");
        foreach (var adapter in NetworkAdapters) yield return new("Network", adapter, "");
        foreach (var audio in AudioDevices) yield return new("Audio", audio, "");
    }

    private static string FormatBool(bool? value) => value switch
    {
        true => "Enabled",
        false => "Disabled",
        null => "Unknown"
    };
}

public sealed record SystemVariant(string Id, string DisplayName);

public interface ISystemModule
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<SystemVariant> Variants { get; }
}
