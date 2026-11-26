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

    /// <summary>Loads a saved profile. Its name is the one it's listed and saved under, whatever
    /// the file itself says (a file copied or renamed in Explorer keeps its old name inside), so
    /// autosave writes back to the same file.</summary>
    public Profile Load(string name)
    {
        var json = File.ReadAllText(PathFor(name));
        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions)
            ?? throw new InvalidDataException($"Profile '{name}' could not be parsed.");
        if (!IsSameProfile(profile.Name ?? "", name)) profile.Name = name;
        return profile;
    }

    public void Save(Profile profile) => SaveSerialized(profile.Name, Serialize(profile));

    /// <summary>The exact JSON Save would write - lets autosave compare against what's on disk
    /// and skip writing when nothing changed.</summary>
    public string Serialize(Profile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public void SaveSerialized(string name, string json) => File.WriteAllText(PathFor(name), json);

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>"name", or "name 2", "name 3"... - the first that isn't already a saved profile.</summary>
    public string UniqueName(string name)
    {
        if (!Exists(name)) return name;
        var number = 2;
        while (Exists($"{name} {number}")) number++;
        return $"{name} {number}";
    }

    /// <summary>Renames a saved profile (its file and the name stored inside it). A change of
    /// capitals only is fine too - on Windows that's the same file, so it's rewritten in place
    /// rather than deleted afterward.</summary>
    public void Rename(string oldName, string newName)
    {
        var profile = Load(oldName);
        profile.Name = newName;
        var samePath = string.Equals(PathFor(oldName), PathFor(newName), StringComparison.OrdinalIgnoreCase);
        if (samePath)
        {
            // Rename the file in place to the new spelling, then update the name inside it - the
            // profile is never missing from disk, even if the write fails.
            var json = Serialize(profile);
            File.Move(PathFor(oldName), PathFor(newName));
            SaveSerialized(newName, json);
            return;
        }
        Save(profile);
        Delete(oldName);
    }

    /// <summary>True if both names map to the same profile file (e.g. they differ only in capitals).</summary>
    public bool IsSameProfile(string a, string b) => string.Equals(PathFor(a), PathFor(b), StringComparison.OrdinalIgnoreCase);

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Loads a profile from an arbitrary file path (as opposed to Load(name), which only
    /// looks inside ProfilesDirectory) - for importing a profile someone shared, or restoring a
    /// backup from anywhere on disk.</summary>
    public Profile LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Profile>(json, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' could not be parsed as a profile.");
    }

    /// <summary>Saves a profile to an arbitrary file path (as opposed to Save(profile), which only
    /// writes inside ProfilesDirectory) - for exporting a profile to share or back up.</summary>
    public void SaveToFile(Profile profile, string path)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
    }

    private string PathFor(string name) => Path.Combine(ProfilesDirectory, $"{SanitizeFileName(name)}.json");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
