# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1 — Foundation ✅
Windows app, hardware inventory, USB discovery and modular OS architecture.

### v0.2 — Compatibility engine ✅
Hardware findings plus kext, patch and boot-argument planning.

### v0.3 — Hardware Sniffer ✅
Deep Scan with exact Report.json + ACPI import.

### v0.4 — Policy + staging ✅
Deterministic automation profile, pinned OpCore Simplify source and workspace manifest.

### v0.5 — EFI builder ✅
Non-interactive upstream integration and mandatory `ocvalidate`.

### v0.6 — EFI structure audit ✅
Independent verification that enabled ACPI, kext, driver and tool references physically exist.

### v0.7 — Apple Recovery ✅
Official OpenCorePkg `macrecovery.py`, signed chunk verification and local SHA-256 hashes.

### v0.8 — Final installer manifest ✅
A deterministic SHA-256 manifest covers the validated EFI tree and verified Apple Recovery payload.

### v0.9 — USB target safety inspector 🚧

The first USB-media phase is deliberately **read-only**.

CorePilot now inventories each physical USB target with:

- Windows physical disk number and device path
- model, serial number and total size
- interface/bus type and media type
- Windows removable-media flag
- all mounted logical volumes, filesystem, size and label
- detection of the currently running Windows system volume
- safety classification: **SAFE CANDIDATE / WARNING / BLOCKED**

A disk containing the running Windows system volume is always **BLOCKED**, even if Windows reports it through a USB interface.

A target with incomplete identity is **WARNING** and is also rejected by the current planning flow.

**SAFE CANDIDATE only means the read-only identity checks passed. It does not enable disk writing.**

## Safety model

1. Scan
2. Deep Scan
3. Plan
4. Stage
5. Build EFI
6. Validate EFI
7. Download + verify Apple Recovery
8. Create + verify final installer manifest
9. Inspect physical USB target
10. Dry-run partition/write plan — next
11. **Destructive USB write — still disabled**

No current step writes to a physical disk.

## Next

1. Define the exact GPT layout for macOS recovery media.
2. Generate a dry-run write plan bound to disk number + model + serial + size.
3. Re-scan the target immediately before execution and reject any identity change.
4. Re-verify the installer manifest immediately before execution.
5. Only then add destructive writes behind explicit typed confirmation.
6. Continue Windows/Linux media engines and multiboot later.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
