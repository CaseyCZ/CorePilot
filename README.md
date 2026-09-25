# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

The target workflow is intentionally simple:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## Current development

### v0.1 — Foundation ✅
Windows .NET 8/WPF app, local hardware inventory, USB discovery and modular OS architecture.

### v0.2 — macOS compatibility engine ✅
Hardware compatibility findings plus kext, kernel patch and boot-argument planning.

### v0.3 — Hardware Sniffer integration ✅
Explicit Deep Scan, exact PCI/USB identities, Report.json + ACPI export and report import.

### v0.4 — macOS automation + EFI staging 🚧

CorePilot now prepares deterministic build inputs instead of driving upstream text menus:

- automatic SMBIOS, GPU, Wi-Fi, audio and USB bootstrap policy
- persisted `CorePilotAutomationProfile.json`
- pinned OpCore Simplify source revision for reproducible builds
- downloaded source archive SHA-256 recorded in the workspace
- upstream source structure validated before use
- Deep Scan `Report.json + ACPI` copied into an isolated workspace
- Python runtime detection without silently installing anything
- `CorePilotWorkspace.json` manifest records all inputs needed by the future EFI builder

The staging step **does not execute OpCore Simplify and does not write to a disk**.

## Safety model

CorePilot separates the pipeline into explicit phases:

1. **Scan** — read-only local hardware inventory.
2. **Deep Scan** — explicitly download/run Hardware Sniffer and import exact hardware data.
3. **Plan** — resolve compatibility and automatic build choices.
4. **Stage** — download pinned OpenCore tooling and prepare an isolated workspace.
5. **Build EFI** — next milestone; no disk access.
6. **Validate EFI** — must pass before media creation.
7. **Write USB** — remains disabled until all prior stages are validated.

## Architecture

- **CorePilot.App** — Windows UI
- **CorePilot.Core** — shared models and module contracts
- **CorePilot.Hardware** — local scanner, disks, Hardware Sniffer integration and report parser
- **CorePilot.MacOS** — compatibility, automation policy, staging and future EFI generation
- **CorePilot.Windows** — Windows media workflow
- **CorePilot.Linux** — Linux media workflow

## Next

1. Add a non-interactive Python bridge that consumes `CorePilotAutomationProfile.json`.
2. Reuse OpCore Simplify's compatibility, ACPI, SMBIOS, kext and config modules without text-menu input.
3. Produce a staged EFI folder.
4. Run structural/config validation.
5. Download Apple recovery.
6. Only then enable guarded USB writes.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions publishes a self-contained `CorePilot-win-x64` test artifact.
