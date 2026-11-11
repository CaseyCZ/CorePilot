using System.Windows;
using CorePilot.Services;
using CorePilot.ViewModels;
using CorePilot.Views;
using System.Windows.Threading;

namespace CorePilot;

public sealed class AppHost : IDisposable
{
    private SettingsStore? _settings;
    private MediaService? _media;
    private LyricsService? _lyrics;
    private Interop.CoreAudio.AudioService? _audio;
    private PowerService? _power;
    private PomodoroService? _pomodoro;
    private WaterService? _water;
    private ShelfService? _shelf;
    private TrayService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private MiniPlayerWindow? _miniPlayer;
    private DispatcherTimer? _miniTimer;

    public void Start()
    {
        try
        {
            _settings = new SettingsStore();
            LanguageManager.Current.SetLanguage(_settings.Current.Language);
            ThemeService.Apply(_settings.Current.Theme);
            _media = new MediaService();
            _lyrics = new LyricsService();
            _audio = new Interop.CoreAudio.AudioService();
            _power = new PowerService();
            _pomodoro = new PomodoroService(_settings);
            _water = new WaterService(_settings);
            _shelf = new ShelfService(_settings);

            _viewModel = new MainViewModel(_settings, _media, _lyrics, _audio, _power, _pomodoro, _water, _shelf);

            _tray = new TrayService(_settings, () => _viewModel.IsPinned);
            _tray.OpenRequested += view => _window?.OpenView(view);
            _tray.PinToggleRequested += () => _viewModel.IsPinned = !_viewModel.IsPinned;
            _tray.AutoLyricsToggled += enabled => _window?.SetAutoLyricsEnabled(enabled);
            _tray.FullscreenAutoHideToggled += enabled => _window?.SetFullscreenAutoHide(enabled);
            _tray.ClickGuardToggled += enabled => _window?.SetClickGuard(enabled);
            _tray.GlobalHotkeysToggled += enabled => _window?.SetGlobalHotkeysEnabled(enabled);
            _tray.ExitRequested += () => Application.Current.Shutdown();
            _tray.SettingsRequested += ShowSettings;
            _tray.ClipboardToggled += enabled => _window?.SetClipboardEnabled(enabled);

            _viewModel.NotifyRequested += (title, message) => _tray?.ShowNotification(title, message);
            _viewModel.PinRequested += pinned => _tray?.SetPinned(pinned);

            _window = new MainWindow(_viewModel);
            _window.SetClipboardEnabled(_settings.Current.ClipboardEnabled);
            _window.Show();
            _window.SetClickGuard(_settings.Current.ClickGuardEnabled);
            _miniTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1.3)
            };
            _miniTimer.Tick += (_, _) =>
            {
                try { SyncMiniPlayer(); }
                catch (Exception ex) { Infrastructure.Log.Write("Mini player update failed.", ex); }
            };
            _miniTimer.Start();
            SyncMiniPlayer();

            _ = _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Write("CorePilot failed to start.", ex);
            var deepest = ex;
            while (deepest.InnerException is not null) deepest = deepest.InnerException;
            MessageBox.Show(
                "CorePilot se nepodařilo spustit / CorePilot could not start.\n\n" +
                deepest.GetType().Name + ": " + deepest.Message +
                "\n\nZáznam chyby / Error log: " +
                Path.Combine(Infrastructure.Log.DataFolder, "nusalite.log"),
                "CorePilot", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settings!,
            clickGuardChanged: enabled => _window?.SetClickGuard(enabled),
            autoLyricsChanged: enabled => _window?.SetAutoLyricsEnabled(enabled),
            fullscreenChanged: enabled => _window?.SetFullscreenAutoHide(enabled),
            hotkeysChanged: enabled => _window?.SetGlobalHotkeysEnabled(enabled),
            guardGhostRefresh: () => _window?.RefreshGuardGhost(),
            clipboardChanged: enabled => _window?.SetClipboardEnabled(enabled),
            miniPlayerChanged: _ => SyncMiniPlayer());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SyncMiniPlayer()
    {
        if (_settings is null || _viewModel is null || _window is null) return;
        if (!_settings.Current.TaskbarWidget)
        {
            if (_miniPlayer is not null)
            {
                _miniPlayer.Close();
                _miniPlayer = null;
            }
            return;
        }

        _miniPlayer ??= new MiniPlayerWindow(
            _viewModel, () => _window.OpenView(Models.IslandView.Player));
        bool show = _viewModel.HasMedia && _window.IsVisible;
        if (show && !_miniPlayer.IsVisible)
        {
            _miniPlayer.Show();
            _miniPlayer.Position();
        }
        else if (!show && _miniPlayer.IsVisible)
        {
            _miniPlayer.Hide();
        }
    }

    public void Wake()
    {
        _window?.Dispatcher.Invoke(() =>
        {
            if (_window is not null)
            {
                if (!_window.IsVisible)
                {
                    _window.Show();
                }
                _window.WakeIsland();
            }
        });
    }

    public void Dispose()
    {
        _miniTimer?.Stop();
        _miniPlayer?.Close();
        _tray?.Dispose();
        _viewModel?.Dispose();
    }
}
