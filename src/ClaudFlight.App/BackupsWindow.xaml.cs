using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ClaudFlight.Core.Profiles;

namespace ClaudFlight.App;

/// <summary>Lists one profile's automatic backups (newest first) and puts one back.</summary>
public partial class BackupsWindow : Window
{
    private readonly ProfileStore _store;
    private readonly string _profileName;

    /// <summary>The backup the user chose to restore, or null if they closed the window.</summary>
    public ProfileBackup? ChosenBackup { get; private set; }

    public BackupsWindow(Window owner, ProfileStore store, string profileName)
    {
        InitializeComponent();
        Owner = owner;
        _store = store;
        _profileName = profileName;
        Title = $"Backups of {profileName}";
        IntroText.Text = $"Earlier versions of '{profileName}', saved automatically while you work (up to 30, the newest first). Pick one and click Restore to go back to it.";

        var backups = store.ListBackups(profileName);
        BackupsList.ItemsSource = backups;
        NoBackupsText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackupsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = BackupsList.SelectedItem is ProfileBackup;

    private void BackupsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BackupsList.SelectedItem is ProfileBackup) Restore_Click(sender, e);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsList.SelectedItem is not ProfileBackup backup) return;
        var answer = MessageBox.Show(this,
            $"Put '{_profileName}' back the way it was on {backup.Time:dddd d MMMM, HH:mm}?\n\nWhat it is now is backed up first, so you can come back to it from this list.",
            "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        ChosenBackup = backup;
        DialogResult = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = BackupsList.SelectedItem is ProfileBackup backup ? Path.GetDirectoryName(backup.Path)! : _store.BackupsDirectory;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
