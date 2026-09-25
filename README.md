# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

The target workflow is intentionally simple:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## v0.1 Foundation

- Windows 11 desktop GUI built with .NET 8 / WPF
- hardware inventory for CPU, GPU, motherboard, firmware, Secure Boot, memory, network and audio
- USB / physical disk discovery
- modular operating-system architecture
- initial macOS, Windows and Linux modules
- GitHub Actions build validation

> **Safety:** v0.1 does not write to or format disks yet. Destructive USB operations will only be enabled after target verification, explicit confirmation and automated validation are in place.

## Architecture

- **CorePilot.App** — Windows UI
- **CorePilot.Core** — shared models and module contracts
- **CorePilot.Hardware** — hardware and disk discovery
- **CorePilot.MacOS** — OpenCore, compatibility, ACPI/kext and Apple recovery workflow
- **CorePilot.Windows** — Windows media workflow
- **CorePilot.Linux** — Linux media workflow

## Roadmap

**v0.1 — Foundation:** hardware scan, system selection, USB discovery and modular architecture.

**v0.2 — macOS compatibility engine:** hardware IDs, blocker detection, OpenCore plan and kext/ACPI dependency resolution.

**v0.3 — macOS media builder:** OpenCore + Apple recovery download, EFI generation, validation and safe USB creation.

Later: Windows/Linux media creation, multiboot, recovery tools, update engine and optional offline packs.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```
