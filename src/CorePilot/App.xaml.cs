using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CorePilot;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += HandleDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                StartupDiagnostics.Log("Unhandled application exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupDiagnostics.Log("Unobserved background task", args.Exception);
            args.SetObserved();
        };
    }

    private void HandleDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        StartupDiagnostics.Log("Unhandled UI exception", args.Exception);
        args.Handled = true;
        try
        {
            MessageBox.Show(
                "CorePilot narazil na chybu. Podrobnosti najdeš v %LOCALAPPDATA%\\CorePilot\\logs\\startup.log.",
                "CorePilot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        Shutdown(1);
    }
}

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    public static void Log(string stage, Exception exception)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CorePilot", "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "startup.log");
            lock (Sync)
            {
                File.AppendAllText(file,
                    $"[{DateTimeOffset.Now:O}] {stage}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
                if (new FileInfo(file).Length > 2_000_000)
                {
                    var content = File.ReadAllText(file);
                    File.WriteAllText(file, content[^Math.Min(content.Length, 500_000)..], Encoding.UTF8);
                }
            }
        }
        catch { }
    }
}
