using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CorePilot.Infrastructure;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.35;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class RatioToHeightConverter : IValueConverter
{
    public double MaxHeight { get; set; } = 34;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? d : 0;
        var minimum = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) ? min : 3;
        return Math.Max(minimum, Math.Clamp(ratio, 0, 1) * MaxHeight);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        var visible = Invert ? !hasText : hasText;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}


/// <summary>
/// WPF RangeBase.Value rejects NaN and non-finite numbers. Media sessions,
/// third-party metadata and saved settings are untrusted numeric inputs.
/// Normalize them before binding any ProgressBar/Slider to avoid XamlParseException.
/// </summary>
public static class FiniteRange
{
    public static double Clamp(object? value, double minimum = 0, double maximum = 1)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Invalid UI range.");
        double numeric = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => minimum
        };
        return double.IsFinite(numeric) ? Math.Clamp(numeric, minimum, maximum) : minimum;
    }
}

public sealed class SafeProgressValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        FiniteRange.Clamp(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
