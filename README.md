# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.10 ✅

CorePilot currently provides:

- Windows hardware inventory + Hardware Sniffer Deep Scan
- macOS compatibility and deterministic automation policies
- pinned OpCore Simplify staging
- non-interactive EFI generation
- upstream `ocvalidate`
- independent EFI structure audit
- verified Apple Recovery download
- SHA-256 installer manifest for every EFI/recovery file
- fail-closed USB target inspection including boot/system/pagefile protection
- stable USB target identity fingerprint
- manifest-bound, SHA-256 protected USB dry-run plan

### v0.11 — Atomic execution preflight 🚧

Immediately before any future destructive confirmation, CorePilot now creates a short-lived execution preflight gate.

The gate revalidates together:

- the currently attached physical USB target
- physical disk number and device path
- exact USB identity fingerprint
- current USB safety level
- complete installer manifest and every referenced file
- installer-manifest SHA-256
- complete dry-run plan
- dry-run plan SHA-256
- required future typed confirmation phrase

The generated files are:

- `CorePilotUsbExecutionPreflight.json`
- `CorePilotUsbExecutionPreflight.sha256`

Important properties:

- preflight lifetime: **2 minutes**
- `ReadyForConfirmationOnly=true`
- `PhysicalDiskWritesEnabled=false`
- any changed target identity, plan hash, manifest hash or blocked safety state invalidates the gate
- the UI re-inspects the USB again when running preflight

This stage still contains **no physical-disk writer**.

## Safety model

1. Scan
2. Deep Scan
3. Plan
4. Stage
5. Build + validate EFI
6. Download + verify Apple Recovery
7. Create + verify installer manifest
8. Inspect USB target
9. Create + verify USB dry-run plan
10. Run short-lived atomic execution preflight
11. Typed destructive confirmation — next
12. **Physical USB writer — still disabled**

## Next

1. Add a confirmation gate that accepts only the exact phrase stored in the valid, unexpired preflight.
2. Confirmation must never extend preflight expiry and must be invalidated by any target refresh/change.
3. Add a disk-operation abstraction whose only implementation is a logging/dry-run backend.
4. Exercise the full workflow without a single destructive API.
5. Only after that add a real GPT/FAT32 backend behind the same gates.
6. Continue Windows/Linux media engines once the common safety layer is stable.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
