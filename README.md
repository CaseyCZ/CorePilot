# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.8 ✅

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

### v0.9 — Read-only USB safety inspector 🚧

Before CorePilot gains any destructive disk operation, the selected USB target is now inspected read-only:

- resolves the exact `Win32_DiskDrive`
- reads physical disk index, model, serial number, size, interface/media type and PNP identity
- enumerates partitions and mounted logical volumes
- checks Windows `BootPartition`, `BootVolume`, `SystemVolume`
- checks the current Windows system drive
- checks pagefile placement
- creates a stable SHA-256 target identity fingerprint
- **BLOCKED** for any disk carrying system/boot/pagefile content
- **BLOCKED** if disk topology cannot be resolved completely
- **SAFE CANDIDATE** for a removable USB with stable serial identity
- **STRONG CONFIRMATION** for USB fixed media (such as an external SSD/HDD) or removable media without a stable serial

This feature performs **no disk writes, formatting, partitioning, dismounting or volume changes**.

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
10. **Write USB — still disabled**

## Next

1. Create a dry-run macOS USB layout plan tied to both the installer-manifest SHA and USB identity fingerprint.
2. Re-inspect the target immediately before execution and reject any identity change.
3. Add explicit destructive confirmation containing disk number, model, serial/fingerprint and size.
4. Only then implement the actual GPT/EFI/recovery write engine.
5. Continue Windows/Linux media engines after the common disk-safety layer is stable.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
