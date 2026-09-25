namespace CorePilot.Windows;

public sealed record WindowsMediaOptions(
    bool ExtendedHardwareCompatibility)
{
    public static WindowsMediaOptions Standard { get; } =
        new(
            ExtendedHardwareCompatibility: false);

    public static WindowsMediaOptions OlderPc { get; } =
        new(
            ExtendedHardwareCompatibility: true);

    public string ModeText =>
        ExtendedHardwareCompatibility
            ? "Windows 11 compatibility media"
            : "Standard Windows media";
}
