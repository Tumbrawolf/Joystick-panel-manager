using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App;

/// <summary>Sets up the profile's modes: each a name and the switch (position) that picks it.</summary>
public partial class ModesWindow : Window
{
    private sealed record ModeRow(ModeDefinition Mode, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly MainWindow _host;
    private ShiftControl? _picked;
    private readonly bool _loading;

    internal ModesWindow(MainWindow host)
    {
        _loading = true;
        InitializeComponent();
        Owner = host;
        _host = host;
        AnnounceCheck.IsChecked = host.AnnounceModes;
        _loading = false;
        RefreshList();
    }

    private ModeDefinition? Selected => (ModesList.SelectedItem as ModeRow)?.Mode;

    private void RefreshList(ModeDefinition? select = null)
    {
        var rows = _host.Modes
            .Select(m => new ModeRow(m, $"{m.Name}  -  {_host.ModeSwitchName(m.SelectedBy)}, {_host.MappingsInMode(m.Name)} mapping(s)"))
            .ToList();
        ModesList.ItemsSource = rows;
        ModesList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Mode, select));
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var has = Selected is not null;
        ChangeSwitchButton.IsEnabled = has;
        RenameButton.IsEnabled = has;
        RemoveModeButton.IsEnabled = has;
    }

    private void ModesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshButtons();

    /// <summary>Asks for a control that's on or off, or null if none was picked.</summary>
    private ShiftControl? PickSwitch()
    {
        var picker = new PickControlWindow(this, _host.PickableDevices);
        if (picker.ShowDialog() != true || picker.Picked is not { } picked) return null;
        if (picked.Kind == ControlKind.Axis && picked.Part is InputPart.Whole or InputPart.Zone)
        {
            Warn("A mode needs something that's either on or off: a button, a hat direction, or one end of an axis (its + or − part).");
            return null;
        }
        return new ShiftControl(picked.DeviceGuid, picked.Kind, picked.Index, picked.Part);
    }

    private void PickSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (PickSwitch() is not { } control) return;
        _picked = control;
        PickedSwitchText.Text = _host.ModeSwitchName(control);
        PickedSwitchText.Foreground = Brushes.Black;
    }

    private bool ValidName(string name, ModeDefinition? except)
    {
        if (name.Length == 0)
        {
            Warn("Type a name for the mode, e.g. Combat.");
            return false;
        }
        if (_host.Modes.Any(m => !ReferenceEquals(m, except) && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Warn($"There's already a mode called {name}.");
            return false;
        }
        return true;
    }

    private void AddMode_Click(object sender, RoutedEventArgs e)
    {
        var name = ModeNameBox.Text.Trim();
        if (!ValidName(name, null)) return;
        if (_picked is null)
        {
            Warn("Pick the switch that selects this mode first.");
            return;
        }
        var mode = _host.AddMode(name, _picked);
        ModeNameBox.Text = "";
        _picked = null;
        PickedSwitchText.Text = "none picked yet";
        PickedSwitchText.Foreground = Brushes.Gray;
        RefreshList(mode);
    }

    private void ChangeSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } mode || PickSwitch() is not { } control) return;
        _host.SetModeSwitch(mode, control);
        RefreshList(mode);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } mode) return;
        var name = ModeNameBox.Text.Trim();
        if (!ValidName(name, mode)) return;
        _host.RenameMode(mode, name);
        ModeNameBox.Text = "";
        RefreshList(mode);
    }

    private void RemoveMode_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } mode && _host.ConfirmDeleteMode(mode, this)) RefreshList();
    }

    private void Announce_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.AnnounceModes = AnnounceCheck.IsChecked == true;
    }

    private void Warn(string message) => MessageBox.Show(this, message, "Modes", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
