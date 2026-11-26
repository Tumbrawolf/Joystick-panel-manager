using System.Text.Json;

namespace ClaudFlight.Core.Profiles;

public sealed class AppSettings
{
    public string? LastProfileName { get; set; }

    /// <summary>Profiles to switch to automatically while a given game is running.</summary>
    public List<GameProfileEntry> GameProfiles { get; set; } = [];
}

/// <summary>"While ProcessName (e.g. DCS.exe -&gt; "DCS") is running, use ProfileName."</summary>
public sealed class GameProfileEntry
{
    public required string ProcessName { get; set; }
    public required string ProfileName { get; set; }
}

/// <summary>Tiny persisted app-level settings (distinct from a Profile): which profile to
/// auto-load next time the app starts, and which profile goes with which game.</summary>
public sealed class AppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudFlight");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
