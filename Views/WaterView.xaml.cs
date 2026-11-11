using System.Windows;
using System.Windows.Controls;
using CorePilot.ViewModels;

namespace CorePilot.Views;

public partial class WaterView : UserControl
{
    public WaterView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Add_Click(object sender, RoutedEventArgs e) => Vm?.AddWater(250);

    private void Reset_Click(object sender, RoutedEventArgs e) => Vm?.ResetWater();
}
