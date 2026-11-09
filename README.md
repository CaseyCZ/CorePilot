<div align="center">

# CorePilot

### A smarter place for everything happening on your Windows desktop.

**Music · Volume · Clipboard · Lock keys · Personalization**

A compact Dynamic Island-inspired companion for **Windows 10 and Windows 11**. A tiny floating island stays out of the way and expands when you want to control music or your desktop.

</div>

## One island, fewer distractions

- **Now Playing:** artwork, track, artist, playback, seeking where supported
- **Volume:** master volume, mute, brief status when the level changes
- **Clipboard:** opt-in local history for text and pictures; tap to copy again or clear
- **Caps Lock / Num Lock:** understated status indications
- **Mini player:** optional floating media widget near the taskbar
- **Appearance:** dark/light mode, animations and per-feature preferences

Everything can be configured from the same application. The island sits at the top of the primary monitor and expands when clicked.

## Compatibility & current status

**Source preview — early development; Windows runtime verification pending.** Targets Windows 10 2004+ and Windows 11 on x64 hardware. Designed with C# / .NET 10 and WPF.

The mini player floats **above** the taskbar rather than embedding inside it. True Windows 11 Mica and Acrylic backgrounds, suppression of native Windows volume flyouts, multi-monitor positioning and the final installer are **not implemented**. Unsupported media players may not provide transport/seek features.

## Build from source

Install the **.NET 10 SDK** on Windows 10 or 11, then run:

```powershell
dotnet restore CorePilot.slnx
dotnet build CorePilot.slnx -c Release
dotnet run --project tests/CorePilot.Tests/CorePilot.Tests.csproj -c Release
dotnet run --project src/CorePilot/CorePilot.csproj -c Release
```

Publish a standalone Windows x64 executable:

```powershell
dotnet publish src/CorePilot/CorePilot.csproj -c Release -r win-x64 --self-contained true -o out/win-x64
```

GitHub Actions builds and smoke-tests the application on Windows, then uploads a downloadable build artifact.

## Privacy

Clipboard tracking is **off by default**, retains at most 20 recent entries (up to five images) **in memory only**, and is cleared on exit or when monitoring is disabled. Clipboard contents are not sent to any server. The settings file only stores your preferences in `%LocalAppData%\CorePilot\settings.json`.

Some clipboard data can be sensitive; Windows cannot reliably mark all passwords and secrets. Only enable history if you understand this risk.

## Inspiration

CorePilot is an original implementation inspired by the interaction patterns of [Nusa](https://github.com/rama2555/nusa) and the Fluent style of [FluentFlyout](https://github.com/unchihugo/FluentFlyout). Their proprietary code/assets are not included. Reusing FluentFlyout's GPL-covered code in the future would require compliance with its license.

The original USB installer project formerly hosted in this repository has been superseded. Older downloadable releases, if still visible on GitHub, belong to that **old product**, not this desktop companion.

## Quality

See [testing notes](docs/TESTING.md) for verified and pending checks. Contributions and bug reports are welcome.

---

**CorePilot** — less clutter, more control.
