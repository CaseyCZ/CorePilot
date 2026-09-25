using CorePilot.Core;

namespace CorePilot.Windows;

public sealed class WindowsModule : ISystemModule
{
    public string Id => "windows";
    public string DisplayName => "Windows";
    public string Description => "Microsoft Windows boot media workflow.";
    public IReadOnlyList<SystemVariant> Variants { get; } =
    [
        new("windows-11", "Windows 11"),
        new("windows-10", "Windows 10")
    ];
}
