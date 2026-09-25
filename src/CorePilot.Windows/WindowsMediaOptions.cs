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
            LegacyBiosCompatible: true);

    public string ModeText =>
        ExtendedHardwareCompatibility || LegacyBiosCompatible
            ? "Older PC compatibility"
            : "Standard Windows media";
}
