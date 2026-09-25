using System.Management;
using CorePilot.Core;

namespace CorePilot.Hardware;

/// <summary>
/// Read-only physical disk inspector used to classify future USB write targets.
/// This class never opens a raw disk handle and never modifies partitions or volumes.
/// </summary>
public sealed class UsbTargetInspector
{
    public Task<IReadOnlyList<UsbDriveInfo>> InspectAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<UsbDriveInfo>>(Inspect, cancellationToken);

    public IReadOnlyList<UsbDriveInfo> Inspect()
    {
        var systemDrive = ReadSystemDrive();
        var result = new List<UsbDriveInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Index, Model, SerialNumber, InterfaceType, MediaType, Size, PNPDeviceID " +
            "FROM Win32_DiskDrive");

        foreach (ManagementObject disk in searcher.Get())
        {
            var deviceId = Value(disk, "DeviceID");
            var interfaceType = Value(disk, "InterfaceType");
            var mediaType = Value(disk, "MediaType");
            var pnpId = Value(disk, "PNPDeviceID");

            var isUsb =
                interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                pnpId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                pnpId.StartsWith("USB", StringComparison.OrdinalIgnoreCase);

            var isRemovable =
                mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);

            var volumes = ReadVolumes(disk, systemDrive);
            var isSystemDisk = volumes.Any(x => x.IsSystemVolume);

            result.Add(new UsbDriveInfo(
                deviceId,
                IntValue(disk, "Index", -1),
                Value(disk, "Model", "Unknown disk"),
                NormalizeSerial(Value(disk, "SerialNumber")),
                interfaceType,
                mediaType,
                LongValue(disk, "Size"),
                isUsb,
                isRemovable,
                isSystemDisk,
                volumes));
        }

        return result
            .OrderByDescending(x => x.IsUsb)
            .ThenBy(x => x.IsSystemDisk)
            .ThenBy(x => x.DiskNumber)
            .ToArray();
    }

    private static IReadOnlyList<DiskVolumeInfo> ReadVolumes(
        ManagementObject disk,
        string systemDrive)
    {
        var volumes = new List<DiskVolumeInfo>();

        try
        {
            using var partitions = disk.GetRelated("Win32_DiskPartition");

            foreach (ManagementObject partition in partitions)
            {
                using (partition)
                using (var logicalDisks = partition.GetRelated("Win32_LogicalDisk"))
                {
                    foreach (ManagementObject logical in logicalDisks)
                    {
                        using (logical)
                        {
                            var drive = Value(logical, "DeviceID").TrimEnd('\\');
                            if (string.IsNullOrWhiteSpace(drive))
                                continue;

                            volumes.Add(new DiskVolumeInfo(
                                drive,
                                Value(logical, "VolumeName"),
                                Value(logical, "FileSystem"),
                                LongValue(logical, "Size"),
                                LongValue(logical, "FreeSpace"),
                                drive.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                }
            }
        }
        catch
        {
            // Failure to enumerate volumes makes the disk less trustworthy.
            // The caller still gets the physical disk, while missing identity
            // data will prevent future destructive operations.
        }

        return volumes
            .DistinctBy(x => x.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadSystemDrive()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SystemDrive FROM Win32_OperatingSystem");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var value = Value(item, "SystemDrive").TrimEnd('\\');
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }
        catch
        {
            // Fall through to the local Windows system directory.
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return (Path.GetPathRoot(systemDirectory) ?? "")
            .TrimEnd('\\', '/');
    }

    private static string NormalizeSerial(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : string.Join(" ", value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Value(
        ManagementBaseObject? item,
        string property,
        string fallback = "")
    {
        if (item is null)
            return fallback;

        var value = item[property]?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int IntValue(
        ManagementBaseObject? item,
        string property,
        int fallback = 0)
    {
        if (item is null)
            return fallback;

        return int.TryParse(item[property]?.ToString(), out var value)
            ? value
            : fallback;
    }

    private static long LongValue(
        ManagementBaseObject? item,
        string property)
    {
        if (item is null)
            return 0;

        return long.TryParse(item[property]?.ToString(), out var value)
            ? value
            : 0;
    }
}
