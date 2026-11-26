using Microsoft.Win32;

namespace ClaudFlight.Core.Profiles;

/// <summary>Starting ClaudFlight when Windows starts: an entry under the current user's Run key
/// that launches it straight into the tray. Only ever touches its own value.</summary>
public sealed class StartupRegistration
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ClaudFlight";

    /// <summary>The command-line switch that starts the app hidden in the tray.</summary>
    public const string TrayArgument = "--tray";

    private readonly string _keyPath;

    /// <param name="keyPath">Under HKEY_CURRENT_USER. Defaults to the real Run key, or the
    /// CLAUDFLIGHT_STARTUP_KEY environment variable's key when set (for testing).</param>
    public StartupRegistration(string? keyPath = null) =>
        _keyPath = keyPath ?? Environment.GetEnvironmentVariable("CLAUDFLIGHT_STARTUP_KEY") ?? DefaultKeyPath;

    public static string CommandFor(string exePath) => $"\"{exePath}\" {TrayArgument}";

    /// <summary>The command Windows runs at sign-in, or null if there's no entry.</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(ValueName) as string;
        }
    }

    public bool IsEnabled => Command is not null;

    /// <summary>True if there's an entry but it starts a different copy of the app (e.g. it was
    /// moved) - Enable again to point it here.</summary>
    public bool PointsElsewhere(string exePath) => Command is { } command && !string.Equals(command, CommandFor(exePath), StringComparison.OrdinalIgnoreCase);

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
