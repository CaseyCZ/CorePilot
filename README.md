# CorePilot

**CorePilot** is a hardware-aware boot media creator for Windows.

Target workflow:

**Choose an operating system → choose This computer or Other computer → Verify → optionally choose a USB drive → Write to disk.**

CorePilot is not intended to stop at a compatibility verdict. **Verify is the preparation engine**: it scans the exact hardware, searches the trusted online knowledge/tool catalog for usable installation paths, resolves applicable drivers/kexts/patches, generates the hardware-specific configuration, downloads non-destructive installer payloads where supported, and validates the result. A detected incompatibility is treated as a remediation task first; it becomes a final blocker only when the currently implemented safe paths cannot resolve it.

The main UI intentionally exposes only the two workflow actions **Verify** and **Write to disk**. **Write to disk never invents or rebuilds a configuration at the destructive stage; it consumes the payload already prepared and validated by Verify**, then re-validates sources/manifest/USB identity before erasing the selected target.

## Download released Windows build

Latest continuously tested build:

https://github.com/CaseyCZ/CorePilot/releases/latest

A push/merge to `main` first runs the full **Build** workflow. Only after that workflow succeeds, the **Latest Release** workflow may promote its exact `CorePilot-win-x64` artifact. Promotion is fail-closed:

- the Build run must be a successful `push` on `main`
- the built commit must still be the current `main`
- the commit must be GitHub-verified
- an older completed run cannot overwrite a newer Latest build
- the ZIP is accompanied by a SHA-256 file

Versioned releases remain manual. When a version is agreed as ready, the **Release** workflow is run with an explicit version such as `v0.18`; it reruns the full safety/build pipeline and publishes:

- `CorePilot-v0.18-win-x64.zip`
- `CorePilot-v0.18-win-x64.sha256`

## macOS pipeline status

### v0.1–v0.16 ✅

The current non-destructive pipeline includes hardware discovery, compatibility, OpenCore EFI generation and validation, Apple Recovery verification, installer-manifest hashing, USB safety inspection, dry-run planning, execution preflight, exact typed confirmation, logging-only write simulation, a central state machine and persistent searchable Activity/Error logging.

### Current guarded writer ✅

The simple UI exposes only **Verify** and **Write to disk**.

For supported Hackintosh/OpenCore targets, **Verify** now performs the non-destructive preparation stages automatically:

- live online-source + preparation-knowledge refresh
- Hardware Sniffer Deep Scan
- hardware-specific remediation/source resolution
- fresh download of the current GitHub-verified OpCore-Simplify commit
- EFI generation, `ocvalidate` and structural validation
- verification of every cached OpenCore/kext file against OpCore-Simplify integrity manifests
- binding of OpenCorePkg and cataloged kext downloads to the current live CorePilot release catalog
- Apple Recovery download and verification
- installer manifest + SHA-256 verification

**Write to disk** then consumes that prepared payload and performs only the destructive boundary/safety stages:

- a second live-source refresh immediately before destructive authorization
- exact USB identity inspection and short-lived preflight
- exact typed erase phrase
- another identity check inside the elevated erase boundary
- GPT/FAT32 creation and verified EFI/Recovery copy
- mounted-volume-to-physical-disk verification
- SHA-256 verification of every copied file

If a critical source goes offline, a component version changes while the installer is being prepared, the USB identity changes, authorization expires, or any hash check fails, physical writing stops.

### Windows / Linux guarded media ✅

For **Windows 11**, This computer mode evaluates the detected PC first. If TPM 2.0 is the only blocking requirement that CorePilot can safely remediate, it can automatically select **Windows 11 compatibility media** and apply the implemented Windows Setup `BypassTPMCheck` / `BypassSecureBootCheck` settings while keeping the normal UEFI/FAT32 media layout. Legacy BIOS and low RAM remain real blockers, and CorePilot does not claim an unverified generic CPU/storage bypass.

In **Other computer** mode, Windows/Linux media preparation intentionally ignores this PC's TPM, CPU, Secure Boot and firmware state. Because the target hardware is unknown, CorePilot prepares standard Windows media and does not claim target-PC compatibility.

Verify resolves the selected Windows download path only (Windows 10 does not depend on the Windows 11 page, and vice versa) plus the common GitHub-verified Fido resolver. The physical writer rechecks the exact USB identity inside the elevated erase boundary, creates GPT/FAT32 media, copies the mounted official ISO, splits `install.wim` when it exceeds FAT32 limits, applies the selected setup compatibility profile when required and re-hashes critical boot files.

**Linux** verification is scoped to the selected distribution only. Verify downloads the official ISO and official SHA-256 metadata for that distribution. The physical writer rechecks the target identity, raw-writes the verified image and performs a full SHA-256 read-back over the written image length.

Windows and Linux now use the same **Verify → READY TO WRITE → Write to disk** model. Verify prepares the selected official installer image before USB is selected; Write to disk consumes that exact prepared image through a guarded physical writer.

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
- prepared Windows/Linux ISO provenance + SHA-256 metadata when applicable
- verified Windows/Linux USB write transcript when applicable
- CorePilot-generated macOS workspace/build/recovery/manifest/dry-run/preflight/confirmation/write metadata

CorePilot does not copy arbitrary user files or documents into the bundle.

Before packaging, it redacts known computer/user names and local profile paths, USB serial numbers, target identity fingerprints and destructive confirmation phrases.

## Error focus

A real `ERROR` now automatically opens or focuses the Activity Log and selects the exact error entry so the stack trace is immediately visible.

Warnings do not pop the window.

The log window also has **Copy selected**, which copies the selected timestamp, level, subsystem, message and stack trace to the clipboard.

## Next

