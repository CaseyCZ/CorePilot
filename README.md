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

### v0.5 — Non-interactive EFI builder 🚧

CorePilot now has a real EFI build path:

- embedded Python bridge extracted only into the isolated build workspace
- directly imports the pinned OpCore Simplify internals
- uses upstream compatibility checking and hardware customization
- uses upstream ACPI selection, SMBIOS handling, kext selection and OpenCore file gathering
- policy answers are supplied only for known prompts
- **any unknown/new upstream prompt fails closed**
- Tahoe audio does not silently enable OCLP/root patches
- configurations requiring OCLP stop until explicit Advanced approval exists
- downloads required OpenCore/kext components through the upstream gatherer
- runs the upstream OpenCore EFI build
- requires `EFI/BOOT/BOOTx64.efi`, `EFI/OC/OpenCore.efi` and `config.plist`
- locates and runs upstream `ocvalidate.exe`
- the EFI is considered successful only if `ocvalidate` returns success
- writes `CorePilotEfiBuild.json` and a build log into the workspace

The Windows UI now has a **Build EFI** action after Plan/Stage.

## Safety model

1. **Scan** — read-only local hardware inventory.
2. **Deep Scan** — explicit Hardware Sniffer download/run.
3. **Plan** — compatibility + automatic choices.
4. **Stage** — pinned upstream source + isolated workspace.
5. **Build EFI** — generates files only inside the workspace.
6. **Validate EFI** — mandatory `ocvalidate`.
7. **Write USB** — **still disabled**.

No build step writes to a physical disk.

## Next

1. Add structural EFI validation beyond `ocvalidate` (required files, drivers, kext dependencies, config snapshot audit).
2. Add Apple Recovery download using OpenCore/macRecovery.
3. Add a final installer manifest with hashes.
4. Only after those pass, implement guarded USB partition/write operations.
5. Then add Windows/Linux media engines and multiboot.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
