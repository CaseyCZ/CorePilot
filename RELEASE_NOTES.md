# CorePilot 2.4.1 — Navigation and Mini Player reliability

## Interaction improvements
- Navigation arrows retain a transparent appearance while offering 44 × 44 DIP hit areas and keyboard focus indicators.
- Carousel navigation dots are real accessible buttons, with localized names and visible keyboard focus.
- Wheel input over buttons, sliders, selectors and scrollable content no longer switches carousel pages.
- The horizontal file shelf consumes vertical-wheel gestures to scroll files rather than switching pages.
- Arrow-key page switching is confined to carousel navigation controls; active editors and sliders retain their own keyboard input.

## Startup regression
- A dedicated Mini Player regression check asserts that read-only `TrackProgress` is bound OneWay and evaluates successfully.

---

## Also included from CorePilot 2.4



## Personalization
- The island now defaults to opaque black; optional Graphite/Void glass themes retain a subtle gradient and 1-DIP rim.
- Navigation arrows no longer have a filled background.
- The overview shows date only, without time-of-day greetings.
- Content slide transitions use a subtly damped spring easing function, while HWND sizes remain non-overshooting.
- Date and time formats are configurable, previewed in CZ/EN, persisted locally and refreshed immediately.
- Longer date formats increase the overview width rather than clipping the date.
- Settings now includes monitor selection, theme selection and transition animation controls. System-wide reduced-motion settings are respected.

## Stability and performance
- Per-monitor island placement replaces the primary-screen-only assumption; display changes trigger safe repositioning.
- Position updates no longer force hidden fullscreen overlays to show.
- Windows media session updates marshal back to the WPF Dispatcher and coalesce pending timeline refreshes.
- Media metadata refreshes reject stale asynchronous results; external metadata is length-limited.
- Playback timeline polling slows to 2.5 seconds when not playing, and remains at 500 ms during playback.
- Clipboard history remains opt-in and RAM-only; retained image pixel data now has a 16 MiB budget estimate.
- Artwork decoding releases source streams and rejects oversized encoded images.
- The online artwork cache uses a bounded 24-item LRU with regression coverage, preventing unbounded cover accumulation.
- Win32 region replacement avoids duplicate work and checks GDI errors; event subscriptions and timers are cleaned up.

## Verification
- CI builds the .NET 10 WPF app for Windows x64, runs progress/visual-load regression tests, launches the published executable, installs the Inno Setup EXE silently and verifies installed startup.
- The settings window is included in the regression coverage, including the new format/display selectors.
- Manual validation is still required for mixed-DPI, monitor unplug, exclusive fullscreen games, sleep/resume and accessibility.

## Compatibility
Windows 10 build 19041 (2004) or later, Windows 11, x64. The installer is unsigned; SmartScreen may warn.
MIT license and attribution for the NusaLite open-source base remain included.
