using System.Text.RegularExpressions;
using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed class MacOSCompatibilityAnalyzer
{
    private const string NvidiaGuide = "https://dortania.github.io/GPU-Buyers-Guide/modern-gpus/nvidia-gpu.html";
    private const string AmdGuide = "https://dortania.github.io/GPU-Buyers-Guide/modern-gpus/amd-gpu.html";
    private const string IntelGuide = "https://dortania.github.io/GPU-Buyers-Guide/modern-gpus/intel-gpu.html";
    private const string AmdVanilla = "https://github.com/AMD-OSX/AMD_Vanilla";
    private const string BiosGuide = "https://dortania.github.io/OpenCore-Install-Guide/config.plist/kaby-lake.html";

    public CompatibilityReport Analyze(HardwareReport hardware, SystemVariant target)
    {
        var findings = new List<CompatibilityFinding>();
        var kexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Lilu.kext",
            "VirtualSMC.kext"
        };
        var patches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bootArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AnalyzeFirmware(hardware, findings);
        AnalyzeCpu(hardware, findings, patches);
        AnalyzeGraphics(hardware, target, findings, kexts, bootArgs);
        AnalyzeNetwork(hardware, findings, kexts);

        if (hardware.DevicesByCategory("Audio").Count > 0)
            kexts.Add("AppleALC.kext (layout-id still needs device-specific resolution)");

        return new CompatibilityReport(
            target.Id,
            findings,
            kexts.OrderBy(x => x).ToArray(),
            patches.OrderBy(x => x).ToArray(),
            bootArgs.OrderBy(x => x).ToArray());
    }

    private static void AnalyzeFirmware(HardwareReport hardware, ICollection<CompatibilityFinding> findings)
    {
        if (!hardware.FirmwareMode.Equals("UEFI", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new(
                CompatibilityState.Blocked,
                "Firmware",
                "CorePilot macOS workflow requires UEFI",
                $"Detected firmware mode: {hardware.FirmwareMode}.",
                "Enable UEFI boot mode and disable CSM/Legacy mode before creating the installer.",
                BiosGuide));
        }
        else
        {
            findings.Add(new(
                CompatibilityState.Supported,
                "Firmware",
                "UEFI detected",
                "Firmware mode is suitable for the standard OpenCore workflow.",
                Reference: BiosGuide));
        }

        if (hardware.SecureBoot is true)
        {
            findings.Add(new(
                CompatibilityState.ActionRequired,
                "Firmware",
                "Firmware Secure Boot is enabled",
                "Dortania's initial-install BIOS guidance disables firmware Secure Boot.",
                "Disable firmware Secure Boot for the initial CorePilot/OpenCore installation workflow.",
                BiosGuide));
        }
    }

    private static void AnalyzeCpu(
        HardwareReport hardware,
        ICollection<CompatibilityFinding> findings,
        ISet<string> patches)
    {
        var cpu = hardware.Cpu;

        if (cpu.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            patches.Add("AMD Vanilla kernel patches");
            patches.Add($"Core-count patch: {hardware.CpuCores} physical cores");
            findings.Add(new(
                CompatibilityState.ActionRequired,
                "CPU",
                "AMD Ryzen detected",
                $"Ryzen requires AMD Vanilla kernel patches and a physical core-count patch ({hardware.CpuCores} cores detected).",
                "CorePilot will generate the AMD patch set instead of using an Intel kernel configuration.",
                AmdVanilla));

            if (LooksLikeZen4Desktop(cpu))
            {
                patches.Add("Zen 4 IOPCIFamily patch");
                findings.Add(new(
                    CompatibilityState.ActionRequired,
                    "CPU",
                    "Zen 4-class Ryzen detected",
                    "Ryzen 7000 desktop CPUs require the AMD Vanilla IOPCIFamily patch for Zen 4 stability.",
                    "Include the Zen 4 IOPCIFamily patch in Kernel -> Patch.",
                    AmdVanilla));
            }
            return;
        }

        if (cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new(
                CompatibilityState.Warning,
                "CPU",
                "Intel CPU detected",
                "Intel support is generation-specific and will be resolved from CPUID/model rules in the next compatibility database pass.",
                "Keep this as a verification item until CorePilot resolves the exact generation."));
            return;
        }

        findings.Add(new(
            CompatibilityState.Unknown,
            "CPU",
            "CPU family needs verification",
            cpu,
            "Do not create macOS media until the CPU family is classified."));
    }

    private static void AnalyzeGraphics(
        HardwareReport hardware,
        SystemVariant target,
        ICollection<CompatibilityFinding> findings,
        ISet<string> kexts,
        ISet<string> bootArgs)
    {
        var gpus = hardware.DevicesByCategory("GPU");
        if (gpus.Count == 0)
        {
            findings.Add(new(
                CompatibilityState.Blocked,
                "GPU",
                "No GPU detected",
                "CorePilot could not find a display adapter.",
                "Rescan hardware or inspect Device Manager."));
            return;
        }

        var hasUsableCandidate = false;
        var hasUnknownCandidate = false;

        foreach (var gpu in gpus)
        {
            var name = gpu.Name;

            if (IsUnsupportedNvidia(name))
            {
                findings.Add(new(
                    CompatibilityState.ActionRequired,
                    "GPU",
                    $"{name} is unsupported by macOS",
                    "Modern NVIDIA RTX/GTX 16-series GPUs have no macOS driver.",
                    "The GPU must be disabled/ignored by macOS. A separate supported display GPU is required for acceleration.",
                    NvidiaGuide));
                continue;
            }

            if (IsUnsupportedIntelXe(name))
            {
                findings.Add(new(
                    CompatibilityState.Blocked,
                    "GPU",
                    $"{name} is not supported",
                    "Xe-based Intel graphics do not have macOS support.",
                    "Use a supported graphics path.",
                    IntelGuide));
                continue;
            }

            if (IsUnsupportedAmd(name, hardware.Cpu))
            {
                findings.Add(new(
                    CompatibilityState.ActionRequired,
                    "GPU",
                    $"{name} is not a supported accelerated macOS graphics path",
                    "Navi 3x, Navi 24 and Zen 4/5 Radeon APUs are listed as unsupported.",
                    "Use a supported AMD dGPU or another validated graphics path.",
                    AmdGuide));
                continue;
            }

            if (IsNativeAmdCandidate(name))
            {
                hasUsableCandidate = true;
                kexts.Add("WhateverGreen.kext");
                bootArgs.Add("agdpmod=pikera");

                var state = target.Id == "tahoe-26"
                    ? CompatibilityState.Warning
                    : CompatibilityState.Supported;

                findings.Add(new(
                    state,
                    "GPU",
                    $"{name} is a known AMD candidate",
                    target.Id == "tahoe-26"
                        ? "The public GPU Buyers Guide currently documents these Navi cards through Sequoia; CorePilot will keep Tahoe as a verification item."
                        : "This GPU family is documented as a supported macOS candidate.",
                    "Use Lilu + WhateverGreen; most Navi 21/23 cards need agdpmod=pikera.",
                    AmdGuide));
                continue;
            }

            if (NeedsAmdSpoof(name))
            {
                hasUsableCandidate = true;
                kexts.Add("WhateverGreen.kext");
                bootArgs.Add("agdpmod=pikera");
                findings.Add(new(
                    CompatibilityState.ActionRequired,
                    "GPU",
                    $"{name} requires spoofing",
                    "This AMD GPU is documented as working through a supported device identity.",
                    "CorePilot must generate the matching device-id spoof.",
                    AmdGuide));
                continue;
            }

            hasUnknownCandidate = true;
            findings.Add(new(
                CompatibilityState.Unknown,
                "GPU",
                $"{name} needs exact compatibility resolution",
                string.IsNullOrWhiteSpace(gpu.PnpDeviceId) ? "No PCI ID was returned." : gpu.PnpDeviceId,
                "Resolve the exact PCI device ID before building EFI."));
        }

        if (!hasUsableCandidate && !hasUnknownCandidate)
        {
            findings.Add(new(
                CompatibilityState.Blocked,
                "GPU",
                "No supported accelerated graphics path detected",
                "All detected GPUs are currently classified as unsupported for accelerated macOS graphics.",
                "Add/enable a supported GPU before using the native macOS installer."));
        }
    }

    private static void AnalyzeNetwork(
        HardwareReport hardware,
        ICollection<CompatibilityFinding> findings,
        ISet<string> kexts)
    {
        foreach (var adapter in hardware.DevicesByCategory("Network"))
        {
            if (adapter.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                && (adapter.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                    || adapter.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                    || adapter.Name.Contains("AX", StringComparison.OrdinalIgnoreCase)))
            {
                kexts.Add("itlwm/AirportItlwm (version-specific)");
                findings.Add(new(
                    CompatibilityState.ActionRequired,
                    "Wi-Fi",
                    adapter.Name,
                    "Intel Wi-Fi is not natively supported by macOS and requires the OpenIntelWireless stack.",
                    "Resolve the exact chipset and target-specific itlwm/AirportItlwm build."));
            }
        }
    }

    private static bool LooksLikeZen4Desktop(string cpu) =>
        Regex.IsMatch(cpu, @"Ryzen\s+[3579]\s+(7600|7700|7800|7900|7950)", RegexOptions.IgnoreCase);

    private static bool IsUnsupportedNvidia(string name) =>
        Regex.IsMatch(name, @"NVIDIA.*(RTX\s*(20|30|40|50)|GTX\s*16)", RegexOptions.IgnoreCase);

    private static bool IsUnsupportedIntelXe(string name) =>
        name.Contains("Iris Xe", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(name, @"Intel.*\bArc\b", RegexOptions.IgnoreCase);

    private static bool IsUnsupportedAmd(string name, string cpu)
    {
        if (Regex.IsMatch(name, @"Radeon\s+RX\s+(6(300|400|500)|7\d{3})", RegexOptions.IgnoreCase))
            return true;

        return cpu.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase)
               && LooksLikeZen4Desktop(cpu)
               && name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNativeAmdCandidate(string name) =>
        Regex.IsMatch(name, @"Radeon\s+RX\s+(6600(\s+XT)?|6800(\s+XT)?|6900\s+XT)", RegexOptions.IgnoreCase)
        || Regex.IsMatch(name, @"Radeon\s+Pro\s+W(6600|6800)", RegexOptions.IgnoreCase);

    private static bool NeedsAmdSpoof(string name) =>
        Regex.IsMatch(name, @"Radeon\s+RX\s+(6650\s+XT|6950\s+XT)", RegexOptions.IgnoreCase);
}
