# CorePilot Changelog

## Unreleased — development branch

- Improve hardware-aware Hackintosh preparation: select one version-specific Intel Wi-Fi kext family, defer unsupported Tahoe audio instead of requiring AppleALC, and explicitly warn when audio layout or post-install USB port mapping is not verified. Actual model/device support still depends on upstream EFI generation and verification.
- Correct 2017 genuine Apple Mac Sonoma/Sequoia/Tahoe classifications: an OCLP-assisted manual installation may be possible. Tahoe 26 with OCLP 3.0 RC stays amber/experimental; this Windows app still never claims to write a native Apple installer. Warn T1-equipped Touch Bar models against whole-disk erasure.
- Add user-selected Windows 10/11 ISO inspection with optional user-entered checksum. Custom ISO origin remains unverified and amber, including when the checksum matches.
- Rehash Windows ISOs inside the elevated erase boundary; block using an ISO stored on the target USB.
- Prepare Windows/Linux media after selecting and checking the target USB, using a single main **Create installation USB** action and a separate identifiable erase confirmation.
- Add resumable Windows ISO downloads with partial-file metadata, bounded retries, and safe restart when a download object changes.
- Add opt-in Czech regional/keyboard and privacy-focused Windows setup choices. Account creation remains inside Windows Setup and no password is stored on USB.
- Keep advanced typed disk confirmation and separate preparation/writing controls available.
- Use the same primary Create installation USB action for supported OpenCore targets, preserving the macOS preflight and USB identity protections. Show device name, capacity and letters in both standard and advanced erase confirmations.
- Add an in-app genuine Apple Mac installation guide linking to Apple's `createinstallmedia` instructions and OCLP's official installer guide. Native Apple writing remains deliberately blocked in this Windows-only application.

## 0.18 — November 2026

- Introduced the safer Verify / Prepare / Write workflow and cancellation for non-destructive operations.
- Updated the desktop application to .NET 10 and added semantic ready/warning/error UI status colours.
- Added the continuously published Windows x64 development build and source verification safeguards.

## Earlier development

- Implemented hardware detection, USB safety inspection and guarded Windows/Linux media writing.
- Developed hardware-aware macOS/OpenCore preparation and EFI validation with a separate legacy-Apple path.
- Added the live online source catalog, state-machine workflow, support bundles and activity logging.

See the repository history and GitHub releases for individual earlier changes.
