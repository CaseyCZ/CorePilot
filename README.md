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

- deterministic bridge into pinned OpCore Simplify internals
- strict fail-closed handling for upstream prompts
- upstream compatibility, ACPI, SMBIOS, kext and config generation
- workspace-only EFI generation
- mandatory upstream `ocvalidate`

### v0.6 — EFI structure audit 🚧

After `ocvalidate`, CorePilot now independently parses the generated XML `config.plist` and verifies every enabled file reference:

- required `EFI/BOOT/BOOTx64.efi`
- required `EFI/OC/OpenCore.efi`
- `ACPI -> Add -> Path`
- `Kernel -> Add -> BundlePath`
- kext `PlistPath` and `ExecutablePath`
- `UEFI -> Drivers -> Path`
- `Misc -> Tools -> Path`
- duplicate enabled references are surfaced as warnings
- referenced paths are constrained to their expected EFI directory to reject traversal such as `../`

The structural result is appended to `CorePilotEfiBuild.json`. EFI is accepted only when both `ocvalidate` and the CorePilot structure audit succeed.

## Safety model

1. **Scan** — read-only local hardware inventory.
2. **Deep Scan** — explicit Hardware Sniffer download/run.
3. **Plan** — compatibility + automatic choices.
4. **Stage** — pinned upstream source + isolated workspace.
5. **Build EFI** — generates files only inside the workspace.
6. **Validate EFI** — upstream `ocvalidate` + independent CorePilot structure audit.
7. **Write USB** — **still disabled**.

No build step writes to a physical disk.

## Next

1. Add Apple Recovery download using OpenCore `macrecovery.py`.
2. Hash the completed EFI + recovery payload into a final installer manifest.
3. Add recovery/EFI integrity re-check before any removable disk operation.
4. Only then implement guarded USB partition/write operations.
5. Then add Windows/Linux media engines and multiboot.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
