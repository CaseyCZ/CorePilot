using CorePilot.Core;

namespace CorePilot.MacOS;

public sealed class MacOSModule : ISystemModule
{
    public string Id => "macos";
    public string DisplayName => "macOS";
    public string Description => "Hardware-aware OpenCore installer workflow.";
    public IReadOnlyList<SystemVariant> Variants { get; } =
    [
        new("tahoe-26", "macOS Tahoe 26"),
        new("sequoia-15", "macOS Sequoia 15"),
        new("sonoma-14", "macOS Sonoma 14"),
        new("ventura-13", "macOS Ventura 13")
    ];
}
