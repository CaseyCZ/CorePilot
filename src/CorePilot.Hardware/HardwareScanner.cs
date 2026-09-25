using System.Management;
using CorePilot.Core;
using Microsoft.Win32;

namespace CorePilot.Hardware;

public sealed class HardwareScanner
{
    public Task<HardwareReport> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Scan, cancellationToken);

    public Task<IReadOnlyList<UsbDriveInfo>> ScanDisksAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<UsbDriveInfo>>(ScanDisks, cancellationToken);

    private static HardwareReport Scan()
    {
        var computer = First("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var board = First("SELECT Manufacturer, Product FROM Win32_BaseBoard");

        var cpuName = Value(cpu, "Name");
        var cores = Value(cpu, "NumberOfCores");
        var threads = Value(cpu, "NumberOfLogicalProcessors");
        var cpuDetails = string.IsNullOrWhiteSpace(cores) ? cpuName : $"{cpuName} ({cores}C/{threads}T)";

        return new HardwareReport(
            Environment.MachineName,
            Value(computer, "Manufacturer"),
            Value(computer, "Model"),
            cpuDetails,
            JoinNonEmpty(Value(board, "Manufacturer"), Value(board, "Product")),
            ReadFirmwareMode(),
            ReadSecureBoot(),
            LongValue(computer, "TotalPhysicalMemory"),
            QueryNames("SELECT Name FROM Win32_VideoController"),
            QueryNames("SELECT Name FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetEnabled=True"),
            QueryNames("SELECT Name FROM Win32_SoundDevice"),
            ScanDisks());
    }

    private static IReadOnlyList<UsbDriveInfo> ScanDisks()
    {
        var result = new List<UsbDriveInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, InterfaceType, Size, PNPDeviceID FROM Win32_DiskDrive");

        foreach (ManagementObject item in searcher.Get())
        {
            var interfaceType = Value(item, "InterfaceType");
            var pnpId = Value(item, "PNPDeviceID");
            var isUsb = interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase)
                        || pnpId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase)
                        || pnpId.StartsWith("USB", StringComparison.OrdinalIgnoreCase);

            result.Add(new UsbDriveInfo(
                Value(item, "DeviceID"),
                Value(item, "Model", "Unknown disk"),
                LongValue(item, "Size"),
                isUsb));
        }

        return result.OrderByDescending(x => x.IsUsb)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> QueryNames(string query)
    {
        var values = new List<string>();
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementObject item in searcher.Get())
        {
            var value = Value(item, "Name");
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ManagementObject? First(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static string Value(ManagementBaseObject? item, string property, string fallback = "")
    {
        if (item is null) return fallback;
        var value = item[property]?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static long LongValue(ManagementBaseObject? item, string property)
    {
        if (item is null) return 0;
        return long.TryParse(item[property]?.ToString(), out var value) ? value : 0;
    }

    private static string ReadFirmwareMode()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
        return Convert.ToInt32(key?.GetValue("PEFirmwareType") ?? 0) switch
        {
            2 => "UEFI",
            1 => "Legacy BIOS",
            _ => "Unknown"
        };
    }

    private static bool? ReadSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var value = key?.GetValue("UEFISecureBootEnabled");
            return value is null ? null : Convert.ToInt32(value) == 1;
        }
        catch { return null; }
    }

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
}
