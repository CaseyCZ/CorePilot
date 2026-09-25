# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.14 ✅

The non-destructive macOS pipeline covers hardware discovery, compatibility, OpenCore EFI generation and validation, verified Apple Recovery, installer-manifest hashing, USB safety inspection, dry-run planning, short-lived preflight, exact typed confirmation, logging-only write simulation and a generation-based execution state machine.

### v0.15 — Live Activity / Error Log 🚧

CorePilot now includes a persistent live activity log designed specifically for diagnosing test failures on different PCs.

The main window shows:

- **RUNNING / READY** state
- current subsystem/operation
- live current status text
- current workflow phase/generation
- accumulated error count
- an **Open log** button

The Activity Log window shows every entry with:

- timestamp including milliseconds
- level: `INFO / SUCCESS / WARNING / ERROR`
- subsystem/area
- human-readable status message
- full selected exception detail and stack trace

Major operations report live progress:

- local hardware scan
- Hardware Sniffer Deep Scan
- physical disk refresh
- USB safety inspection
- OpenCore workspace staging
- EFI build and validation
- Apple Recovery + installer manifest
- USB dry-run planning
- execution preflight
- typed confirmation
- logging-only write simulation
- workflow phase/invalidation changes

Every session is also persisted automatically under:

`%LocalAppData%\CorePilot\logs\CorePilot-YYYYMMDD-HHmmss.log`

Clearing the Activity Log window clears only the visible list; the persistent session log remains on disk.

CorePilot also records:

- unhandled WPF/UI exceptions
- unhandled application-domain exceptions
- unobserved background task exceptions
- full `Exception.ToString()` output including stack traces

This makes a failed test report usable even when the application closes unexpectedly.

Physical-disk writes remain disabled.

## Next

1. Add state-driven enabled/disabled buttons so only valid next actions can be clicked.
2. Add log filters for errors/warnings and search.
3. Add a one-click support bundle containing the session log, workflow snapshot, hardware report and non-sensitive manifests.
4. Keep improving simulation coverage before considering a real physical-disk writer.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge, logging-backend safety guard and workflow state-machine smoke tests, then publishes a self-contained `CorePilot-win-x64` test artifact.
