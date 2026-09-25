# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.9 ✅

CorePilot currently provides:

- Windows hardware inventory + Hardware Sniffer Deep Scan
- macOS compatibility and deterministic automation policies
- pinned OpCore Simplify staging
- non-interactive EFI generation
- upstream `ocvalidate`
- independent EFI structure audit
- verified Apple Recovery download
- final installer manifest with SHA-256 for every EFI/recovery file
- immediate manifest re-verification
- fail-closed USB target safety inspection
- boot/system/pagefile protection checks
- stable SHA-256 USB target identity fingerprint

### v0.10 — Manifest-bound USB dry-run plan 🚧

CorePilot can now create the exact future macOS USB operation plan **without executing any physical disk command**.

The dry-run plan is bound to:

- exact physical disk number and Windows device path
- target model, serial, size and SHA-256 identity fingerprint
- current USB safety classification
- exact `CorePilotInstallerManifest.json` SHA-256
- the already-verified EFI + Apple Recovery payload

The planned recovery USB layout follows the current OpenCore/Dortania Windows workflow:

- GPT partition table
- one primary FAT32 installer partition
- volume label `EFI`
- `EFI/` at the partition root
- `com.apple.recovery.boot/` at the partition root
- verified recovery `.dmg` and `.chunklist` inside `com.apple.recovery.boot/`

For USB media larger than the practical Windows built-in FAT32 formatting limit, CorePilot plans a smaller FAT32 installer partition instead of pretending Windows can format the entire large device as FAT32.

The generated workspace files are:

- `CorePilotUsbWritePlan.json`
- `CorePilotUsbWritePlan.sha256`

The plan includes future destructive actions as **descriptions/command previews only**. It is marked `DryRunOnly=true`; CorePilot does not execute diskpart, clean, format, dismount or raw-disk writes.

Before creating the plan, CorePilot re-inspects the selected USB and rejects the operation if its identity fingerprint changed.

## Safety model

1. Scan
2. Deep Scan
3. Plan
4. Stage
5. Build EFI
6. Validate EFI
7. Download + verify Apple Recovery
8. Create + verify installer manifest
9. Inspect exact USB target read-only
10. Create + verify manifest-bound USB dry-run plan
11. Re-inspect target immediately before any future execution
12. **Destructive USB write — still disabled**

## Next

1. Add an execution preflight object that revalidates target fingerprint + manifest + dry-run plan hash in one atomic gate.
2. Add explicit typed destructive confirmation containing disk number and fingerprint.
3. Implement a disk-operation abstraction with a permanent dry-run backend first.
4. Only after those tests pass, add the real GPT/FAT32 writer.
5. Continue Windows/Linux media engines after the common disk-safety layer is stable.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
