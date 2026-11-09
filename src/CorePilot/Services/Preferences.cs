using System.IO;
using System.Text.Json;
namespace CorePilot.Services;
public sealed class Preferences
{
    public bool ClipboardEnabled { get; set; } = false;
    public bool TaskbarWidget { get; set; } = true;
    public bool Animations { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    public bool ShowVolumeNotification { get; set; } = true;
    public bool ShowKeyNotification { get; set; } = true;
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorePilot", "settings.json");
    public static Preferences Load()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsFile)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