1. Add the dedicated native-Apple/OCLP media writer for genuine Macs that need legacy patching.
2. Expand hardware-specific Windows/Linux remediation only where a real installer dependency requires it.
3. Add further media-layout fallbacks if future Windows images exceed the current guarded FAT32 + DISM split strategy.

## Build

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore CorePilot.sln
dotnet build CorePilot.sln -c Release
dotnet run --project src/CorePilot.App/CorePilot.App.csproj
```

GitHub Actions validates the Python bridge, isolated physical-writer safety markers, online-source trust/fallback behavior, component-integrity gates, state-machine transitions, Windows/Linux/macOS compatibility checks, then publishes a self-contained versioned `CorePilot-win-x64` test artifact.


## Simple two-step UI

The development UI now keeps the normal flow intentionally small:

1. **Verify** — one-click preparation: hardware scan → online remediation/tool lookup → hardware-specific configuration → required dependency resolution → payload generation/download → validation. No USB disk is required.
2. **Write to disk** — becomes available only when CorePilot has a prepared writable result. It re-validates the prepared payload and performs the guarded destructive USB stages.

For supported Hackintosh/OpenCore targets the flow is now: **Verify** → Hardware Sniffer Deep Scan → current verified OpCore-Simplify staging → hardware-specific EFI/kext/patch configuration → `ocvalidate` + structural/integrity audit → Apple Recovery → final installer manifest. Then **Write to disk** → live-source revalidation → USB safety inspection → manifest-bound write plan → short-lived preflight → exact typed erase phrase → final USB identity check → elevated physical write → SHA-256 verification of every copied EFI/Recovery file.

For **Windows 10/11**, This computer mode checks the detected hardware, while Other computer mode prepares standard universal media without using this PC as the compatibility gate. In This computer mode, Windows 11 may automatically use the guarded compatibility profile when TPM 2.0 is the remaining supported blocker; Legacy BIOS is not treated as resolved. Verify resolves the current official Microsoft retail ISO through the GitHub-verified Fido source, downloads it, records its SHA-256 and prepares it for the guarded Windows USB writer. The writer keeps the Windows USB on the GPT/FAT32 UEFI path, applies the verified setup compatibility profile when selected, and automatically splits an oversized `install.wim` with DISM when required.

For **Ubuntu, Fedora, Debian and Linux Mint**, Verify resolves the current official x86_64 image, downloads the publisher checksum, verifies the ISO SHA-256, and prepares it for the guarded Linux raw-image writer. After writing, CorePilot reads back the full image-length region from the USB and requires the SHA-256 to match before reporting success.

Genuine Apple Macs stay on a separate native/OCLP compatibility path instead of being forced through the Hackintosh writer. The dedicated genuine-Apple physical media writer is still pending.

### Genuine Apple Mac path

CorePilot now separates genuine Apple hardware from Hackintosh/OpenCore compatibility rules.

For **MacBookPro14,1 / 14,2 / 14,3 (2017)** with **macOS Ventura 13**:

- Ventura is treated as natively supported.
- no Hackintosh kexts, kernel patches or OpenCore boot arguments are requested.
- firmware/graphics are evaluated as part of the known Apple platform instead of generic PC rules.

Newer macOS targets on the 2017 MacBook Pro are not marked natively supported; they stay blocked until the dedicated OpenCore Legacy Patcher path is integrated.


## Online source catalog

CorePilot no longer treats the versions of OpenCore, kexts and helper tools as permanently bundled application data.

At **Verify** time CorePilot first resolves the current `CaseyCZ/CorePilot` `main` commit through the GitHub API, requires that commit to be GitHub-verified, and only then downloads `src/CorePilot.Core/Data/source-catalog.json` from that immutable commit SHA.

If the network/catalog is temporarily unavailable, CorePilot can use the cached or bundled catalog for diagnostics, but a cached critical source does **not** authorize physical writing. The execution-critical OpCore-Simplify catalog entry is additionally policy-locked to `lzhoang2801/OpCore-Simplify`, branch `main`, with a verified commit required.

The catalog currently covers:

- Apple macOS download/install + version sources
- Dortania OpenCore Install Guide
- Acidanthera OpenCorePkg
- OpCore-Simplify
- MacRecoveryX
- USBToolBox
- ProperTree
- OpenCore Auxiliary Tools (OCAT)
- Hackintool
- GenSMBIOS
- official OpenCore Legacy Patcher
- kgp OCLP mod as an explicitly experimental secondary source
- Acidanthera kext upstreams such as Lilu, VirtualSMC, WhateverGreen, AppleALC, IntelMausi, AirportBrcmFixup, NVMeFix and RestrictEvents
- Microsoft Windows download sources
- Rufus official/upstream sources
- Ubuntu, Fedora, Debian and Linux Mint official download sources

GitHub release sources resolve the current stable release. Branch-based tools resolve the current upstream branch head. The OpCore-Simplify execution path additionally requires the resolved GitHub commit to be **verified** before CorePilot will execute it.

OpCore-Simplify is resolved to a GitHub-verified commit and CorePilot downloads a fresh commit-addressed archive for each staging run instead of trusting an old executable source cache. The archive is SHA-256 hashed and recorded in the workspace manifest. Hardware-Sniffer-CLI is also checked against the SHA-256 digest published on its GitHub Release asset before CorePilot executes it. After EFI generation, CorePilot additionally audits OpCore-Simplify's `OCK_Files/history.json`: every recorded OpenCore/kext component must have an HTTPS source, stable id and SHA-256 metadata, and every file in each cache folder must match its integrity manifest. OpenCorePkg and cataloged kexts are also checked against the current live CorePilot release catalog. The same live sources are re-resolved again immediately before USB authorization.

This lets source URLs and component metadata be updated from the online catalog without requiring a new CorePilot application release.
