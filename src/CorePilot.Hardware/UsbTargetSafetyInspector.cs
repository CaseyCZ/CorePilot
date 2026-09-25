using System.Management;
using System.Security.Cryptography;
using System.Text;
using CorePilot.Core;

namespace CorePilot.Hardware;

public sealed class UsbTargetSafetyInspector
{
    public Task<UsbTargetSafetyReport> InspectAsync(
        UsbDriveInfo target,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(target), cancellationToken);

    private static UsbTargetSafetyReport Inspect(UsbDriveInfo target)
    {
        var systemDrive = ReadSystemDrive();
        var pageFileDrives = ReadPageFileDrives();
        var volumesByLetter = ReadVolumeFlags();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Index, Model, SerialNumber, InterfaceType, MediaType, " +
            "PNPDeviceID, Size FROM Win32_DiskDrive");

        foreach (ManagementObject disk in searcher.Get())
        {
            if (!Value(disk, "DeviceID").Equals(
                    target.DeviceId,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            return InspectDisk(
                target,
                disk,
                systemDrive,
                pageFileDrives,
                volumesByLetter);
        }

        return new(
            target.DeviceId,
            -1,
            target.Model,
            "",
            "Unknown",
            "Unknown",
            "",
            target.SizeBytes,
            target.IsUsb,
            false,
            Fingerprint(target.Model, "", "", target.SizeBytes),
            UsbTargetSafetyLevel.Blocked,
            ["Selected physical disk is no longer present or could not be resolved."],
            []);
    }

    private static UsbTargetSafetyReport InspectDisk(
        UsbDriveInfo target,
        ManagementObject disk,
        string systemDrive,
        IReadOnlySet<string> pageFileDrives,
        IReadOnlyDictionary<string, VolumeFlags> volumeFlags)
    {
        var deviceId = Value(disk, "DeviceID");
        var diskIndex = IntValue(disk, "Index", -1);
        var model = Value(disk, "Model", target.Model);
        var serial = Value(disk, "SerialNumber");
        var interfaceType = Value(disk, "InterfaceType", "Unknown");
        var mediaType = Value(disk, "MediaType", "Unknown");
        var pnpId = Value(disk, "PNPDeviceID");
        var size = LongValue(disk, "Size", target.SizeBytes);

        var isUsb =
            interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            pnpId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
            pnpId.StartsWith("USB", StringComparison.OrdinalIgnoreCase);

        var isRemovableMedia =
            mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);

        var reasons = new List<string>();
        var partitions = new List<UsbPartitionInfo>();
        var containsProtectedContent = false;
        var associationFailed = false;

        try
        {
            using var relatedPartitions = disk.GetRelated("Win32_DiskPartition");

            foreach (ManagementObject partition in relatedPartitions)
            {
                var partitionVolumes = new List<UsbVolumeInfo>();
                var bootPartition = BoolValue(partition, "BootPartition");
                var partitionDeviceId = Value(partition, "DeviceID");
                var partitionIndex = IntValue(partition, "Index", -1);

                if (bootPartition)
                {
                    containsProtectedContent = true;
                    reasons.Add($"Partition {partitionIndex} is marked as a Windows boot partition.");
                }

                try
                {
                    using var logicalDisks = partition.GetRelated("Win32_LogicalDisk");

                    foreach (ManagementObject logical in logicalDisks)
                    {
                        var driveLetter = NormalizeDrive(Value(logical, "DeviceID"));
                        var flags = volumeFlags.TryGetValue(driveLetter, out var known)
                            ? known
                            : new VolumeFlags(false, false);

                        var isSystemDrive =
                            !string.IsNullOrWhiteSpace(driveLetter) &&
                            driveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);

                        var hasPageFile =
                            !string.IsNullOrWhiteSpace(driveLetter) &&
                            pageFileDrives.Contains(driveLetter);

                        var isProtected =
                            isSystemDrive ||
                            flags.IsBootVolume ||
                            flags.IsSystemVolume ||
                            hasPageFile;

                        if (isProtected)
                        {
                            containsProtectedContent = true;
                            var labels = new List<string>();
                            if (isSystemDrive) labels.Add("current Windows system drive");
                            if (flags.IsBootVolume) labels.Add("BootVolume");
                            if (flags.IsSystemVolume) labels.Add("SystemVolume");
                            if (hasPageFile) labels.Add("pagefile");
                            reasons.Add(
                                $"{driveLetter} is protected ({string.Join(", ", labels)}).");
                        }

                        partitionVolumes.Add(new(
                            driveLetter,
                            Value(logical, "VolumeName"),
                            Value(logical, "FileSystem"),
                            LongValue(logical, "Size"),
                            LongValue(logical, "FreeSpace"),
                            flags.IsBootVolume,
                            flags.IsSystemVolume || isSystemDrive,
                            hasPageFile));
                    }
                }
                catch (Exception ex)
                {
                    associationFailed = true;
                    reasons.Add(
                        $"Could not fully resolve volumes for partition {partitionIndex}: {ex.Message}");
                }

                partitions.Add(new(
                    partitionDeviceId,
                    partitionIndex,
                    Value(partition, "Type"),
                    LongValue(partition, "Size"),
                    bootPartition,
                    BoolValue(partition, "PrimaryPartition"),
                    partitionVolumes));
            }
        }
        catch (Exception ex)
        {
            associationFailed = true;
            reasons.Add($"Could not fully resolve disk partitions: {ex.Message}");
        }

