using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CorePilot.Interop;
using CorePilot.Models;

namespace CorePilot;

public partial class MainWindow
{
    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        ActivateNavigationKeyboard();
        NavigateCarousel(-1);
        e.Handled = true;
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        ActivateNavigationKeyboard();
        NavigateCarousel(1);
        e.Handled = true;
    }

    private void NavigateCarousel(int direction)
    {
        if (_isFullscreenSuppressed) return;
        var index = Array.IndexOf(Carousel, _view);
        index = index < 0 ? (direction < 0 ? 0 : Carousel.Length - 1)
                          : index;
        var next = (index + direction + Carousel.Length) % Carousel.Length;
        _collapseTimer.Stop();
        SwitchTo(Carousel[next], fast: true, explicitRequest: true);
    }

    /// <summary>
    /// Keyboard focus is requested only after an explicit mouse click, never
    /// from hover or background notifications.
    /// </summary>
    private void ActivateNavigationKeyboard()
    {
        if (_hwnd == IntPtr.Zero) return;
        WindowStyler.SetKeyboardNavigation(_hwnd, true);
        _keyboardNavigationActive = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_view is IslandView.Idle or IslandView.Peek) return;
            Activate();
            Keyboard.Focus(NextPageButton);
        }));
    }

    private void ReleaseKeyboardNavigation()
    {
        if (!_keyboardNavigationActive || _hwnd == IntPtr.Zero) return;
        _keyboardNavigationActive = false;
        WindowStyler.SetKeyboardNavigation(_hwnd, false);
    }

    private void Island_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_view is IslandView.Idle or IslandView.Peek) return;
        if (e.Key == Key.Escape)
        {
            _viewModel.IsPinned = false;
            SwitchTo(IslandView.Idle, force: true);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (FocusedEditorOrSlider(Keyboard.FocusedElement as DependencyObject)) return;
        NavigateCarousel(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }

    private static bool FocusedEditorOrSlider(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is RangeBase or TextBoxBase or ComboBox or ListBox)
                return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : null;
        }
        return false;
    }
}
