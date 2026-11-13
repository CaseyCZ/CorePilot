using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CorePilot.Infrastructure;

/// <summary>
/// The island carousel owns the mouse wheel only over non-interactive whitespace.
/// Scrollable content and controls keep their own wheel gestures, including at boundaries.
/// </summary>
public static class WheelNavigationPolicy
{
    public static bool ShouldNavigate(DependencyObject? source, DependencyObject islandRoot)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, islandRoot))
                return true;

            if (source is ScrollViewer or RangeBase or TextBoxBase
                or Selector or ButtonBase or PasswordBox)
                return false;

            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        // Events from detached popups or other windows are not island navigation.
        return false;
    }
}
