# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## Current development

### v0.1 — Foundation ✅
Windows .NET 8/WPF app, local hardware inventory, USB discovery and modular OS architecture.

### v0.2 — macOS compatibility engine ✅
Hardware findings plus kext, kernel patch and boot-argument planning.

### v0.3 — Hardware Sniffer integration ✅
Explicit Deep Scan, exact PCI/USB identities, Report.json + ACPI export and import.

### v0.4 — macOS policy + staging ✅
Deterministic automation profile, pinned OpCore Simplify source, SHA-256 workspace manifest and Python detection.

### v0.5 — Non-interactive EFI builder ✅
Pinned upstream internals, fail-closed automation bridge and mandatory `ocvalidate`.

### v0.6 — EFI structure audit ✅
CorePilot independently checks enabled ACPI, kext, driver and tool references from `config.plist`.

### v0.7 — Apple Recovery 🚧

CorePilot now uses the **macrecovery.py shipped with the same OpenCorePkg gathered for the EFI build**.

Supported target profiles currently use the official examples from OpenCorePkg `Utilities/macrecovery/recovery_urls.txt`:

- Ventura 13
- Sonoma 14
- Sequoia 15
- Tahoe 26

Recovery flow:

1. EFI must pass both `ocvalidate` and the CorePilot structure audit.
2. CorePilot launches the local OpenCorePkg `macrecovery.py` with the official board-ID/MLB profile for the selected target.
3. The payload is written only to the isolated workspace as `com.apple.recovery.boot/BaseSystem.dmg` and `BaseSystem.chunklist`.
4. `macrecovery.py` verifies the signed chunklist and every image chunk.
5. CorePilot computes its own SHA-256 for both files.
6. The result is saved as `CorePilotRecovery.json`.

The UI exposes this as **Download Recovery** after **Build EFI**.

## Safety model

1. **Scan** — read-only local hardware inventory.
2. **Deep Scan** — explicit Hardware Sniffer download/run.
3. **Plan** — compatibility + automatic choices.
4. **Stage** — pinned upstream source + isolated workspace.
5. **Build EFI** — workspace only.
6. **Validate EFI** — `ocvalidate` + CorePilot structure audit.
7. **Download Recovery** — Apple payload into the workspace + signed chunk verification + SHA-256.
8. **Write USB** — **still disabled**.

No current CorePilot step writes to a physical disk.

## Next

1. Create a final installer manifest containing EFI tree hashes + Recovery hashes.
2. Re-verify that manifest immediately before any removable-disk action.
3. Add guarded GPT/EFI/recovery USB creation with strong target-disk confirmation.
4. Then add Windows/Linux media engines and multiboot.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
