# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose a USB drive → CorePilot handles the rest.**

## Download released Windows build

GitHub Releases contain only versions that were explicitly promoted after testing.

Latest published release:

https://github.com/CaseyCZ/CorePilot/releases/latest

Normal pushes to `main` do **not** update Releases. They only run CI and publish the temporary `CorePilot-win-x64` Actions artifact.

When a version is agreed as ready, the manual **Release** workflow is run with an explicit version such as `v0.18`. It reruns the full safety/build pipeline and publishes:

- `CorePilot-v0.18-win-x64.zip`
- `CorePilot-v0.18-win-x64.sha256`

## macOS pipeline status

### v0.1–v0.16 ✅

The current non-destructive pipeline includes hardware discovery, compatibility, OpenCore EFI generation and validation, Apple Recovery verification, installer-manifest hashing, USB safety inspection, dry-run planning, execution preflight, exact typed confirmation, logging-only write simulation, a central state machine and persistent searchable Activity/Error logging.

### v0.17 — State-driven guided actions 🚧

CorePilot now decides whether each action is valid **before the user can click it**.

The action policy is a pure state-machine layer covered by the existing CI smoke test.

Examples:

- **Deep scan** requires a local hardware scan.
- **Prepare plan** requires macOS compatibility + Deep Scan + a selected USB + an automation profile cleared for automatic building.
- **Build EFI** is enabled only at `WORKSPACE STAGED`.
- **Download Recovery** is enabled only at `EFI VALIDATED`.
- **Check USB safety** becomes part of the guided flow after `MANIFEST VERIFIED`.
- **Dry-run USB plan** requires an inspected, non-blocked target.
- **Execution preflight** requires `DRY-RUN PLANNED`.
- **Confirm target** requires a live, unexpired `PREFLIGHT READY`.
- **Simulate write** requires exact `CONFIRMED` state and remains one-shot.
- while any logged operation is `RUNNING`, conflicting workflow actions and input selectors are disabled.

Disabled buttons expose the reason through tooltips even while disabled.

The main workflow card also displays a **Next · …** hint showing the expected next step.

### v0.17 state-machine fixes

While wiring the guided actions, two missing phase transitions from the earlier UI integration were found and corrected:

- successful EFI generation now explicitly advances to `EFI VALIDATED`
- successful Apple Recovery + installer-manifest verification now advances through `RECOVERY VERIFIED → MANIFEST VERIFIED`

CI now exercises the action policy together with the state-machine smoke test so these transitions have usable downstream actions.

### RUNNING-state correction

Safety-stop branches after a started operation now finish the Activity Log as `WARNING` instead of leaving the app visually stuck on `RUNNING`.

This covers:

- changed USB identity during dry-run
- blocked target during execution preflight
- blocked target during typed confirmation
- blocked target during write simulation

Physical-disk writes remain disabled.

## Support Bundle

The development build now has a **Support bundle** button next to **Open log**.

It creates:

`%LocalAppData%\CorePilot\support\CorePilot-Support-YYYYMMDD-HHmmss.zip`

The ZIP can include:

- current session log
- workflow phase/generation/expiry snapshot
- sanitized hardware summary
- compatibility findings
- automation profile
- sanitized USB safety result
- CorePilot-generated workspace/build/recovery/manifest/dry-run/preflight/confirmation/simulation metadata

CorePilot does not copy arbitrary user files or documents into the bundle.

Before packaging, it redacts known computer/user names and local profile paths, USB serial numbers, target identity fingerprints and destructive confirmation phrases.

## Next

1. Optionally auto-open/focus Activity Log on ERROR.
2. Add dedicated FAT32 strategy handling for larger USB media.
3. Continue simulation coverage before considering a real physical-disk writer.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge, logging-backend safety guard, state-machine transitions and workflow action policy, then publishes a self-contained `CorePilot-win-x64` test artifact.
