# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.11 ✅

CorePilot now covers the complete non-destructive preparation chain:

- hardware + Deep Scan
- deterministic macOS compatibility policy
- non-interactive OpenCore EFI generation
- `ocvalidate` + independent EFI structure audit
- verified Apple Recovery
- SHA-256 installer manifest
- fail-closed USB safety inspection
- stable USB target fingerprint
- manifest-bound USB dry-run plan
- short-lived atomic execution preflight

### v0.12 — Exact typed confirmation 🚧

CorePilot now accepts a destructive confirmation phrase only when a fresh execution preflight is still valid.

Example shape:

`ERASE DISK <physical-number> <fingerprint-prefix>`

Rules:

- comparison is exact and case-sensitive
- the USB is re-inspected again immediately before confirmation
- the complete execution preflight is re-verified
- installer manifest and dry-run plan must still verify
- confirmation expiry is exactly the original preflight expiry
- confirmation **never extends** the two-minute preflight lifetime
- a changed/blocked USB invalidates confirmation
- the accepted confirmation is stored with SHA-256 in the workspace
- `PhysicalDiskWritesEnabled=false` remains hard-coded

Generated files:

- `CorePilotUsbTypedConfirmation.json`
- `CorePilotUsbTypedConfirmation.sha256`

This version still has **no implementation capable of formatting or writing a physical disk**.

## Safety model

1. Scan / Deep Scan
2. Plan / Stage
3. Build + validate EFI
4. Verify Apple Recovery
5. Build + verify installer manifest
6. Inspect USB target
7. Build + verify dry-run write plan
8. Short-lived atomic execution preflight
9. Exact typed confirmation
10. Logging-only disk backend — next
11. **Real physical USB writer — disabled**

## Next

1. Introduce a common disk-operation interface.
2. Implement only a logging/dry-run backend first.
3. Feed the already-verified write-plan actions through that backend and generate an execution transcript.
4. Require valid typed confirmation even for the simulated destructive sequence.
5. Add tests that prove the simulated backend cannot touch a physical disk.
6. Only then evaluate a real GPT/FAT32 backend behind the same gates.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge and publishes a self-contained `CorePilot-win-x64` test artifact.
