using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;
using Microsoft.Win32;

namespace ClaudFlight.App;

/// <summary>One saved profile in the Profiles tab's list.</summary>
public sealed record ProfileListItem(string Name, bool IsActive, string UsedBy)
{
    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() =>
        Name + (IsActive ? " (active)" : "") + (string.IsNullOrEmpty(UsedBy) ? "" : $", {UsedBy}");
}

/// <summary>One "while this game runs, use this profile" entry in the Profiles tab.</summary>
public sealed record GameEntryRow(GameProfileEntry Entry, string ProfileText, bool ProfileMissing)
{
    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Entry.ProcessName} uses {ProfileText}";

    // Each rebuilt row is a new row: were it "equal" to the one it replaces, the accessibility tree
    // would keep the old row's entry, pointing a screen reader at its discarded Remove button and
    // checkbox.
    public bool Equals(GameEntryRow? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>The Profiles tab: every saved profile (switch, new, duplicate, rename, delete,
/// export, import) and the per-game profiles, plus the quick-switch dropdown in the bottom bar.</summary>
public partial class MainWindow
{
    public ObservableCollection<ProfileListItem> ProfileItems { get; } = [];
    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<GameEntryRow> GameEntryRows { get; } = [];

    private bool _refreshingProfiles;

    /// <summary>Re-reads the saved profiles and refreshes everything that lists them: the
    /// Profiles tab, the bottom bar's dropdown and the games list.</summary>
    private void RefreshProfileList()
    {
        _refreshingProfiles = true;
        var selected = (ProfilesList.SelectedItem as ProfileListItem)?.Name ?? _currentProfile.Name;

        var names = _profileStore.ListProfileNames().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        ProfileNames.Clear();
        foreach (var name in names) ProfileNames.Add(name);

        ProfileItems.Clear();
        foreach (var name in names)
        {
            var games = _settings.GameProfiles.Where(g => _profileStore.IsSameProfile(g.ProfileName, name)).Select(g => g.ProcessName).ToList();
            ProfileItems.Add(new ProfileListItem(name, _profileStore.IsSameProfile(name, _currentProfile.Name),
                games.Count == 0 ? "" : "used for " + string.Join(", ", games)));
        }
        ProfilesList.SelectedItem = ProfileItems.FirstOrDefault(p => p.Name == selected) ?? ProfileItems.FirstOrDefault(p => p.IsActive);

        QuickProfileCombo.SelectedItem = ProfileNames.FirstOrDefault(n => _profileStore.IsSameProfile(n, _currentProfile.Name));
        if (GameProfileCombo.SelectedIndex < 0 && ProfileNames.Count > 0) GameProfileCombo.SelectedIndex = 0;

        GameEntryRows.Clear();
        foreach (var entry in _settings.GameProfiles)
        {
            var exists = _profileStore.Exists(entry.ProfileName);
            GameEntryRows.Add(new GameEntryRow(entry, exists ? entry.ProfileName : $"{entry.ProfileName} (missing - pick another)", !exists));
        }
        NoGameEntriesText.Visibility = GameEntryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _refreshingProfiles = false;
        UpdateGameStatus();
    }

    private string? SelectedProfileName => (ProfilesList.SelectedItem as ProfileListItem)?.Name;

    /// <summary>Saves the current profile, then makes another one active (a manual switch, so it
    /// also stays active next launch and cancels any game's automatic switch-back).</summary>
    private void SwitchProfileManually(string name)
    {
        if (_profileStore.IsSameProfile(name, _currentProfile.Name)) return;
        AutoSaveCurrentProfile();
        try
        {
            ApplyLoadedProfile(_profileStore.Load(name));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't load profile '{name}':\n{ex.Message}", "Profiles", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshProfileList();
            return;
        }
        _profileBeforeGame = null;
        RememberCurrentProfile();
        RefreshProfileList();
    }

    private void QuickProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingProfiles && QuickProfileCombo.SelectedItem is string name) SwitchProfileManually(name);
    }

    // ---- Profiles list ----

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ProfileRenameBox.Text = SelectedProfileName ?? "";
        var isActive = (ProfilesList.SelectedItem as ProfileListItem)?.IsActive == true;
        SwitchToProfileButton.IsEnabled = SelectedProfileName is not null && !isActive;
        DeleteProfileButton.IsEnabled = SelectedProfileName is not null && !isActive;
    }

    private void ProfilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedProfileName is { } name) SwitchProfileManually(name);
    }

    private void SwitchToProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileName is { } name) SwitchProfileManually(name);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        AutoSaveCurrentProfile();
        // Saved straight away below, so it needs a name no existing profile has.
        ApplyLoadedProfile(new Profile { Name = _profileStore.UniqueName("New profile") });
        AutoSaveCurrentProfile();
        _profileBeforeGame = null;
        RememberCurrentProfile();
        RefreshProfileList();
        ProfilesList.SelectedItem = ProfileItems.FirstOrDefault(p => p.IsActive);
        ProfileRenameBox.Focus();
        ProfileRenameBox.SelectAll();
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileName is not { } name) return;
        if (_profileStore.IsSameProfile(name, _currentProfile.Name)) AutoSaveCurrentProfile();

        try
        {
            var copy = _profileStore.Load(name);
            copy.Name = _profileStore.UniqueName($"{name} copy");
            _profileStore.Save(copy);
            RefreshProfileList();
            ProfilesList.SelectedItem = ProfileItems.FirstOrDefault(p => p.Name == copy.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't copy the profile:\n{ex.Message}", "Duplicate profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProfileRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RenameProfile_Click(sender, e);
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileName is not { } oldName) return;
        var newName = ProfileRenameBox.Text.Trim();
        if (newName == oldName) return;
        if (newName.Length == 0)
        {
            MessageBox.Show(this, "Type a name for the profile first.", "Rename profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_profileStore.IsSameProfile(oldName, newName) && _profileStore.Exists(newName))
        {
            MessageBox.Show(this, $"There's already a profile called '{newName}'.", "Rename profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isActive = _profileStore.IsSameProfile(oldName, _currentProfile.Name);
        try
        {
            if (isActive) AutoSaveCurrentProfile();
            _profileStore.Rename(oldName, newName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't rename the profile:\n{ex.Message}", "Rename profile", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (isActive)
        {
            _currentProfile.Name = newName;
            _lastSavedJson = _profileStore.Serialize(_currentProfile);
        }
        foreach (var entry in _settings.GameProfiles.Where(g => _profileStore.IsSameProfile(g.ProfileName, oldName))) entry.ProfileName = newName;
        if (_profileBeforeGame is not null && _profileStore.IsSameProfile(_profileBeforeGame, oldName)) _profileBeforeGame = newName;
        RememberCurrentProfile();

        RefreshProfileList();
        ProfilesList.SelectedItem = ProfileItems.FirstOrDefault(p => p.Name == newName);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileName is not { } name || _profileStore.IsSameProfile(name, _currentProfile.Name)) return;

        var games = _settings.GameProfiles.Where(g => _profileStore.IsSameProfile(g.ProfileName, name)).ToList();
        var message = $"Delete profile '{name}' and its backups? This can't be undone (Export it first if you might want it back).";
        if (games.Count > 0) message += $"\n\nIt's also removed from the games that use it: {string.Join(", ", games.Select(g => g.ProcessName))}.";
        if (MessageBox.Show(this, message, "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            _profileStore.Delete(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't delete the profile:\n{ex.Message}", "Delete profile", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        foreach (var game in games) _settings.GameProfiles.Remove(game);
        if (_profileBeforeGame is not null && _profileStore.IsSameProfile(_profileBeforeGame, name)) _profileBeforeGame = null;
        RememberCurrentProfile();
        RefreshProfileList();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileName is not { } name) return;

        Profile profile;
        if (_profileStore.IsSameProfile(name, _currentProfile.Name))
        {
            HarvestInputLabels();
            profile = _currentProfile;
        }
        else
        {
            try { profile = _profileStore.Load(name); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                MessageBox.Show(this, $"Couldn't read the profile:\n{ex.Message}", "Export profile", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export profile",
            Filter = "ClaudFlight profile (*.json)|*.json",
            FileName = profile.Name,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _profileStore.SaveToFile(profile, dialog.FileName);
            MessageBox.Show(this, $"Exported profile '{profile.Name}' to:\n{dialog.FileName}", "Export profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't export the profile:\n{ex.Message}", "Export profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Adds a profile from a .json file anywhere to the list (without switching to it).</summary>
    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import profile",
            Filter = "ClaudFlight profile (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        Profile imported;
        try
        {
            imported = _profileStore.LoadFromFile(dialog.FileName);
            // Never overwrite a saved profile that happens to share its name.
            if (string.IsNullOrWhiteSpace(imported.Name)) imported.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
            if (_profileStore.Exists(imported.Name)) imported.Name = _profileStore.UniqueName($"{imported.Name} (imported)");
            _profileStore.Save(imported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't import that file as a profile:\n{ex.Message}", "Import profile", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshProfileList();
        ProfilesList.SelectedItem = ProfileItems.FirstOrDefault(p => p.Name == imported.Name);
        if (MessageBox.Show(this, $"Imported profile '{imported.Name}'. Switch to it now?", "Import profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            SwitchProfileManually(imported.Name);
        }
    }

    // ---- Profiles for games ----

    /// <summary>Lists programs that have a window (the likely games) for the "Game" dropdown.</summary>
    private bool _listingPrograms;

    /// <summary>Fills the game picker with programs that have a window. Listing them takes a moment
    /// on a busy PC, so it's done in the background; the picker keeps whatever was typed meanwhile.</summary>
    private async void RefreshRunningPrograms()
    {
        if (_listingPrograms) return;
        _listingPrograms = true;
        SortedSet<string> running;
        try
        {
            running = await Task.Run(ProgramsWithWindows);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        finally
        {
            _listingPrograms = false;
        }

        var typed = GameProcessCombo.Text;
        GameProcessCombo.ItemsSource = running;
        GameProcessCombo.Text = typed;
    }

    private static SortedSet<string> ProgramsWithWindows()
    {
        var running = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero) running.Add(process.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // exited while we looked
            }
            finally
            {
                process.Dispose();
            }
        }
        return running;
    }

    private void RefreshRunningPrograms_Click(object sender, RoutedEventArgs e) => RefreshRunningPrograms();

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Pick the game's program", Filter = "Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) GameProcessCombo.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void AddGameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = GameProcessCombo.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || GameProfileCombo.SelectedItem is not string profile)
        {
            MessageBox.Show(this, "Pick (or type) a game and a profile first.", "Profiles for games", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.GameProfiles.RemoveAll(entry => string.Equals(entry.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        _settings.GameProfiles.Add(new GameProfileEntry { ProcessName = name, ProfileName = profile, KeyboardKeysOn = GameKeysCheck.IsChecked == true });
        GameKeysCheck.IsChecked = false;
        GameProcessCombo.Text = "";
        RememberCurrentProfile(); // saves the settings
        RefreshProfileList();
        CheckGameProfiles();
    }

    /// <summary>A game's "Keys on" box: saved, and applied straight away if that game is running.</summary>
    private void GameKeys_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsLoaded: true, DataContext: GameEntryRow row } box) return;
        row.Entry.KeyboardKeysOn = box.IsChecked == true;
        RememberCurrentProfile(); // saves the settings
        if (_activeGameProcess is { } game && string.Equals(game, row.Entry.ProcessName, StringComparison.OrdinalIgnoreCase)) ApplyGameKeys(row.Entry);
    }

    private void RemoveGameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameEntryRow row }) return;
        _settings.GameProfiles.Remove(row.Entry);
        RememberCurrentProfile();
        RefreshProfileList();
    }

    /// <summary>The bottom bar's and Profiles tab's note of which game's profile is in use.</summary>
    private void UpdateGameStatus()
    {
        var text = _activeGameProcess is null
            ? _settings.GameProfiles.Count == 0 ? "" : "No listed game is running."
            : _profileBeforeGame is not null
                ? $"{_activeGameProcess} is running, so its profile is in use. '{_profileBeforeGame}' comes back when it closes."
                : $"{_activeGameProcess} is running (its profile was already in use).";
        GameStatusText.Text = text;
        GameStatusBarText.Text = _activeGameProcess is null ? "" : $"{_activeGameProcess} running";
    }
}
