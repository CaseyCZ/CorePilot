using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;

namespace CorePilot.App;

public partial class LogWindow : Window
{
    private readonly ActivityLogService _log;

    public LogWindow(ActivityLogService log)
    {
        InitializeComponent();
        _log = log;
        DataContext = log;

        _log.Entries.CollectionChanged += Entries_OnCollectionChanged;
        Closed += (_, _) =>
            _log.Entries.CollectionChanged -= Entries_OnCollectionChanged;
    }

    private void Entries_OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (_log.Entries.Count == 0)
            return;

        Dispatcher.BeginInvoke(() =>
            LogList.ScrollIntoView(_log.Entries[^1]));
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_log.LogFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { directory },
            UseShellExecute = true
        });
    }

    private void CopyPath_OnClick(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(_log.LogFilePath);

    private void ClearView_OnClick(object sender, RoutedEventArgs e) =>
        _log.ClearView();
}
