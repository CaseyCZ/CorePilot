using System.Text.Json;
using CorePilot.Core;

namespace CorePilot.Hardware;

public sealed class HardwareSnifferReportParser
{
    private static readonly (string JsonName, string Category)[] DeviceSections =
    [
        ("GPU", "GPU"),
        ("Network", "Network"),
        ("Sound", "Audio"),
        ("USB Controllers", "USB"),
        ("Storage Controllers", "Storage"),
        ("Bluetooth", "Bluetooth"),
        ("SD Controller", "SD"),
        ("Input", "Input")
    ];

    public async Task<HardwareReport> MergeAsync(
        string reportPath,
        HardwareReport localReport,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reportPath))
            throw new FileNotFoundException("Hardware Sniffer Report.json was not found.", reportPath);

        await using var stream = File.OpenRead(reportPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Hardware Sniffer report root must be a JSON object.");

        var motherboard = RequireObject(root, "Motherboard");
        var bios = RequireObject(root, "BIOS");
        var cpu = RequireObject(root, "CPU");
        RequireObject(root, "GPU");
        RequireObject(root, "Network");
        RequireObject(root, "USB Controllers");
        RequireObject(root, "Storage Controllers");

        var boardName = GetString(motherboard, "Name");
        var chipset = GetString(motherboard, "Chipset");
        var boardDisplay = JoinUseful(boardName, chipset is "Unknown" or "" ? "" : $"({chipset})");

        var platform = GetString(motherboard, "Platform");
        var firmware = GetString(bios, "Firmware Type");
        var secureBootText = GetString(bios, "Secure Boot");
        bool? secureBoot = secureBootText.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            ? true
            : secureBootText.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                ? false
                : null;

        var cpuName = GetString(cpu, "Processor Name");
        var cpuCores = ParseInt(GetString(cpu, "Core Count"), localReport.CpuCores);

        var deepDevices = new List<HardwareDeviceInfo>();
        foreach (var (jsonName, category) in DeviceSections)
            ReadDeviceSection(root, jsonName, category, deepDevices);

        var mergedDevices = new List<HardwareDeviceInfo>(deepDevices);

        foreach (var local in localReport.Devices)
        {
            var alreadyPresent = deepDevices.Any(deep =>
                deep.Category.Equals(local.Category, StringComparison.OrdinalIgnoreCase) &&
                deep.Name.Equals(local.Name, StringComparison.OrdinalIgnoreCase));

            if (!alreadyPresent)
                mergedDevices.Add(local);
        }

        return localReport with
        {
            FormFactor = string.IsNullOrWhiteSpace(platform) ? localReport.FormFactor : platform,
            Cpu = string.IsNullOrWhiteSpace(cpuName) ? localReport.Cpu : cpuName,
            CpuCores = cpuCores,
            Motherboard = string.IsNullOrWhiteSpace(boardDisplay) ? localReport.Motherboard : boardDisplay,
            FirmwareMode = string.IsNullOrWhiteSpace(firmware) ? localReport.FirmwareMode : firmware,
            SecureBoot = secureBoot,
            Devices = mergedDevices
                .DistinctBy(
                    x => $"{x.Category}|{x.Name}|{x.DeviceId}|{x.PnpDeviceId}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void ReadDeviceSection(
        JsonElement root,
        string jsonName,
        string category,
        ICollection<HardwareDeviceInfo> destination)
    {
        if (!root.TryGetProperty(jsonName, out var section) || section.ValueKind != JsonValueKind.Object)
            return;

        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            var props = entry.Value;
            destination.Add(new HardwareDeviceInfo(
                category,
                entry.Name,
                DeviceId: GetString(props, "Device ID"),
                SubsystemId: GetString(props, "Subsystem ID"),
                BusType: GetString(props, "Bus Type"),
                PciPath: GetString(props, "PCI Path"),
                AcpiPath: GetString(props, "ACPI Path")));
        }
    }

    private static JsonElement RequireObject(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Hardware Sniffer report is missing required '{property}' object.");

        return value;
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return "";

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : value.ToString().Trim();
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static string JoinUseful(params string[] values) =>
        string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
}
