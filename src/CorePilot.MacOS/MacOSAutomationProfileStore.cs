using System.Text.Json;

namespace CorePilot.MacOS;

public sealed class MacOSAutomationProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public async Task<string> SaveAsync(
        MacOSAutomationProfile profile,
        CancellationToken cancellationToken = default)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(appData, "CorePilot", "profiles");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"macos-{profile.TargetId}-latest.json");
        var json = JsonSerializer.Serialize(profile, Options);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }
}
