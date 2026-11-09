using System.Windows;
using CorePilot.Services;
namespace CorePilot;
public partial class WidgetWindow : Window
{
    private readonly Action _showIsland;
    private readonly Func<Task> _togglePlayback;
    public WidgetWindow(Action showIsland, Func<Task> togglePlayback)
    { InitializeComponent(); _showIsland = showIsland; _togglePlayback = togglePlayback; Position(); }
    private void Position()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 90;
        Top = screen.Bottom - Height - 8;
    }
    public void Update(MediaSnapshot media)
    {
        WidgetTitle.Text = media.Title; WidgetArtist.Text = media.Artist;
        WidgetArt.Source = media.Artwork; WidgetPlay.Content = media.Playing ? "Ⅱ" : "▶";
    }
    private void ShowIsland_Click(object sender, RoutedEventArgs e) => _showIsland();
    private async void Play_Click(object sender, RoutedEventArgs e)
    { try { await _togglePlayback(); } catch { /* player may not support transport */ } }
}
