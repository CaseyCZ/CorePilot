using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace CorePilot.App;

public enum ActivityLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record ActivityLogEntry(
    DateTimeOffset Timestamp,
    ActivityLogLevel Level,
    string Area,
    string Message,
    string Detail)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string LevelText => Level.ToString().ToUpperInvariant();
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

public sealed class ActivityLogService : INotifyPropertyChanged
{
    private readonly object _fileGate = new();
    private string _currentStatus = "Ready";
    private string _currentArea = "App";
    private bool _isBusy;
    private int _errorCount;

    public ObservableCollection<ActivityLogEntry> Entries { get; } = [];

    public string LogFilePath { get; }

    public string CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus == value)
                return;
            _currentStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public string CurrentArea
    {
        get => _currentArea;
        private set
        {
            if (_currentArea == value)
                return;
            _currentArea = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public int ErrorCount
    {
        get => _errorCount;
        private set
        {
            if (_errorCount == value)
                return;
            _errorCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText =>
        IsBusy
            ? "RUNNING"
            : ErrorCount > 0
                ? $"READY · {ErrorCount} error{(ErrorCount == 1 ? "" : "s")}"
                : "READY";

    public ActivityLogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(appData, "CorePilot", "logs");
        Directory.CreateDirectory(directory);

        LogFilePath = Path.Combine(
            directory,
            $"CorePilot-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public void Start(string area, string message)
    {
        SetActivity(area, message, true);
        Append(ActivityLogLevel.Info, area, "START · " + message, "");
    }

    public void Progress(string area, string message)
    {
        SetActivity(area, message, true);
        Append(ActivityLogLevel.Info, area, message, "");
    }

    public void Success(string area, string message)
    {
        SetActivity(area, message, false);
        Append(ActivityLogLevel.Success, area, message, "");
    }

    public void Info(string area, string message) =>
        Append(ActivityLogLevel.Info, area, message, "");

    public void Warning(string area, string message)
    {
        SetActivity(area, message, false);
        Append(ActivityLogLevel.Warning, area, message, "");
    }

    public void Error(string area, string message, Exception? exception = null)
    {
        SetActivity(area, message, false);
        var detail = exception?.ToString() ?? "";
        Append(ActivityLogLevel.Error, area, message, detail);
    }

    public void ClearView()
    {
        RunOnUi(() => Entries.Clear());
        Info("Log", "Visible log entries cleared. Persistent session log was kept.");
    }

    private void SetActivity(string area, string status, bool busy)
    {
        RunOnUi(() =>
        {
            CurrentArea = area;
            CurrentStatus = status;
            IsBusy = busy;
        });
    }

    private void Append(
        ActivityLogLevel level,
        string area,
        string message,
        string detail)
    {
        var entry = new ActivityLogEntry(
            DateTimeOffset.Now,
            level,
            area,
            message,
            detail);

        WriteFile(entry);

        RunOnUi(() =>
        {
            Entries.Add(entry);

            while (Entries.Count > 5000)
                Entries.RemoveAt(0);

            if (level == ActivityLogLevel.Error)
                ErrorCount++;
        });
    }

    private void WriteFile(ActivityLogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Timestamp.ToString("O"))
            .Append(" [")
            .Append(entry.LevelText)
            .Append("] [")
            .Append(entry.Area)
            .Append("] ")
            .AppendLine(entry.Message);

        if (entry.HasDetail)
        {
            builder.AppendLine(entry.Detail);
            builder.AppendLine("---");
        }

        lock (_fileGate)
            File.AppendAllText(LogFilePath, builder.ToString(), Encoding.UTF8);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
