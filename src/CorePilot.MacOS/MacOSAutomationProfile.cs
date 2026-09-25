using System.Text.RegularExpressions;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed record MacOSAutomationDecision(
    string Category,
    string Subject,
    string Choice,
    string Reason,
    bool RequiresReview = false);

public sealed record MacOSAutomationProfile(
    string TargetId,
    string DarwinVersion,
    string SmbiosModel,
    string GraphicsMode,
    string WifiMode,
    string AudioMode,
    bool RequiresReview,
    bool CanBuildEfi,
    IReadOnlyList<string> EnabledDevices,
    IReadOnlyList<string> DisabledDevices,
    IReadOnlyList<MacOSAutomationDecision> Decisions);

public sealed class MacOSAutomationPlanner
{
    public MacOSAutomationProfile Build(
        HardwareReport hardware,
        SystemVariant target,
        CompatibilityReport compatibility)
    {
        var decisions = new List<MacOSAutomationDecision>();
        var enabledDevices = new List<string>();
        var disabledDevices = new List<string>();

        var darwin = TargetDarwin(target.Id);
        var smbios = SelectSmbios(hardware, darwin);

        decisions.Add(new(
            "SMBIOS",
            hardware.Motherboard,
            smbios,
            "Automatic default follows the same platform-oriented strategy used by OpCore Simplify."));

        var graphicsMode = ResolveGraphics(
            hardware,
            enabledDevices,
            disabledDevices,
            decisions);

        var wifiMode = ResolveWifi(hardware, darwin, decisions);
        var audioMode = ResolveAudio(hardware, darwin, decisions);

        if (hardware.Cpu.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            decisions.Add(new(
                "Kernel",
                hardware.Cpu,
                $"AMD Vanilla · {hardware.CpuCores} physical cores",
                "AMD Ryzen requires the AMD Vanilla patch set and the physical core-count replacement."));
        }

        if (hardware.SecureBoot is true)
        {
            decisions.Add(new(
                "Firmware",
                "Secure Boot",
                "Disable before first boot",
                "The initial OpenCore installation workflow expects firmware Secure Boot disabled."));
        }

        decisions.Add(new(
            "USB",
            "USB mapping",
            "UTBDefault for installer; map ports after installation",
            "This matches the upstream bootstrap workflow and avoids guessing the final USB map."));

        var requiresReview =
            decisions.Any(x => x.RequiresReview) ||
            compatibility.Findings.Any(x => x.State == CompatibilityState.Unknown);

        var canBuild =
            compatibility.CanProceed &&
            !graphicsMode.Equals("Blocked", StringComparison.OrdinalIgnoreCase);

        return new(
            target.Id,
            darwin,
            smbios,
            graphicsMode,
            wifiMode,
            audioMode,
            requiresReview,
            canBuild,
            enabledDevices,
            disabledDevices,
            decisions);
    }

