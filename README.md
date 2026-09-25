# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

The target workflow is intentionally simple:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## Current development

### v0.1 — Foundation ✅

- Windows 11 desktop GUI built with .NET 8 / WPF
- hardware inventory
- USB disk discovery
- modular macOS, Windows and Linux architecture
- GitHub Actions build validation

### v0.2 — macOS compatibility engine 🚧

The current v0.2 branch adds:

- PCI/PNP identifiers to hardware inventory
- laptop/desktop detection
- macOS compatibility findings: **OK / ACTION / WARNING / BLOCKED / UNKNOWN**
- firmware UEFI + Secure Boot checks
- AMD Ryzen / AMD Vanilla planning with physical core count
- Zen 4 IOPCIFamily planning
- NVIDIA RTX/GTX 16 blocker detection
- Intel Xe/Arc detection
- AMD Navi/Zen 4 APU graphics classification
- initial supported/spoofed AMD dGPU candidates
- Intel Wi-Fi action detection
- generated plan for kexts, kernel patches and boot arguments

CorePilot intentionally treats uncertain hardware as **UNKNOWN** instead of pretending it is compatible.

> **Safety:** disk formatting and installer writing are still disabled. Native macOS media will not be prepared while a blocking compatibility finding exists.

## Architecture

- **CorePilot.App** — Windows UI
- **CorePilot.Core** — shared models and module contracts
- **CorePilot.Hardware** — hardware and disk discovery
- **CorePilot.MacOS** — OpenCore, compatibility, ACPI/kext and Apple recovery workflow
- **CorePilot.Windows** — Windows media workflow
- **CorePilot.Linux** — Linux media workflow

## Next

**v0.3 — macOS media builder:** integrate OpenCore/OpCore-Simplify data, download Apple recovery, resolve kext versions, generate/validate EFI and add guarded USB creation.

Later: Windows/Linux media creation, multiboot, recovery tools, update engine and optional offline packs.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```
