namespace CorePilot.Core;

/// <summary>
/// Semantic status colours for the WPF UI. The status text is retained so that
/// colour is never the only way to understand the result.
/// </summary>
public static class UiStatusTone
{
    public const string Neutral = "Neutral";
    public const string Info = "Info";
    public const string Success = "Success";
    public const string Warning = "Warning";
    public const string Error = "Error";

    public static string ForMessage(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Neutral;

        var value = status.Trim().ToUpperInvariant();

        if (value.Contains("NOT READY") ||
            value.Contains("FAILED") ||
            value.Contains("BLOCKED") ||
            value.Contains("UNRESOLVED") ||
            value.Contains("UNSAFE") ||
            value.Contains("NOT SUPPORTED") ||
            value.Contains("COULD NOT") ||
            value.Contains("STOPPED SAFELY") ||
            value.Contains("TARGET HARDWARE REQUIRED"))
            return Error;

        // A ready result with warnings is amber, never green.
        if (value.Contains('⚠') ||
            value.Contains("WARNING") ||
            value.Contains("REVIEW") ||
            value.Contains("MANUAL") ||
            value.Contains("REQUIRED FIX") ||
            value.Contains("INSPECTION REQUIRED") ||
            value.Contains("NOT SELECTED") ||
            value.Contains("CANCEL") ||
            value.Contains("NOT AVAILABLE"))
            return Warning;

        if (value.StartsWith("READY", StringComparison.Ordinal) ||
            value.StartsWith("VERIFIED", StringComparison.Ordinal) ||
            value.StartsWith("USB READY", StringComparison.Ordinal) ||
            value.StartsWith("SYSTEM PREPARED", StringComparison.Ordinal) ||
            value.StartsWith("PREPARED", StringComparison.Ordinal) ||
            value.StartsWith("COMPATIBLE", StringComparison.Ordinal) ||
            value.StartsWith("SAFE", StringComparison.Ordinal) ||
            value.StartsWith("INSTALLABLE", StringComparison.Ordinal) ||
            value.Contains("SUCCESS"))
            return Success;

        if (value.Contains("PREPARING") ||
            value.Contains("DOWNLOADING") ||
            value.Contains("SCANNING") ||
            value.Contains("VERIFYING") ||
            value.Contains("REFRESHING") ||
            value.Contains("RUNNING"))
            return Info;

        return Neutral;
    }
}
