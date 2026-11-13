using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CorePilot.Infrastructure;
using CorePilot.Models;
using CorePilot.Services;
using CorePilot.Views;

namespace CorePilot.RangeRegression;

internal static class Program
{
    private sealed class Sample
    {
        public object? Value { get; init; }
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new System.Windows.Application();
            // Load real shipped theme resources and exercise the actual WPF views.
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/CorePilot;component/Themes/Theme.Graphite.xaml")
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/CorePilot;component/Themes/Styles.xaml")
            });
            app.Resources["SafeProgress"] = new SafeProgressValueConverter();

            var converter = new SafeProgressValueConverter();
            var values = new (object? raw, double expected)[]
            {
                (double.NaN, 0),
                (double.PositiveInfinity, 0),
                (double.NegativeInfinity, 0),
                (null, 0),
                ("invalid media progress", 0),
                (-4.0, 0),
                (0.4, 0.4),
                (99.0, 1)
            };

            foreach (var (raw, expected) in values)
            {
                var normalized = (double)converter.Convert(raw, typeof(double), null, CultureInfo.InvariantCulture);
                AssertEqual(expected, normalized, "finite conversion");

                var bar = new ProgressBar { Minimum = 0, Maximum = 1 };
                BindingOperations.SetBinding(bar, RangeBase.ValueProperty, new Binding(nameof(Sample.Value))
                {
                    Source = new Sample { Value = raw },
                    Converter = converter,
                    Mode = BindingMode.OneWay
                });
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                AssertEqual(expected, bar.Value, "real WPF RangeBase.Value binding");
            }

            var cache = new BoundedLruCache<string, string>(2, StringComparer.OrdinalIgnoreCase);
            cache.Set("one", "A");
            cache.Set("two", "B");
            if (!cache.TryGetValue("ONE", out var first) || first != "A")
                throw new InvalidOperationException("LRU case-insensitive lookup failed.");
            cache.Set("three", "C");
            if (cache.Count != 2 || cache.TryGetValue("two", out _) ||
                !cache.TryGetValue("one", out _))
                throw new InvalidOperationException("LRU did not evict the least recent entry.");
            cache.Set("one", "Updated");
            if (cache.Count != 2 || !cache.TryGetValue("one", out first) || first != "Updated")
                throw new InvalidOperationException("LRU replacement produced duplicate nodes.");
            cache.Clear();
            if (cache.Count != 0)
                throw new InvalidOperationException("LRU cache did not clear.");

            var spring = new SubtleSpringEase { EasingMode = EasingMode.EaseIn };
            AssertEqual(0, spring.Ease(0), "spring start");
            AssertEqual(1, spring.Ease(1), "spring end");
            for (var t = 0.0; t <= 1.0; t += 0.01)
            {
                var value = spring.Ease(t);
                if (!double.IsFinite(value) || value < -0.01 || value > 1.07)
                    throw new InvalidOperationException("Spring easing exceeds its bounded overshoot.");
            }

            AssertEqual(0.15, FiniteRange.Clamp(double.NaN, 0.15, 1.0), "NaN guard opacity");
            AssertEqual(0.15, FiniteRange.Clamp(double.PositiveInfinity, 0.15, 1.0), "nonfinite guard opacity");
            AssertEqual(1.0, FiniteRange.Clamp(12.0, 0.15, 1.0), "clamped guard opacity");

            var track = new TrackInfo { Duration = TimeSpan.FromTicks(1), Position = TimeSpan.FromTicks(50) };
            AssertEqual(1.0, track.Progress, "media timeline must stay in range");
            AssertEqual(0.0, TrackInfo.Empty.Progress, "empty media timeline");

            // The original screenshot showed a XAML RangeBase.Value exception.
            // Instantiate the real views to catch XAML parsing failures as well.
            _ = new PlayerView();
            _ = new PeekView();
            _ = new WaterView();

            // SettingsWindow is not normally displayed during startup. Construct it
            // to detect missing XAML handlers/resources or binding initialization errors.
            var settingsWindow = new SettingsWindow(new SettingsStore());
            if (settingsWindow.FindName("MonitorCombo") is not ComboBox monitors ||
                monitors.Items.Count == 0 ||
                settingsWindow.FindName("TimeFormatCombo") is not ComboBox timeFormats ||
                timeFormats.SelectedItem is null ||
                settingsWindow.FindName("DateFormatCombo") is not ComboBox dateFormats ||
                dateFormats.SelectedItem is null)
                throw new InvalidOperationException("Settings UI failed to initialize.");
            settingsWindow.Close();
            Console.WriteLine("PASS: media, peek, water and Settings views load; unsafe values are rejected safely.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void AssertEqual(double expected, double actual, string what)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }
}
