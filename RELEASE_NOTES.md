# CorePilot 2.3 — Improved reliability and refined glass appearance

## Stability
- Adds defensive validation for all progress values coming from Windows media sessions and user settings. Undefined or infinite values can no longer reach WPF progress controls.
- Guards saved opacity settings against invalid values.
- Improves startup diagnostics, including the underlying exception when a window fails to load.
- Updates the Windows startup smoke test to **reject error dialog windows** rather than passing just because the process is still running.
- Includes Windows regression tests that feed NaN, infinities and out-of-range numbers to real WPF progress controls and load the actual media/peek/water views.

## Design
- Adds a restrained dark, translucent gradient and thin glass rim to the main Dynamic Island.
- Refines the graphite theme with cool mint/blue accents and a matching compact navigation strip.
- Inspired by V-Notch's clean desktop aesthetic; this is **not** its DirectX 11 Liquid Glass/refraction engine.

CorePilot remains docked in its compact overview within a top/bottom Windows taskbar when inactive. Expanded views, left/right arrows, clickable dots and keyboard navigation are preserved. Czech and English remain available.

Windows 10 and 11, x64. The installer is unsigned. Includes required MIT license and attribution for the NusaLite base.
