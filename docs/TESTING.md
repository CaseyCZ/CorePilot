# CorePilot — Testing

## Verified at source-preview stage

- XAML and project file XML parse
- Source references, event names and privacy defaults can be statically inspected
- Pure logic tests for formatting, audio percentage conversion and seek clamping are included

## Unverified until Windows CI/device test

- WPF Windows 10/11 runtime behavior
- Windows Media Session transport commands/album art/seek support
- Core Audio endpoint operations
- Clipboard text/image event handling
- Global key state status notifications
- Mini player taskbar positioning, DPI and multi-monitor
- Light/dark theme and animations with real accessibility settings

## Current limitations

Mica / Acrylic backdrop effects, embedded taskbar button, suppression of native Windows volume OSD, installer, signing and update service are not yet implemented. The taskbar widget floats above the taskbar. History of copied content is opt-in and memory-only; avoid enabling on shared/sensitive systems.
