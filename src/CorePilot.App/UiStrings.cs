using System.Resources;

namespace CorePilot.App;

/// <summary>
/// English UI resources. Additional cultures can be added via satellite .resx files.
/// </summary>
public static class UiStrings
{
    private static readonly ResourceManager Manager = new(
        "CorePilot.App.Resources.UiStrings", typeof(UiStrings).Assembly);

    private static string Get(string name) =>
        Manager.GetString(name, System.Globalization.CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI text '{name}'.");

    public static string CreateInstallationUsb => Get(nameof(CreateInstallationUsb));
    public static string CancelCheck => Get(nameof(CancelCheck));
    public static string CancelDownload => Get(nameof(CancelDownload));
    public static string CancelPreparation => Get(nameof(CancelPreparation));
    public static string CustomIsoOriginUnverified => Get(nameof(CustomIsoOriginUnverified));
    public static string CustomIsoChecksumMatches => Get(nameof(CustomIsoChecksumMatches));
    public static string DownloadInterrupted => Get(nameof(DownloadInterrupted));
    public static string DownloadRestarting => Get(nameof(DownloadRestarting));
    public static string DownloadExpired => Get(nameof(DownloadExpired));
    public static string ConfirmUsbErase => Get(nameof(ConfirmUsbErase));
    public static string UsbNotSelected => Get(nameof(UsbNotSelected));
}
