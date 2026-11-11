using System.Windows;
using System.Windows.Controls;
using CorePilot.Services;

namespace CorePilot.Views;

public partial class ClipboardView : UserControl
{
    private readonly ClipboardHistoryService _history;

    public ClipboardView(ClipboardHistoryService history)
    {
        _history = history;
        InitializeComponent();
        DataContext = _history;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _history.Clear();
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardHistoryItem item })
            _history.Restore(item);
    }
}
