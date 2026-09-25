using System.Management;
using CorePilot.Core;
using Microsoft.Win32;

namespace CorePilot.Hardware;

public sealed class HardwareScanner
{
    private static readonly UsbTargetInspector DiskInspector = new();

    public Task<HardwareReport> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Scan, cancellationToken);

    public Task<IReadOnlyList<UsbDriveInfo>> ScanDisksAsync(CancellationToken cancellationToken = default) =>
        DiskInspector.InspectAsync(cancellationToken);

    private static HardwareReport Scan()
    {
        var computer = First("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var board = First("SELECT Manufacturer, Product FROM Win32_BaseBoard");

        var devices = new List<HardwareDeviceInfo>();
        devices.AddRange(QueryDevices("GPU", "SELECT Name, PNPDeviceID FROM Win32_VideoController"));
        devices.AddRange(QueryDevices("Network", "SELECT Name, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetEnabled=True"));
        devices.AddRange(QueryDevices("Audio", "SELECT Name, PNPDeviceID FROM Win32_SoundDevice"));
        devices.AddRange(QueryDevices("USB", "SELECT Name, PNPDeviceID FROM Win32_USBController"));

        return new HardwareReport(
            Environment.MachineName,
            Value(computer, "Manufacturer"),
            Value(computer, "Model"),
            ReadFormFactor(),
            Value(cpu, "Name"),
            IntValue(cpu, "NumberOfCores"),
            IntValue(cpu, "NumberOfLogicalProcessors"),
            JoinNonEmpty(Value(board, "Manufacturer"), Value(board, "Product")),
            ReadFirmwareMode(),
            ReadSecureBoot(),
            LongValue(computer, "TotalPhysicalMemory"),
            devices.DistinctBy(x => $"{x.Category}|{x.Name}|{x.PnpDeviceId}", StringComparer.OrdinalIgnoreCase).ToArray(),
            DiskInspector.Inspect());
    }

    private static IReadOnlyList<HardwareDeviceInfo> QueryDevices(string category, string query)
    {
        var values = new List<HardwareDeviceInfo>();
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementObject item in searcher.Get())
        {
            using (item)
            {
                var name = Value(item, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                values.Add(new HardwareDeviceInfo(
                    category,
                    name,
                    Value(item, "PNPDeviceID")));
            }
        }

        return values;
    }

    private static ManagementObject? First(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static string ReadFormFactor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ChassisTypes FROM Win32_SystemEnclosure");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["ChassisTypes"] is not ushort[] types)
                        continue;

                    if (types.Any(x => x is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32))
                        return "Laptop";
                }
            }
        }
        catch
        {
        }

        return "Desktop";
    }

    private static string Value(ManagementBaseObject? item, string property, string fallback = "")
    {
        if (item is null)
            return fallback;

        var value = item[property]?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int IntValue(ManagementBaseObject? item, string property)
    {
        if (item is null)
            return 0;

        return int.TryParse(item[property]?.ToString(), out var value) ? value : 0;
    }

    private static long LongValue(ManagementBaseObject? item, string property)
    {
        if (item is null)
            return 0;

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
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");

            var value = key?.GetValue("UEFISecureBootEnabled");
            return value is null ? null : Convert.ToInt32(value) == 1;
        }
        catch
        {
            return null;
        }
    }

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
}
