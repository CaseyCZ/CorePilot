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
        ActivateNavigationKeyboard(PreviousPageButton);
        NavigateCarousel(-1);
        e.Handled = true;
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        ActivateNavigationKeyboard(NextPageButton);
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
    private void ActivateNavigationKeyboard(FrameworkElement? requestedFocus = null)
    {
        if (_hwnd == IntPtr.Zero) return;
        WindowStyler.SetKeyboardNavigation(_hwnd, true);
        _keyboardNavigationActive = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_keyboardNavigationActive ||
                _view is IslandView.Idle or IslandView.Peek) return;

            var focusTarget =
                requestedFocus is { IsVisible: true, Focusable: true }
                    ? requestedFocus
                    : NextPageButton.IsVisible
                        ? NextPageButton
                        : _dots.FirstOrDefault(dot =>
                            dot.IsVisible && dot.Tag is IslandView view && view == _view);

            // Hover alone never steals foreground focus.
            if (focusTarget is null) return;
            Activate();
            Keyboard.Focus(focusTarget);
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
            // Let an open editor/dropdown consume Escape before collapsing the island.
            if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true })
                return;

            _viewModel.IsPinned = false;
            SwitchTo(IslandView.Idle, force: true);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Left or Key.Right)) return;

        // Arrow keys belong to sliders, text editing and other controls when focused.
        // Carousel keyboard navigation is available only from its own chrome.
        var focused = Keyboard.FocusedElement;
        if (!ReferenceEquals(focused, PreviousPageButton) &&
            !ReferenceEquals(focused, NextPageButton) &&
            !_dots.Any(dot => ReferenceEquals(focused, dot))) return;

        NavigateCarousel(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }
}
