namespace CorePilot.Windows;

public sealed record WindowsMediaOptions(
    bool ExtendedHardwareCompatibility,
    bool LegacyBiosCompatible)
{
    public static WindowsMediaOptions Standard { get; } =
        new(false, false);

    public static WindowsMediaOptions OlderPc { get; } =
        new(true, true);

    public string ModeText =>
        ExtendedHardwareCompatibility || LegacyBiosCompatible
            ? "Older PC compatibility"
            : "Standard Windows media";
}
