# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

The target workflow is intentionally simple:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## Current development

### v0.1 — Foundation ✅

- .NET 8 / WPF Windows desktop app
- local hardware inventory
- USB disk discovery
- modular macOS, Windows and Linux architecture
- GitHub Actions validation

### v0.2 — macOS compatibility engine ✅

- PCI/PNP identifiers
- laptop/desktop detection
- **OK / ACTION / WARNING / BLOCKED / UNKNOWN** findings
- firmware, CPU, GPU and initial Wi-Fi checks
- generated kext / kernel patch / boot-argument plan

### v0.3 — Hardware Sniffer integration 🚧

- self-contained `win-x64` CI artifacts
- explicit Hardware Sniffer Deep Scan
- latest official `Hardware-Sniffer-CLI.exe` release discovery
- per-version tool cache and SHA-256 fingerprint
- automatic `Report.json + ACPI` export
- direct parsing of the upstream report schema
- deep hardware IDs for GPU, network, audio, USB, storage, Bluetooth and input devices
- compatibility is recalculated immediately after Deep Scan

Deep Scan is explicit: CorePilot does not silently download or execute third-party tools at startup.

> **Safety:** disk formatting and installer writing are still disabled. Native macOS media will not be prepared while a blocking compatibility finding exists.

## Architecture

- **CorePilot.App** — Windows UI
- **CorePilot.Core** — shared models and module contracts
- **CorePilot.Hardware** — local scanner, disks, Hardware Sniffer integration and report parser
- **CorePilot.MacOS** — OpenCore compatibility, ACPI/kext and Apple recovery workflow
- **CorePilot.Windows** — Windows media workflow
- **CorePilot.Linux** — Linux media workflow

## Next

1. Feed exact Hardware Sniffer device IDs into the macOS rules database.
2. Add an OpCore Simplify bridge for non-interactive EFI generation.
3. Download Apple recovery for the chosen macOS release.
4. Validate generated EFI before allowing guarded USB writes.
5. Add Windows/Linux media engines and multiboot later.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions also publishes a self-contained `CorePilot-win-x64` test artifact.
