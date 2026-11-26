using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;

namespace ClaudFlight.App;

// Undo/redo for the current profile, and its automatic backups. Each autosave that changes the
// profile is one undo step (autosave runs every couple of seconds, so a burst of edits is one step).
public partial class MainWindow
{
    private const int UndoLimit = 50;
    private static readonly TimeSpan BackupGap = TimeSpan.FromMinutes(10);

    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];

    /// <summary>The profile the undo history belongs to; switching profile starts a fresh one.</summary>
    private Profile? _historyProfile;

    /// <summary>True while undo/redo saves its own result, so that save isn't a new step.</summary>
    private bool _savingHistoryStep;

    /// <summary>Called by autosave just before it writes a changed profile: the version on disk
    /// becomes the newest undo step, and gets backed up if the last backup is old enough.</summary>
    private void BeforeProfileSaved(bool isNew)
    {
        if (!isNew)
        {
            try { _profileStore.BackUpIfDue(_currentProfile.Name, BackupGap); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }

        if (_savingHistoryStep) return;
        if (!ReferenceEquals(_historyProfile, _currentProfile))
        {
            _historyProfile = _currentProfile;
            _undo.Clear();
            _redo.Clear();
        }
        else if (_lastSavedJson is { } previous)
        {
            _undo.Add(previous);
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
            _redo.Clear();
        }
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0 && ReferenceEquals(_historyProfile, _currentProfile);
        RedoButton.IsEnabled = _redo.Count > 0 && ReferenceEquals(_historyProfile, _currentProfile);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => StepHistory(_undo, _redo);

    private void Redo_Click(object sender, RoutedEventArgs e) => StepHistory(_redo, _undo);

    /// <summary>Puts back the newest version from one stack, keeping what it replaces on the other.</summary>
    private void StepHistory(List<string> from, List<string> to)
    {
        AutoSaveCurrentProfile(); // anything changed since the last autosave becomes its own step first
        if (from.Count == 0 || !ReferenceEquals(_historyProfile, _currentProfile) || _lastSavedJson is null) return;

        Profile restored;
        try
        {
            restored = _profileStore.Deserialize(from[^1]);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            from.RemoveAt(from.Count - 1);
            UpdateUndoButtons();
            return;
        }
        from.RemoveAt(from.Count - 1);
        to.Add(_lastSavedJson);
        restored.Name = _currentProfile.Name; // in case it was renamed since

        ApplyLoadedProfile(restored);
        _historyProfile = restored;
        _savingHistoryStep = true;
        try { AutoSaveCurrentProfile(); }
        finally { _savingHistoryStep = false; }
        UpdateUndoButtons();
    }

    /// <summary>Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) undo and redo profile changes - except while
    /// typing in a text box, which keeps its own undo.</summary>
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers is not (ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))) return;
        if (Keyboard.FocusedElement is TextBoxBase) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Z && !shift) Undo_Click(sender, e);
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) Redo_Click(sender, e);
        else return;
        e.Handled = true;
    }

    private void ProfileBackups_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfileName is not { } name) return;
        var isActive = _profileStore.IsSameProfile(name, _currentProfile.Name);
        if (isActive) AutoSaveCurrentProfile();

        var window = new BackupsWindow(this, _profileStore, name);
        if (window.ShowDialog() != true || window.ChosenBackup is not { } backup) return;

        try
        {
            var restored = _profileStore.RestoreBackup(name, backup);
            if (isActive)
            {
                ApplyLoadedProfile(restored);
                _lastSavedJson = null; // written below as it is now, without an undo step
                _savingHistoryStep = true;
                try { AutoSaveCurrentProfile(); }
                finally { _savingHistoryStep = false; }
            }
            RefreshProfileList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't restore the backup:\n{ex.Message}", "Restore backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