        UsbTargetSafetyLevel level;

        if (!isUsb)
        {
            level = UsbTargetSafetyLevel.Blocked;
            reasons.Add("The selected disk is not positively identified as USB.");
        }
        else if (size <= 0)
        {
            level = UsbTargetSafetyLevel.Blocked;
            reasons.Add("Disk size could not be determined.");
        }
        else if (containsProtectedContent)
        {
            level = UsbTargetSafetyLevel.Blocked;
            reasons.Add("A disk containing Windows boot/system/pagefile content can never be a write target.");
        }
        else if (associationFailed)
        {
            level = UsbTargetSafetyLevel.Blocked;
            reasons.Add("Disk topology is incomplete, so CorePilot fails closed.");
        }
        else if (!isRemovableMedia)
        {
            level = UsbTargetSafetyLevel.StrongConfirmation;
            reasons.Add("USB bus detected, but Windows reports fixed media (typical USB SSD/HDD).");
        }
        else if (string.IsNullOrWhiteSpace(serial))
        {
            level = UsbTargetSafetyLevel.StrongConfirmation;
            reasons.Add("Removable USB detected, but no stable serial number is available.");
        }
        else
        {
            level = UsbTargetSafetyLevel.SafeCandidate;
            reasons.Add("Removable USB with stable identity and no protected Windows volumes detected.");
        }

        return new(
            deviceId,
            diskIndex,
            model,
            serial,
            interfaceType,
            mediaType,
            pnpId,
            size,
            isUsb,
            isRemovableMedia,
            Fingerprint(model, serial, pnpId, size),
            level,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            partitions.OrderBy(x => x.Index).ToArray());
    }

    private static Dictionary<string, VolumeFlags> ReadVolumeFlags()
    {
        var result = new Dictionary<string, VolumeFlags>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DriveLetter, BootVolume, SystemVolume FROM Win32_Volume " +
                "WHERE DriveLetter IS NOT NULL");

            foreach (ManagementObject item in searcher.Get())
            {
                var drive = NormalizeDrive(Value(item, "DriveLetter"));
                if (string.IsNullOrWhiteSpace(drive))
                    continue;

                result[drive] = new(
                    BoolValue(item, "BootVolume"),
                    BoolValue(item, "SystemVolume"));
            }
        }
        catch
        {
            // The caller also checks Environment/Win32_OperatingSystem and pagefile.
            // Missing Win32_Volume flags alone must not produce a false "blocked".
        }

        return result;
    }

    private static string ReadSystemDrive()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SystemDrive FROM Win32_OperatingSystem");

            foreach (ManagementObject item in searcher.Get())
            {
                var drive = NormalizeDrive(Value(item, "SystemDrive"));
                if (!string.IsNullOrWhiteSpace(drive))
                    return drive;
            }
        }
        catch
        {
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return NormalizeDrive(Path.GetPathRoot(windows) ?? "");
    }

    private static HashSet<string> ReadPageFileDrives()
    {
        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PageFileUsage");

            foreach (ManagementObject item in searcher.Get())
            {
                var name = Value(item, "Name");
                if (name.Length >= 2 && name[1] == ':')
                    drives.Add(NormalizeDrive(name[..2]));
            }
        }
        catch
        {
        }

        return drives;
    }

    private static string Fingerprint(
        string model,
        string serial,
        string pnpId,
        long size)
    {
        var source = string.Join(
            "\n",
            model.Trim(),
            serial.Trim(),
            pnpId.Trim(),
            size.ToString());

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static string NormalizeDrive(string value)
    {
        value = value.Trim().TrimEnd('\\', '/');
        return value.Length >= 2 && value[1] == ':'
            ? value[..2].ToUpperInvariant()
            : value.ToUpperInvariant();
    }

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
        int fallback = 0) =>
        item is not null &&
        int.TryParse(item[property]?.ToString(), out var value)
            ? value
            : fallback;

    private static long LongValue(
        ManagementBaseObject? item,
        string property,
        long fallback = 0) =>
        item is not null &&
        long.TryParse(item[property]?.ToString(), out var value)
            ? value
            : fallback;

    private static bool BoolValue(
        ManagementBaseObject? item,
        string property)
    {
        if (item is null || item[property] is null)
            return false;

        try
        {
            return Convert.ToBoolean(item[property]);
        }
        catch
        {
            return false;
        }
    }

    private sealed record VolumeFlags(
        bool IsBootVolume,
        bool IsSystemVolume);
}
