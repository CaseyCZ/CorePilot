# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.13 ✅

The non-destructive macOS pipeline covers hardware discovery, compatibility, OpenCore EFI generation and validation, verified Apple Recovery, SHA-256 installer manifest, USB safety inspection, manifest-bound dry-run planning, atomic preflight, exact typed confirmation and a logging-only simulated write backend.

### v0.14 — Central execution state machine 🚧

CorePilot now has one authoritative workflow phase instead of relying only on nullable UI fields.

Phases:

`IDLE → HARDWARE SCANNED → DEEP SCANNED → COMPATIBILITY READY → WORKSPACE STAGED → EFI VALIDATED → RECOVERY VERIFIED → MANIFEST VERIFIED → USB INSPECTED → DRY-RUN PLANNED → PREFLIGHT READY → CONFIRMED → SIMULATED`

Safety behavior:

- every invalidation increments a workflow generation counter
- a new local hardware scan revokes Deep Scan and every downstream artifact
- a new Deep Scan revokes every previously generated downstream artifact
- changing macOS/system compatibility inputs revokes workspace/EFI/recovery/manifest and USB authorization
- changing the selected USB revokes every target-specific state after the verified installer manifest
- refreshing the disk list revokes target-specific state
- re-inspecting a USB creates a new `USB INSPECTED` state
- preflight/confirmation states carry the original expiration timestamp
- expired execution authorization automatically falls back to `DRY-RUN PLANNED`
- expired preflight/confirmation objects are cleared before confirmation or simulation
- simulation can start only from `CONFIRMED`
- successful simulation clears the short-lived execution authorization
- the current phase, generation and invalidation reason are visible in the UI

Physical-disk writes remain impossible: the only disk backend is still the logging-only simulation backend with `CanWritePhysicalDisks=false`.

## Next

1. Add deterministic state-machine smoke tests in CI for invalidation and expiry transitions.
2. Bind button enabled/disabled state to the workflow phase instead of only showing runtime messages.
3. Add a dedicated FAT32 strategy abstraction for larger USB media.
4. Persist a compact support bundle containing workflow snapshot, manifests and simulation transcript.
5. Only after the complete safety layer is covered by tests, evaluate a real writer behind a default-off feature gate.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge, enforces the logging-backend safety guard and publishes a self-contained `CorePilot-win-x64` test artifact.
