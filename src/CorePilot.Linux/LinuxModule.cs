using CorePilot.Core;

namespace CorePilot.Linux;

public sealed class LinuxModule : ISystemModule
{
    public string Id => "linux";
    public string DisplayName => "Linux";
    public string Description => "Linux distribution boot media workflow.";
    public IReadOnlyList<SystemVariant> Variants { get; } =
    [
        new("ubuntu", "Ubuntu"),
        new("fedora", "Fedora"),
        new("linux-mint", "Linux Mint"),
        new("debian", "Debian")
    ];
}