    private static string ResolveGraphics(
        HardwareReport hardware,
        ICollection<string> enabled,
        ICollection<string> disabled,
        ICollection<MacOSAutomationDecision> decisions)
    {
        var candidates = hardware.DevicesByCategory("GPU");
        var supported = new List<HardwareDeviceInfo>();

        foreach (var gpu in candidates)
        {
            var vendor = Vendor(gpu);
            var unsupported =
                vendor == "NVIDIA" && Regex.IsMatch(gpu.Name, @"(RTX\s*\d+|GTX\s*16)", RegexOptions.IgnoreCase) ||
                gpu.Name.Contains("Iris Xe", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(gpu.Name, @"Intel.*\bArc\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(gpu.Name, @"Radeon\s+RX\s+(6(300|400|500)|7\d{3})", RegexOptions.IgnoreCase) ||
                IsModernRyzenApu(hardware, gpu);

            if (unsupported)
            {
                disabled.Add(gpu.Name);
                decisions.Add(new(
                    "GPU",
                    gpu.Name,
                    "Disable for macOS",
                    $"Detected {vendor} graphics path is not suitable for accelerated macOS."));
                continue;
            }

            if (vendor == "AMD" &&
                Regex.IsMatch(gpu.Name, @"Radeon\s+(RX|Pro)", RegexOptions.IgnoreCase))
            {
                supported.Add(gpu);
                continue;
            }

            decisions.Add(new(
                "GPU",
                gpu.Name,
                "Keep unresolved",
                "CorePilot cannot safely choose this graphics device automatically yet.",
                RequiresReview: true));
        }

        if (supported.Count == 0)
        {
            decisions.Add(new(
                "GPU",
                "Accelerated graphics",
                "Blocked",
                "No automatically supported accelerated graphics device remains after filtering."));
            return "Blocked";
        }

        if (supported.Count > 1)
        {
            var names = string.Join(" + ", supported.Select(x => x.Name));
            decisions.Add(new(
                "GPU",
                "Multiple compatible graphics devices",
                names,
                "CorePilot will not silently choose between multiple compatible GPUs.",
                RequiresReview: true));

            foreach (var gpu in supported)
                enabled.Add(gpu.Name);

            return names;
        }

        var selectedGpu = supported[0];
        enabled.Add(selectedGpu.Name);
        decisions.Add(new(
            "GPU",
            "Primary macOS graphics",
            selectedGpu.Name,
            "Supported AMD graphics candidate selected automatically."));

        return selectedGpu.Name;
    }

    private static string ResolveWifi(
        HardwareReport hardware,
        string darwin,
        ICollection<MacOSAutomationDecision> decisions)
    {
        var wifiDevices = hardware.DevicesByCategory("Network").Where(x =>
            x.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            x.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
            x.Name.Contains("AX", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (wifiDevices.Length > 1)
        {
            decisions.Add(new(
                "Wi-Fi",
                "Multiple wireless adapters",
                string.Join(" + ", wifiDevices.Select(x => x.Name)),
                "Upstream device selection would be ambiguous.",
                RequiresReview: true));
        }

        var intelWifi = wifiDevices.FirstOrDefault(x => Vendor(x) == "Intel");

        if (intelWifi is null)
            return "Auto";

        var darwinMajor = int.TryParse(darwin.Split('.')[0], out var major) ? major : 0;
        var choice = darwinMajor >= 23 ? "itlwm + HeliPort" : "AirportItlwm";

        decisions.Add(new(
            "Wi-Fi",
            intelWifi.Name,
            choice,
            darwinMajor >= 23
                ? "For Sonoma and newer, upstream recommends itlwm as the more stable default."
                : "For older supported releases, AirportItlwm is the upstream default."));

        return choice;
    }

    private static string ResolveAudio(
        HardwareReport hardware,
        string darwin,
        ICollection<MacOSAutomationDecision> decisions)
    {
        if (hardware.DevicesByCategory("Audio").Count == 0)
            return "None detected";

        var darwinMajor = int.TryParse(darwin.Split('.')[0], out var major) ? major : 0;

        if (darwinMajor >= 25)
        {
            decisions.Add(new(
                "Audio",
                "macOS Tahoe",
                "Defer audio during installation",
                "Tahoe removed the normal AppleHDA path used by AppleALC. CorePilot will not silently enable OCLP/root patches.",
                RequiresReview: false));

            return "Deferred on Tahoe";
        }

        decisions.Add(new(
            "Audio",
            "Detected audio devices",
            "AppleALC",
            "AppleALC is the standard automatic choice before Tahoe; exact layout-id remains device-specific."));

        return "AppleALC";
    }

    private static string SelectSmbios(HardwareReport hardware, string darwin)
    {
        if (hardware.FormFactor.Equals("Laptop", StringComparison.OrdinalIgnoreCase))
            return "MacBookPro16,2";

        var darwinMajor = int.TryParse(darwin.Split('.')[0], out var major) ? major : 0;

        if (hardware.Cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return darwinMajor >= 25 ? "MacPro7,1" : "iMacPro1,1";

        return darwinMajor >= 25 ? "MacPro7,1" : "iMacPro1,1";
    }

    private static string Vendor(HardwareDeviceInfo device)
    {
        if (device.DeviceId.StartsWith("10DE-", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA";

        if (device.DeviceId.StartsWith("1002-", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "AMD";

        if (device.DeviceId.StartsWith("8086-", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "Intel";

        return "Unknown";
    }

    private static bool IsModernRyzenApu(HardwareReport hardware, HardwareDeviceInfo gpu) =>
        hardware.Cpu.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(hardware.Cpu, @"Ryzen\s+[3579]\s+(7\d{3}|8\d{3}|9\d{3})", RegexOptions.IgnoreCase) &&
        (gpu.Name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
         gpu.Name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase));

    private static string TargetDarwin(string targetId) => targetId switch
    {
        "tahoe-26" => "25.0.0",
        "sequoia-15" => "24.0.0",
        "sonoma-14" => "23.0.0",
        "ventura-13" => "22.0.0",
        _ => "0.0.0"
    };
}
