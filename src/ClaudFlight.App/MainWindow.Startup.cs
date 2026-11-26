using System.Windows;
using ClaudFlight.Core.Profiles;

namespace ClaudFlight.App;

// "Start with Windows": a Run entry that launches the app hidden in the tray (--tray), so mappings
// work from sign-in without opening the window. Toggled from the top bar or the tray menu.
public partial class MainWindow
{
    private readonly StartupRegistration _startup = new();
    private System.Windows.Forms.ToolStripMenuItem? _trayStartupItem;
    private bool _syncingStartup;

    /// <summary>Started with --tray (by the Run entry): stay in the tray until opened.</summary>
    private static bool StartedInTray =>
        Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));

    private void AddTrayStartupItem(System.Windows.Forms.ContextMenuStrip menu)
    {
        _trayStartupItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _trayStartupItem.CheckedChanged += (_, _) =>
        {
            if (!_syncingStartup) SetStartWithWindows(_trayStartupItem.Checked);
        };
        menu.Items.Add(_trayStartupItem);
    }

    // Checked/Unchecked rather than Click, so keyboard and screen-reader toggles count too.
    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingStartup && IsLoaded) SetStartWithWindows(StartWithWindowsCheck.IsChecked == true);
    }

    private void SetStartWithWindows(bool on)
    {
        try
        {
            if (on)
            {
                if (Environment.ProcessPath is not { } exePath) throw new InvalidOperationException("ClaudFlight couldn't tell where it's running from.");
                _startup.Enable(exePath);
            }
            else
            {
                _startup.Disable();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Couldn't change whether ClaudFlight starts with Windows:\n{ex.Message}", "Start with Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SyncStartupUi();
    }

    /// <summary>Shows the Run entry's real state in the checkbox and the tray menu.</summary>
    private void SyncStartupUi()
    {
        bool enabled;
        try { enabled = _startup.IsEnabled; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { enabled = false; }

        _syncingStartup = true;
        StartWithWindowsCheck.IsChecked = enabled;
        if (_trayStartupItem is not null) _trayStartupItem.Checked = enabled;
        _syncingStartup = false;

        var elsewhere = enabled && Environment.ProcessPath is { } exePath && _startup.PointsElsewhere(exePath);
        StartWithWindowsCheck.ToolTip = elsewhere
            ? "On, but it starts a copy of ClaudFlight from another folder. Untick and tick again to start this copy instead."
            : "Start ClaudFlight in the tray when you sign in to Windows, so your mappings work without opening it. Also in the tray icon's menu.";
    }
}
