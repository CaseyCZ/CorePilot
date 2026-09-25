# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.12 ✅

The non-destructive pipeline now includes hardware discovery, macOS policy, EFI generation and validation, Apple Recovery verification, installer-manifest hashing, USB safety inspection, dry-run planning, two-minute atomic execution preflight and exact typed confirmation.

### v0.13 — Logging-only disk backend 🚧

CorePilot now passes the fully confirmed USB write plan through a common disk-operation backend, but the only backend that exists is:

`LoggingDiskOperationBackend`

Hard guarantees in this version:

- `CanWritePhysicalDisks=false`
- the simulator rejects any backend reporting write capability
- every planned destructive action is recorded as **SIMULATED ONLY**
- no command preview is executed
- no process is launched
- no physical-disk handle is opened
- no partition, format, mount or raw-disk API is used
- the full plan is still reverified before simulation
- typed confirmation is reverified and must remain inside the original preflight expiry
- the target USB is re-inspected again before simulation

Generated files:

- `CorePilotUsbSimulationTranscript.json`
- `CorePilotUsbSimulationTranscript.sha256`

The transcript records every would-be step, including which steps would be destructive in a future real backend.

GitHub Actions contains a safety guard that rejects forbidden physical-disk/process capability markers in the logging backend source.

## Safety model

1. Scan / Deep Scan
2. Build + validate EFI
3. Verify Recovery + installer manifest
4. Inspect USB
5. Build dry-run write plan
6. Atomic preflight
7. Exact typed confirmation
8. Execute entire plan through logging-only backend
9. Review SHA-256 simulation transcript
10. **Real physical USB backend — still absent**

## Next

1. Add deterministic simulator assertions: action count/order, all destructive actions remain simulated, hashes remain stable.
2. Add an execution state machine so stale confirmation/preflight objects cannot be reused after target refresh.
3. Add a dedicated FAT32 strategy abstraction for >32 GB USB media.
4. Only after simulation tests are exhaustive, design the real backend behind a feature flag that defaults permanently off.
5. Continue Windows/Linux media engines using the same common safety gates.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge, enforces the logging-backend safety guard and publishes a self-contained `CorePilot-win-x64` test artifact.
