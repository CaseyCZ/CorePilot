using System;

namespace CorePilot.Core;

public sealed class GenericCompatibilityAnalyzer
{
    private const long GiB = 1024L * 1024L * 1024L;

    public CompatibilityReport Analyze(
        string systemId,
        HardwareReport hardware,
        SystemVariant target)
    {
        return systemId.ToLowerInvariant() switch
        {
            "windows" => AnalyzeWindows(hardware, target),
            "linux" => AnalyzeLinux(hardware, target),
            _ => throw new InvalidOperationException(
                $"Generic compatibility analyzer does not support '{systemId}'.")
        };
    }

    private static CompatibilityReport AnalyzeWindows(
        HardwareReport hardware,
        SystemVariant target)
    {
        var findings = new List<CompatibilityFinding>();

        var minMemory = target.Id == "windows-11" ? 4L * GiB : 2L * GiB;
        if (hardware.MemoryBytes >= minMemory)
        {
            findings.Add(new(
                CompatibilityState.Supported,
                "Memory",
                "Memory requirement satisfied",
                $"{hardware.MemoryBytes / (double)GiB:0.#} GB detected."));
        }
        else
        {
            findings.Add(new(
                CompatibilityState.Blocked,
                "Memory",
                "Not enough memory for the selected Windows target",
                $"{hardware.MemoryBytes / (double)GiB:0.#} GB detected; at least {minMemory / GiB} GB is required by this CorePilot pre-check."));
        }

        if (target.Id == "windows-11")
        {
            if (hardware.FirmwareMode.Equals("UEFI", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new(
                    CompatibilityState.Supported,
                    "Firmware",
                    "UEFI detected",
                    "Windows 11 installation media can use the standard UEFI path."));
            }
            else if (hardware.FirmwareMode.Equals("Legacy BIOS", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new(
                    CompatibilityState.Blocked,
                    "Firmware",
                    "Windows 11 requires the UEFI installation path",
                    "The current Windows session reports Legacy BIOS mode.",
                    "Enable UEFI mode before creating/installing Windows 11 media."));
            }
            else
            {
                findings.Add(new(
                    CompatibilityState.Warning,
                    "Firmware",
                    "Firmware mode could not be confirmed",
                    "CorePilot could not reliably confirm UEFI. Verify firmware mode before installation."));
            }

            findings.Add(new(
                hardware.SecureBoot is true
                    ? CompatibilityState.Supported
                    : CompatibilityState.Warning,
                "Secure Boot",
                hardware.SecureBoot is true
                    ? "Secure Boot is enabled"
                    : "Secure Boot is not confirmed as enabled",
                hardware.SecureBoot is null
                    ? "Windows did not expose a reliable Secure Boot state."
                    : hardware.SecureBoot is true
                        ? "Secure Boot state is suitable for the standard Windows 11 path."
                        : "Secure Boot is disabled.",
                hardware.SecureBoot is true
                    ? null
                    : "Enable Secure Boot when supported by the target hardware."));

            findings.Add(hardware.Tpm20 switch
            {
                true => new(
                    CompatibilityState.Supported,
                    "TPM",
                    "TPM 2.0 detected and enabled",
                    "The Windows hardware scan confirmed an enabled TPM 2.0 device."),
                false => new(
                    CompatibilityState.Blocked,
                    "TPM",
                    "TPM 2.0 is not available in the current hardware state",
                    "Windows 11 requires TPM 2.0. CorePilot did not detect an enabled TPM 2.0 device.",
                    "Enable Intel PTT / AMD fTPM / TPM 2.0 in firmware if this computer supports it, then run Verify again."),
                null => new(
                    CompatibilityState.Blocked,
                    "TPM",
                    "TPM 2.0 could not be confirmed",
                    "CorePilot could not read the TPM 2.0 state reliably, so it will not claim that Windows 11 is ready for this computer.",
                    "Check that TPM 2.0 is enabled in firmware and run Verify again.")
            });
        }
        else
        {
            findings.Add(new(
                CompatibilityState.Supported,
                "Firmware",
                "Windows 10 media path available",
                hardware.FirmwareMode.Equals("UEFI", StringComparison.OrdinalIgnoreCase)
                    ? "UEFI detected."
                    : "Windows 10 can be installed on a wider range of firmware configurations; UEFI is preferred where available."));
        }

        findings.Add(new(
            string.IsNullOrWhiteSpace(hardware.Cpu)
                ? CompatibilityState.Unknown
                : target.Id == "windows-11"
                    ? CompatibilityState.Warning
                    : CompatibilityState.Supported,
            "CPU",
            string.IsNullOrWhiteSpace(hardware.Cpu)
                ? "CPU could not be identified"
                : target.Id == "windows-11"
                    ? "CPU detected · exact Microsoft support-list match not asserted"
                    : "CPU detected",
            string.IsNullOrWhiteSpace(hardware.Cpu)
                ? "CorePilot could not read the processor model."
                : target.Id == "windows-11"
                    ? hardware.Cpu + " · CorePilot verifies the local processor identity and core platform checks, but does not currently claim an exact match against Microsoft's model-by-model Windows 11 CPU list."
                    : hardware.Cpu,
            string.IsNullOrWhiteSpace(hardware.Cpu) || target.Id != "windows-11"
                ? null
                : "Windows Setup remains the final authority for the exact processor model requirement."));

        if (target.Id == "windows-11")
        {
            findings.Add(new(
                CompatibilityState.Warning,
                "Graphics",
                "DirectX 12 / WDDM 2.0 compatibility is not fully verified",
                hardware.DevicesByCategory("GPU").Count == 0
                    ? "CorePilot did not detect a physical display adapter in the lightweight hardware report."
                    : "Detected GPU: " + string.Join(", ", hardware.DevicesByCategory("GPU").Select(x => x.Name).Take(3)) + ". CorePilot does not yet read the installed WDDM driver-model level.",
                "Confirm DirectX 12 compatibility and a WDDM 2.0-or-later driver on the target PC."));

            findings.Add(new(
                CompatibilityState.Warning,
                "Storage",
                "Windows 11 target storage capacity is not asserted by media preparation",
                "Microsoft requires a 64 GB or larger storage device. CorePilot prepares the USB installer but does not assume which internal disk/partition will receive Windows.",
                "Confirm that the actual destination drive has at least 64 GB available capacity before installation."));

            findings.Add(new(
                CompatibilityState.Warning,
                "Display / setup",
                "Display and initial-setup requirements are not fully verified",
                "CorePilot does not currently measure target display size/resolution. Windows 11 Home and Windows 11 Pro for personal use also require internet connectivity and a Microsoft account during initial setup.",
                "Confirm a 720p-or-better display larger than 9 inches and the applicable setup connectivity/account requirements."));
        }


        return new(
            target.Id,
            findings,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
    }

    private static CompatibilityReport AnalyzeLinux(
        HardwareReport hardware,
        SystemVariant target)
    {
        var findings = new List<CompatibilityFinding>();

        var recommendedMemory = target.Id switch
        {
            "ubuntu" => 4L * GiB,
            "fedora" => 4L * GiB,
            "linux-mint" => 2L * GiB,
            "debian" => 2L * GiB,
            _ => 2L * GiB
        };

        findings.Add(new(
            hardware.MemoryBytes >= recommendedMemory
                ? CompatibilityState.Supported
                : CompatibilityState.Warning,
            "Memory",
            hardware.MemoryBytes >= recommendedMemory
                ? "Memory is sufficient for the selected Linux desktop"
                : "Memory is below CorePilot's recommended desktop baseline",
            $"{hardware.MemoryBytes / (double)GiB:0.#} GB detected; CorePilot uses {recommendedMemory / GiB} GB as the recommended baseline for this target."));

        findings.Add(new(
            string.IsNullOrWhiteSpace(hardware.Cpu)
                ? CompatibilityState.Unknown
                : CompatibilityState.Supported,
            "CPU",
            string.IsNullOrWhiteSpace(hardware.Cpu)
                ? "CPU could not be identified"
                : "CPU detected",
            string.IsNullOrWhiteSpace(hardware.Cpu)
                ? "CorePilot could not read the processor model."
                : hardware.Cpu));

        findings.Add(new(
            CompatibilityState.Supported,
            "Firmware",
            hardware.FirmwareMode.Equals("UEFI", StringComparison.OrdinalIgnoreCase)
                ? "UEFI detected"
                : "Linux boot media remains available",
            hardware.FirmwareMode.Equals("UEFI", StringComparison.OrdinalIgnoreCase)
                ? "The selected distribution can use its normal UEFI installer path."
                : "CorePilot will not reject Linux solely because the current Windows session is not confirmed as UEFI."));

        if (hardware.DevicesByCategory("Network").Count == 0)
        {
            findings.Add(new(
                CompatibilityState.Warning,
                "Network",
                "No active physical network adapter detected",
                "The lightweight scan did not find an enabled physical adapter. Offline installation may still be possible."));
        }
        else
        {
            findings.Add(new(
                CompatibilityState.Supported,
                "Network",
                "Physical network adapter detected",
                string.Join(", ", hardware.DevicesByCategory("Network").Select(x => x.Name).Take(3))));
        }

        return new(
            target.Id,
            findings,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}
