using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CorePilot.App;

public partial class LogWindow : Window
{
    private readonly ActivityLogService _log;
    private readonly ICollectionView _view;
    private string _levelFilter = "All";
    private string _searchText = "";

    public LogWindow(ActivityLogService log)
    {
        InitializeComponent();
        _log = log;
        DataContext = log;

        _view = CollectionViewSource.GetDefaultView(_log.Entries);
        _view.Filter = FilterEntry;
        LogList.ItemsSource = _view;

        _log.Entries.CollectionChanged += Entries_OnCollectionChanged;
        Closed += (_, _) =>
            _log.Entries.CollectionChanged -= Entries_OnCollectionChanged;

        UpdateCount();
    }

    private bool FilterEntry(object item)
    {
        if (item is not ActivityLogEntry entry)
            return false;

        if (!_levelFilter.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !entry.LevelText.Equals(_levelFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(_searchText))
            return true;

        var search = _searchText.Trim();

        return Contains(entry.TimeText, search) ||
               Contains(entry.LevelText, search) ||
               Contains(entry.Area, search) ||
               Contains(entry.Message, search) ||
               Contains(entry.Detail, search);
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void LevelFilterCombo_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LevelFilterCombo.SelectedItem is ComboBoxItem item)
            _levelFilter = item.Tag?.ToString() ?? "All";

        RefreshView();
    }

    private void SearchBox_OnTextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text ?? "";
        RefreshView();
    }

    private void ClearFilters_OnClick(object sender, RoutedEventArgs e)
    {
        LevelFilterCombo.SelectedIndex = 0;
        SearchBox.Text = "";
        _levelFilter = "All";
        _searchText = "";
        RefreshView();
    }

    private void RefreshView()
    {
        if (_view is null)
            return;

        _view.Refresh();
        UpdateCount();
        ScrollToNewestVisible();
    }

    private void Entries_OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _view.Refresh();
            UpdateCount();
            ScrollToNewestVisible();
        });
    }

    public void FocusEntry(ActivityLogEntry entry)
    {
        LevelFilterCombo.SelectedIndex = 1;
        SearchBox.Text = "";
        _levelFilter = "Error";
        _searchText = "";
        RefreshView();

        if (!_view.Cast<object>().Contains(entry))
        {
            LevelFilterCombo.SelectedIndex = 0;
            _levelFilter = "All";
            RefreshView();
        }

        LogList.SelectedItem = entry;
        LogList.ScrollIntoView(entry);
        LogList.Focus();
    }

    private void ScrollToNewestVisible()
    {
        var newest = _view
            .Cast<object>()
            .OfType<ActivityLogEntry>()
            .LastOrDefault();

        if (newest is not null)
            LogList.ScrollIntoView(newest);
    }

    private void UpdateCount()
    {
        var visible = _view.Cast<object>().Count();
        CountText.Text = $"{visible} / {_log.Entries.Count}";
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

    private void LogList_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        CopySelectedButton.IsEnabled =
            LogList.SelectedItem is ActivityLogEntry;

    private void CopySelected_OnClick(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not ActivityLogEntry entry)
            return;

        var text =
            $"{entry.Timestamp:O} [{entry.LevelText}] [{entry.Area}] {entry.Message}";

        if (entry.HasDetail)
            text += Environment.NewLine + entry.Detail;

        Clipboard.SetText(text);
    }

    private void ClearView_OnClick(object sender, RoutedEventArgs e)
    {
        _log.ClearView();
        RefreshView();
    }
}
