using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CorePilot;

public partial class MainWindow
{
    private const int VkCapsLock = 0x14;
    private const int VkNumLock = 0x90;
    private DispatcherTimer? _lockTimer;
    private bool _wasCapsLock, _wasNumLock;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    private static bool KeyToggled(int key) => (GetKeyState(key) & 1) != 0;

    private void StartLockKeyWatcher()
    {
        _wasCapsLock = KeyToggled(VkCapsLock);
        _wasNumLock = KeyToggled(VkNumLock);
        _lockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _lockTimer.Tick += (_, _) =>
        {
            try
            {
                bool caps = KeyToggled(VkCapsLock);
                bool num = KeyToggled(VkNumLock);
                if (_viewModel.Settings.Current.ShowLockKeyNotifications && !_isFullscreenSuppressed)
                {
                    if (caps != _wasCapsLock)
                        _viewModel.ShowLockKeyStatus("Caps Lock", caps);
                    else if (num != _wasNumLock)
                        _viewModel.ShowLockKeyStatus("Num Lock", num);
                }
                _wasCapsLock = caps;
                _wasNumLock = num;
            }
            catch (Exception ex) { Infrastructure.Log.Write("Lock key indicator failed.", ex); }
        };
        _lockTimer.Start();
    }

    private void StopLockKeyWatcher()
    {
        _lockTimer?.Stop();
        _lockTimer = null;
    }
}
