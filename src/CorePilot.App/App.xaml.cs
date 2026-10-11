using System.Windows;

namespace CorePilot.App;

public partial class App : Application
{
    public static ActivityLogService Log { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(
                "Unhandled",
                "Unhandled UI exception. CorePilot may need to close.",
                args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            Log.Error(
                "Unhandled",
                args.IsTerminating
                    ? "Unhandled application exception; process is terminating."
                    : "Unhandled application exception.",
                exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(
                "Background task",
                "Unobserved background task exception.",
                args.Exception);
            args.SetObserved();
        };

        Log.Info("App", $"CorePilot started · {Environment.OSVersion} · .NET {Environment.Version}");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App", $"CorePilot exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }
}
