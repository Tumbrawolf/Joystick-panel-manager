using System.Text.Json;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Profiles;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string ProfilesDirectory { get; }

    public ProfileStore(string? profilesDirectory = null)
    {
        ProfilesDirectory = profilesDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudFlight", "profiles");
        Directory.CreateDirectory(ProfilesDirectory);
    }

    public IEnumerable<string> ListProfileNames() =>
        Directory.EnumerateFiles(ProfilesDirectory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!);

    public Profile Load(string name)
    {
        var json = File.ReadAllText(PathFor(name));
        return JsonSerializer.Deserialize<Profile>(json, JsonOptions)
            ?? throw new InvalidDataException($"Profile '{name}' could not be parsed.");
    }

    public void Save(Profile profile)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(PathFor(profile.Name), json);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name) => Path.Combine(ProfilesDirectory, $"{SanitizeFileName(name)}.json");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
