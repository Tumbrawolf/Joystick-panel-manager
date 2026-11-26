using System.Globalization;
using System.Text.Json;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Profiles;

/// <summary>One saved copy of a profile as it was at Time.</summary>
public sealed record ProfileBackup(string Path, DateTime Time, long Bytes)
{
    public override string ToString() => $"{Time:ddd d MMM yyyy, HH:mm}  ({Math.Max(1, Bytes / 1024)} KB)";
}

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

    /// <summary>A profile from JSON Serialize wrote (e.g. an earlier version kept for undo).</summary>
    public Profile Deserialize(string json) =>
        JsonSerializer.Deserialize<Profile>(json, JsonOptions) ?? throw new InvalidDataException("The profile could not be parsed.");

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
        File.Delete(PathFor(oldName)); // just the file - Delete would take the backups with it

        // Its backups go with it (merged in, should the new name have a folder already).
        var oldBackups = BackupFolderFor(oldName);
        var newBackups = BackupFolderFor(newName);
        if (!Directory.Exists(oldBackups) || string.Equals(oldBackups, newBackups, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(newBackups))
        {
            Directory.Move(oldBackups, newBackups);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(oldBackups, "*.json"))
        {
            var target = Path.Combine(newBackups, Path.GetFileName(file));
            if (!File.Exists(target)) File.Move(file, target);
        }
        Directory.Delete(oldBackups, recursive: true);
    }

    // ---- Backups: earlier versions of each profile, kept in Backups\<name>\ ----

    private const string BackupTimeFormat = "yyyy-MM-dd HH-mm-ss";

    public string BackupsDirectory => Path.Combine(ProfilesDirectory, "Backups");

    /// <summary>Copies the profile's file as it is now into its backups, unless the newest backup is
    /// less than minimumGap old (so steady autosaving doesn't fill the folder with near-copies), then
    /// drops the oldest beyond keep. Returns true if it made one.</summary>
    public bool BackUpIfDue(string name, TimeSpan minimumGap, int keep = 30, DateTime? now = null)
    {
        var source = PathFor(name);
        if (!File.Exists(source)) return false;
        var time = now ?? DateTime.Now;
        var existing = ListBackups(name);
        // A newest backup "in the future" means the clock went back (e.g. daylight saving ended):
        // don't wait for real time to catch up with it.
        if (existing.Count > 0 && time >= existing[0].Time && time - existing[0].Time < minimumGap) return false;

        var folder = BackupFolderFor(name);
        Directory.CreateDirectory(folder);
        File.Copy(source, Path.Combine(folder, time.ToString(BackupTimeFormat, CultureInfo.InvariantCulture) + ".json"), overwrite: true);
        foreach (var old in ListBackups(name).Skip(keep)) File.Delete(old.Path);
        return true;
    }

    /// <summary>A profile's backups, newest first.</summary>
    public IReadOnlyList<ProfileBackup> ListBackups(string name)
    {
        var folder = BackupFolderFor(name);
        if (!Directory.Exists(folder)) return [];
        var backups = new List<ProfileBackup>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), BackupTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                backups.Add(new ProfileBackup(file, time, new FileInfo(file).Length));
            }
        }
        return backups.OrderByDescending(b => b.Time).ToList();
    }

    /// <summary>Puts a backup back as the named profile's saved file - after backing up what's
    /// there now, so restoring can itself be undone from the list.</summary>
    public Profile RestoreBackup(string name, ProfileBackup backup)
    {
        var profile = LoadFromFile(backup.Path);
        profile.Name = name;
        BackUpIfDue(name, TimeSpan.Zero);
        Save(profile);
        return profile;
    }

    private string BackupFolderFor(string name) => Path.Combine(BackupsDirectory, SanitizeFileName(name));

    /// <summary>True if both names map to the same profile file (e.g. they differ only in capitals).</summary>
    public bool IsSameProfile(string a, string b) => string.Equals(PathFor(a), PathFor(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Deletes the profile and its backups, so a new profile given the same name later
    /// doesn't inherit them.</summary>
    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
        var backups = BackupFolderFor(name);
        if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
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
