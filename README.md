# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## macOS pipeline status

### v0.1–v0.15 ✅

The current non-destructive pipeline includes hardware discovery, compatibility, OpenCore EFI generation and validation, Apple Recovery verification, installer-manifest hashing, USB safety inspection, dry-run planning, execution preflight, exact typed confirmation, logging-only write simulation, a central state machine and persistent Activity/Error logging.

### v0.16 — Searchable / filterable Activity Log 🚧

The Activity Log now supports fast diagnosis during long test runs.

New controls:

- level filter: **All / Errors / Warnings / Success / Info**
- live text search
- search across timestamp, level, subsystem, message and full exception/stack-trace detail
- visible result count in the form `shown / total`
- **Clear filters** without deleting log data
- auto-scroll follows the newest entry that matches the current filter
- persistent session log remains unchanged on disk

The main CorePilot window also shows an indeterminate progress bar while `ActivityLog.IsBusy=true`, so it is visually obvious that a long-running operation is still active.

The persistent log remains stored under:

`%LocalAppData%\CorePilot\logs\CorePilot-YYYYMMDD-HHmmss.log`

Physical-disk writes remain disabled.

## Next

1. Bind action buttons to workflow state so invalid steps are disabled in advance.
2. Add a one-click support bundle containing the session log, workflow snapshot, hardware report and non-sensitive manifests.
3. Add optional automatic opening/focus of the log window on ERROR.
4. Continue improving simulation coverage before considering a real physical-disk writer.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge, logging-backend safety guard and workflow state-machine smoke tests, then publishes a self-contained `CorePilot-win-x64` test artifact.
