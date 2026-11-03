namespace CorePilot.Windows;

public sealed record WindowsMediaOptions(
    bool ExtendedHardwareCompatibility,
    bool CzechRegionalSettings = false,
    bool PrivacyFocusedSetup = false)
{
    public static WindowsMediaOptions Standard { get; } =
        new(
            ExtendedHardwareCompatibility: false);

    public static WindowsMediaOptions Compatibility { get; } =
        new(
            ExtendedHardwareCompatibility: true);

    public string ModeText =>
        (ExtendedHardwareCompatibility
            ? "Windows 11 compatibility media"
            : "Standard Windows media") +
        (CzechRegionalSettings ? " · Czech region/keyboard" : "") +
        (PrivacyFocusedSetup ? " · privacy options" : "");
}
