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

### v0.8 — Final installer manifest 🚧

After Recovery succeeds, CorePilot now automatically creates:

- `CorePilotInstallerManifest.json`
- `CorePilotInstallerManifest.sha256`

The manifest contains:

- selected macOS target and Darwin version
- actual SMBIOS used by the EFI builder
- pinned OpCore Simplify commit + source archive SHA-256
- `ocvalidate` and CorePilot structural-audit status
- every file below the generated EFI tree
- path, role, size and SHA-256 for every EFI file
- verified `BaseSystem.dmg` and `BaseSystem.chunklist` hashes

CorePilot immediately re-reads the manifest and re-hashes all referenced files. Workspace path traversal and reparse-point files are rejected.

This gives the future USB writer a deterministic pre-write contract: **the bytes written to USB must match the already-validated installer manifest.**

## Safety model

1. Scan
2. Deep Scan
3. Plan
4. Stage
5. Build EFI
6. Validate EFI
7. Download + verify Apple Recovery
8. Create + verify final installer manifest
9. **Write USB — still disabled**

No current step writes to a physical disk.

## Next

1. Build a read-only USB target safety inspector: removable status, system-disk detection, model/serial/size and mounted volumes.
2. Design the exact GPT/EFI/recovery layout.
3. Add a dry-run USB write plan that must match the installer manifest.
4. Only after those safeguards pass, enable the destructive write behind explicit confirmation.
5. Then continue Windows/Linux media engines and multiboot.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
