using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CorePilot.Core;
using CorePilot.Models;
using CorePilot.Services;

namespace CorePilot;
public partial class MainWindow : Window
{
    private readonly Preferences _settings = Preferences.Load();
    private readonly MediaBridge _media = new();
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    private ClipboardMonitor? _clipboard;
    private WidgetWindow? _widget;
    private MediaSnapshot _currentMedia = MediaSnapshot.Empty;
    private bool _mediaRefreshing, _mediaErrorReported, _updatingVolume, _ready, _disposed, _lastCaps, _lastNum, _lastMuted;
    private int _lastVolume = -1;
    private DateTime _statusUntil;

    public MainWindow()
    {
        InitializeComponent();
        _mediaTimer.Tick += async (_, _) => await RefreshMediaAsync();
        _systemTimer.Tick += (_, _) =>
        {
            try { RefreshSystem(); }
            catch (Exception ex)
            {
                StartupDiagnostics.Log("System status update", ex);
                _systemTimer.Stop();
            }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetWindowSize(false);
        try
        {
            _clipboard = new ClipboardMonitor(this);
            _clipboard.SetEnabled(_settings.ClipboardEnabled);
            ClipboardList.ItemsSource = _clipboard.Entries;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("Optional clipboard monitor", ex);
            ClipboardCheck.IsEnabled = false;
        }
        _lastCaps = KeyboardStatus.CapsLock; _lastNum = KeyboardStatus.NumLock;
        ClipboardCheck.IsChecked = _settings.ClipboardEnabled;
        WidgetCheck.IsChecked = _settings.TaskbarWidget;
        VolumeCheck.IsChecked = _settings.ShowVolumeNotification;
        KeysCheck.IsChecked = _settings.ShowKeyNotification;
        DarkCheck.IsChecked = _settings.DarkTheme;
        AnimationsCheck.IsChecked = _settings.Animations;
        ApplyTheme();
        try { ConfigureTray(); }
        catch (Exception ex) { StartupDiagnostics.Log("Tray initialization", ex); }
        try { ApplyWidget(); }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("Optional mini player", ex);
            _widget = null;
            WidgetCheck.IsEnabled = false;
        }
        ClockText.Text = DateTime.Now.ToString("H:mm");
        _ready = true; _mediaTimer.Start(); _systemTimer.Start();
        await RefreshMediaAsync();
    }

