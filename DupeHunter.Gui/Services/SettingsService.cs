using System.IO;
using System.Text.Json;

namespace DupeHunter.Gui.Services;

/// <summary>The user preferences that survive between sessions.</summary>
public sealed class AppSettings
{
    public string? LastReportPath { get; set; }
    public double MinWastedMb { get; set; } = 1;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%\DupeHunter.</summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DupeHunter", "settings.json");

    /// <summary>The saved settings, or defaults when none exist or the file is unreadable.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or inaccessible settings just mean starting fresh.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to persist preferences is not worth interrupting the user over.
        }
    }
}
