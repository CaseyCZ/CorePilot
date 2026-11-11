using System.Windows;
using CorePilot.ViewModels;

namespace CorePilot.Views;

public partial class MiniPlayerWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Action _openPlayer;

    public MiniPlayerWindow(MainViewModel vm, Action openPlayer)
    {
        _vm = vm;
        _openPlayer = openPlayer;
        DataContext = vm;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => Position();

    public void Position()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 16);
        Top = Math.Max(area.Top, area.Bottom - Height - 10);
    }

    private void TogglePlayback_Click(object sender, RoutedEventArgs e) => _vm.TogglePlay();
    private void OpenPlayer_Click(object sender, RoutedEventArgs e) => _openPlayer();
}
