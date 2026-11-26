using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ClaudFlight.App;

/// <summary>Automatic profile switching: while a game listed in the settings is running, its
/// profile is active; when the game closes, the profile you had before comes back.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _gameWatchTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>The profile that was active before a game's profile was switched in, or null
    /// when no automatic switch is in effect.</summary>
    private string? _profileBeforeGame;

    /// <summary>The game whose profile is active (or who had it active already), or null.</summary>
    private string? _activeGameProcess;

    /// <summary>True while the list of running programs is being read in the background.</summary>
    private bool _checkingGames;

    private async void CheckGameProfiles()
    {
        if (_checkingGames) return;
        if (_settings.GameProfiles.Count == 0 && _activeGameProcess is null) return;

        // Listing every running program takes a moment on a busy PC; do it off the UI thread so
        // the window and live readings don't stutter every few seconds.
        _checkingGames = true;
        HashSet<string> running;
        try
        {
            running = await Task.Run(RunningProgramNames);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        finally
        {
            _checkingGames = false;
        }

        var before = (_activeGameProcess, _profileBeforeGame);
        CheckGameProfilesCore(running);
        if ((_activeGameProcess, _profileBeforeGame) != before) UpdateGameStatus();
    }

    private static HashSet<string> RunningProgramNames()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try { running.Add(process.ProcessName); }
            catch (InvalidOperationException) { } // exited while being listed
            finally { process.Dispose(); }
        }
        return running;
    }

    private void CheckGameProfilesCore(HashSet<string> running)
    {
        if (_settings.GameProfiles.Count == 0 && _activeGameProcess is null) return;

        if (_activeGameProcess is not null)
        {
            if (running.Contains(_activeGameProcess)) return;

            // The game closed: put back whatever was active before it.
            var game = _activeGameProcess;
            var previous = _profileBeforeGame;
            _activeGameProcess = null;
            _profileBeforeGame = null;
            if (previous is not null && previous != _currentProfile.Name && _profileStore.Exists(previous))
            {
                SwitchToProfile(previous, $"{game} closed - back to profile '{previous}'.");
            }
            return;
        }

        foreach (var entry in _settings.GameProfiles)
        {
            if (!running.Contains(entry.ProcessName) || !_profileStore.Exists(entry.ProfileName)) continue;

            _activeGameProcess = entry.ProcessName;
            if (string.Equals(entry.ProfileName, _currentProfile.Name, StringComparison.OrdinalIgnoreCase)) return; // already on it

            _profileBeforeGame = _currentProfile.Name;
            SwitchToProfile(entry.ProfileName, $"{entry.ProcessName} started - switched to profile '{entry.ProfileName}'.");
            return;
        }
    }

    private void SwitchToProfile(string name, string message)
    {
        AutoSaveCurrentProfile();
        try
        {
            ApplyLoadedProfile(_profileStore.Load(name));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return;
        }
        _trayIcon?.ShowBalloonTip(3000, "ClaudFlight", message, System.Windows.Forms.ToolTipIcon.Info);
    }
}
