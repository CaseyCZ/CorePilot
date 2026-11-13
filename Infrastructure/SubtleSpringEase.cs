using System.Windows;
using System.Windows.Media.Animation;

namespace CorePilot.Infrastructure;

/// <summary>
/// A tightly damped spring for short content-only RenderTransform motion.
/// Never use its overshoot for HWND sizing, hit regions or layout dimensions.
/// </summary>
public sealed class SubtleSpringEase : EasingFunctionBase
{
    private const double Damping = 9.0;
    private const double Frequency = 10.0;

    protected override double EaseInCore(double normalizedTime)
    {
        static double Position(double t) =>
            1 - Math.Exp(-Damping * t) *
                (Math.Cos(Frequency * t) +
                 (Damping / Frequency) * Math.Sin(Frequency * t));

        return Position(Math.Clamp(normalizedTime, 0, 1)) / Position(1);
    }

    protected override Freezable CreateInstanceCore() => new SubtleSpringEase();
}
