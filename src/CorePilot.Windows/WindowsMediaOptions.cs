namespace CorePilot.Windows;

public sealed record WindowsMediaOptions(
    bool ExtendedHardwareCompatibility,
    bool LegacyBiosCompatible)
{
    public static WindowsMediaOptions Standard { get; } =
        new(
            ExtendedHardwareCompatibility: false,
            LegacyBiosCompatible: false);

    public static WindowsMediaOptions OlderPc { get; } =
        new(
            ExtendedHardwareCompatibility: true,
            LegacyBiosCompatible: false);

    public string ModeText =>
        ExtendedHardwareCompatibility
            ? "Windows 11 compatibility media"
            : "Standard Windows media";
}
