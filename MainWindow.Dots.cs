using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CorePilot.Models;
using CorePilot.Services;

namespace CorePilot;

public partial class MainWindow
{
    // ----- Dots ----------------------------------------------------------

    private void BuildDots()
    {
        foreach (var view in Carousel)
        {
            // A large, transparent hit target around the small visual indicator.
            var mark = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = _dotIdleBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var dot = new Border
            {
                Width = 26, Height = 22, Background = Brushes.Transparent,
                Cursor = Cursors.Hand, Tag = view, ToolTip = LabelFor(view),
                Child = mark
            };
            dot.MouseLeftButtonDown += (sender, e) =>
            {
                ActivateNavigationKeyboard();
                _collapseTimer.Stop();
                SwitchTo((IslandView)((Border)sender).Tag, fast: true, explicitRequest: true);
                e.Handled = true;
            };
            DotsPanel.Children.Add(dot);
            _dots.Add(dot);
        }
        UpdateDots();
    }

    private void UpdateDots()
    {
        foreach (var dot in _dots)
        {
            var view = dot.Tag is IslandView v ? v : IslandView.Idle;
            var active = view == _view;
            if (dot.Child is not Border mark) continue;
            mark.Width = active ? 16 : 6;
            mark.CornerRadius = new CornerRadius(4);
            mark.Background = active ? AccentFor(view) : _dotIdleBrush;
            if (MotionEnabled)
            {
                mark.BeginAnimation(OpacityProperty, new DoubleAnimation(
                    active ? 1.0 : 0.65, TimeSpan.FromMilliseconds(160)));
            }
            else
            {
                mark.BeginAnimation(OpacityProperty, null);
                mark.Opacity = active ? 1.0 : 0.65;
            }
        }
    }

    private Brush AccentFor(IslandView view) => view switch
    {
        IslandView.Lyrics => (Brush)FindResource("GoldBrush"),
        IslandView.Focus => (Brush)FindResource("EmberBrush"),
        IslandView.Water => (Brush)FindResource("AquaBrush"),
        IslandView.Shelf => (Brush)FindResource("LimeBrush"),
        IslandView.Clipboard => (Brush)FindResource("MintBrush"),
        _ => (Brush)FindResource("AccentBrush")
    };

    private void SetDotsVisible(bool visible, bool fast)
    {
        if (_dotsVisible != visible)
        {
            _dotsVisible = visible;
            DotsBar.IsHitTestVisible = visible;
            if (!MotionEnabled)
            {
                DotsSlide.BeginAnimation(TranslateTransform.YProperty, null);
                DotsBar.BeginAnimation(OpacityProperty, null);
                DotsSlide.Y = 0;
                DotsBar.Opacity = visible ? 1 : 0;
                UpdateWindowRegion();
                return;
            }

            var beginTime = visible ? TimeSpan.FromMilliseconds(fast ? 40 : 190) : TimeSpan.Zero;
            var duration = TimeSpan.FromMilliseconds(visible ? 220 : 140);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fromY = DotsSlide.Y;

            DotsSlide.BeginAnimation(TranslateTransform.YProperty, null);
            if (visible)
            {
                DotsSlide.Y = 4;
                fromY = 4;
            }
            else
            {
                DotsSlide.Y = 0;
            }

            DotsSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
                fromY,
                visible ? 0 : 4,
                duration)
            {
                BeginTime = beginTime,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            });

            var animation = new DoubleAnimation(visible ? 1 : 0, duration)
            {
                BeginTime = beginTime,
                EasingFunction = ease
            };
            DotsBar.BeginAnimation(OpacityProperty, animation);
            UpdateWindowRegion();
        }
    }

    private static string LabelFor(IslandView view) => view switch
    {
        IslandView.Hub => LanguageManager.T("Overview"),
        IslandView.Player => LanguageManager.T("Pemutar"),
        IslandView.Lyrics => LanguageManager.T("Lirik"),
        IslandView.Focus => LanguageManager.T("Fokus"),
        IslandView.Water => LanguageManager.T("Air"),
        IslandView.Clipboard => LanguageManager.T("Schránka"),
        _ => LanguageManager.T("Shelf")
    };
}
