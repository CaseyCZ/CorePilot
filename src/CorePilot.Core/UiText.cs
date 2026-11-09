namespace CorePilot.Core;
public static class UiText
{
    public static string Time(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
    public static double PercentToScalar(double value) => Math.Clamp(value / 100d, 0, 1);
    public static int ScalarToPercent(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 100, MidpointRounding.AwayFromZero);
    public static double ClampSeek(double seconds, double duration) => Math.Clamp(seconds, 0, Math.Max(0, duration));
}