    private void ConfigureTray()
    {
        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.Text = "CorePilot";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ExpandIsland);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Otevřít Island", null, (_, _) => Dispatcher.Invoke(ExpandIsland));
        menu.Items.Add("Schránka", null, (_, _) => Dispatcher.Invoke(() => { ExpandIsland(); ShowTab("clipboard"); }));
        menu.Items.Add("Ukončit", null, (_, _) => Dispatcher.Invoke(Close));
        _tray.ContextMenuStrip = menu;
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaRefreshing || _disposed) return;
        _mediaRefreshing = true;
        try
        {
            var snapshot = await _media.RefreshAsync();
            if (_disposed) return;
            _currentMedia = snapshot;
            TrackTitle.Text = snapshot.Title;
            TrackArtist.Text = snapshot.Artist;
            AlbumImage.Source = snapshot.Artwork;
            ArtworkPlaceholder.Visibility = snapshot.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
            PlayButton.Content = snapshot.Playing ? "Ⅱ" : "▶";
            TimeElapsed.Text = UiText.Time(snapshot.Position);
            TimeRemaining.Text = UiText.Time(snapshot.Duration);
            SeekSlider.IsEnabled = snapshot.Available && snapshot.Duration.TotalSeconds > 0;
            SeekSlider.Maximum = Math.Max(1, snapshot.Duration.TotalSeconds);
            if (!SeekSlider.IsMouseCaptureWithin)
                SeekSlider.Value = UiText.ClampSeek(snapshot.Position.TotalSeconds, snapshot.Duration.TotalSeconds);
            if (_widget is not null)
            {
                _widget.Update(snapshot);
                if (snapshot.Available && !_widget.IsVisible) _widget.Show();
                else if (!snapshot.Available && _widget.IsVisible) _widget.Hide();
            }
        }
        catch (Exception ex)
        {
            if (!_mediaErrorReported)
            {
                StartupDiagnostics.Log("Media session refresh", ex);
                _mediaErrorReported = true;
            }
        }
        finally { _mediaRefreshing = false; }
    }

    private void RefreshSystem()
    {
        if (_disposed) return;
        ClockText.Text = DateTime.Now.ToString("H:mm");
        if (DateTime.UtcNow >= _statusUntil) StatusText.Visibility = Visibility.Collapsed;
        bool caps = KeyboardStatus.CapsLock, num = KeyboardStatus.NumLock;
        if (_settings.ShowKeyNotification)
        {
            if (caps != _lastCaps) ShowStatus($"Caps Lock {(caps ? "zap." : "vyp.")}");
            else if (num != _lastNum) ShowStatus($"Num Lock {(num ? "zap." : "vyp.")}");
        }
        _lastCaps = caps; _lastNum = num;
        if (!AudioEndpoint.TryGet(out float volume, out bool muted)) return;
        int percent = UiText.ScalarToPercent(volume);
        if (_lastVolume >= 0 && (percent != _lastVolume || muted != _lastMuted) && _settings.ShowVolumeNotification)
            ShowStatus(muted ? "Zvuk ztlumen" : $"Hlasitost {percent} %");
        _lastVolume = percent; _lastMuted = muted;
        if (!_updatingVolume && !VolumeSlider.IsMouseCaptureWithin)
        {
            _updatingVolume = true;
            try {
                VolumeSlider.Value = percent;
                VolumeLabel.Text = $"{percent} %";
                MuteButton.Content = muted ? "🔇" : "🔊";
            } finally { _updatingVolume = false; }
        }
    }
    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        _statusUntil = DateTime.UtcNow.AddSeconds(2.4);
    }
    private void SetWindowSize(bool expanded)
    {
        Width = expanded ? 760 : 326; Height = expanded ? 475 : 58;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = 0;
    }
    private void ExpandIsland()
    {
        if (_disposed) return;
        SetWindowSize(true); ExpandedPanel.Visibility = Visibility.Visible;
        if (_settings.Animations)
        {
            ExpandedPanel.Opacity = 0;
            ExpandedPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));
        } else ExpandedPanel.Opacity = 1;
        Activate();
    }
    private void CollapseIsland() { ExpandedPanel.Visibility = Visibility.Collapsed; SetWindowSize(false); }
    private void ToggleIsland_Click(object sender, RoutedEventArgs e)
    {
        if (ExpandedPanel.Visibility == Visibility.Visible) CollapseIsland(); else ExpandIsland();
    }
    private void Collapse_Click(object sender, RoutedEventArgs e) => CollapseIsland();
    private void ShowTab(string tab)
    {
        MediaSection.Visibility = tab == "media" ? Visibility.Visible : Visibility.Collapsed;
        ClipboardSection.Visibility = tab == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MediaTab_Click(object sender, RoutedEventArgs e) => ShowTab("media");
    private void ClipboardTab_Click(object sender, RoutedEventArgs e) => ShowTab("clipboard");
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowTab("settings");
    private async void Play_Click(object sender, RoutedEventArgs e) {
        try { await _media.ToggleAsync(); await RefreshMediaAsync(); }
        catch { ShowStatus("Přehrávač příkaz nepodporuje"); }
    }
    private async void Next_Click(object sender, RoutedEventArgs e) {
        try { await _media.NextAsync(); await RefreshMediaAsync(); }
        catch { ShowStatus("Přehrávač příkaz nepodporuje"); }
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) {
        try { await _media.PreviousAsync(); await RefreshMediaAsync(); }
        catch { ShowStatus("Přehrávač příkaz nepodporuje"); }
    }
    private async void Seek_Released(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled) return;
        try {
            await _media.SeekAsync(TimeSpan.FromSeconds(UiText.ClampSeek(SeekSlider.Value, _currentMedia.Duration.TotalSeconds)));
        } catch { ShowStatus("Posun skladby není podporován"); }
    }
    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updatingVolume) return;
        VolumeLabel.Text = $"{(int)Math.Round(e.NewValue)} %";
        if (!AudioEndpoint.TrySet((float)UiText.PercentToScalar(e.NewValue)))
            ShowStatus("Hlasitost není dostupná");
    }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (AudioEndpoint.TryGet(out _, out bool mute)) AudioEndpoint.TrySetMute(!mute);
        RefreshSystem();
    }
    private void RestoreClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ClipboardEntry entry }) {
            _clipboard?.Restore(entry);
            ShowStatus("Zkopírováno");
        }
    }
    private void ClearClipboard_Click(object sender, RoutedEventArgs e)
    {
        _clipboard?.Clear(); ShowStatus("Historie vymazána");
    }
    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ClipboardEnabled = ClipboardCheck.IsChecked == true;
        _settings.TaskbarWidget = WidgetCheck.IsChecked == true;
        _settings.ShowVolumeNotification = VolumeCheck.IsChecked == true;
        _settings.ShowKeyNotification = KeysCheck.IsChecked == true;
        _settings.DarkTheme = DarkCheck.IsChecked == true;
        _settings.Animations = AnimationsCheck.IsChecked == true;
        _clipboard?.SetEnabled(_settings.ClipboardEnabled);
        if (!_settings.ClipboardEnabled) _clipboard?.Clear();
        ApplyTheme();
        try { ApplyWidget(); }
        catch (Exception ex) { StartupDiagnostics.Log("Mini player settings", ex); WidgetCheck.IsEnabled = false; }
        _settings.Save();
    }
    private static void ChangeBrush(string key, string color)
    {
        // WPF may freeze brushes defined in application resources.
        // Replacing the resource avoids an InvalidOperationException on startup.
        System.Windows.Application.Current.Resources[key] =
            new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }
    private void ApplyTheme()
    {
        bool dark = _settings.DarkTheme;
        ChangeBrush("PanelBrush", dark ? "#EF131A2A" : "#F3F7FBFF");
        ChangeBrush("PrimaryBrush", dark ? "#F5F8FF" : "#172136");
        ChangeBrush("SecondaryBrush", dark ? "#A6B4CC" : "#596B81");
        ChangeBrush("ControlBrush", dark ? "#382C3B52" : "#FFE2E8F2");
    }
    private void ApplyWidget()
    {
        if (_settings.TaskbarWidget && _widget is null)
        {
            _widget = new WidgetWindow(ExpandIsland, _media.ToggleAsync);
            _widget.Update(_currentMedia);
            if (_currentMedia.Available) _widget.Show();
        }
        else if (!_settings.TaskbarWidget && _widget is not null)
        {
            _widget.Close(); _widget = null;
        }
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _disposed = true;
        _mediaTimer.Stop(); _systemTimer.Stop();
        _clipboard?.Dispose(); _widget?.Close(); _widget = null;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
    }
}
