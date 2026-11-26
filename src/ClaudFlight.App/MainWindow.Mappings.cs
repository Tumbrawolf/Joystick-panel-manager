using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ClaudFlight.App.ViewModels;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.VJoy;
using Binding = ClaudFlight.Core.Models.Binding;

namespace ClaudFlight.App;

/// <summary>One line of the Mappings tab: something one control (or part of one) does.</summary>
public sealed record MappingListRow(
    string Device, string Control, string Layer, string Type, string Output,
    Guid DeviceGuid, ControlKind Kind, int Index, InputPart Part, object? Source,
    bool IsShifted = false, string Warning = "")
{
    public string WarningMark => Warning.Length > 0 ? "⚠" : "";
    public string? WarningTip => Warning.Length > 0 ? Warning : null;

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Device} {Control}: {Output} ({Layer} layer)" + (Warning.Length > 0 ? $". {Warning}" : "");
}

/// <summary>A choice in one of the Mappings tab's filter dropdowns.</summary>
public sealed record MappingFilterOption(string Display, object? Value)
{
    public override string ToString() => Display;
}

/// <summary>The Mappings tab (a plain list of everything mapped, opened into the remap window for
/// changes), plus the other ways in to mapping: Live Monitor's right-click "Remap..." and each
/// device card's "Map all to vJoy...", and the session-wide keyboard keys switch.</summary>
public partial class MainWindow
{
    public ObservableCollection<MappingListRow> MappingRows { get; } = [];
    public ObservableCollection<MappingFilterOption> MappingDeviceFilters { get; } = [];

    public IReadOnlyList<MappingFilterOption> MappingLayerFilters { get; } =
        [new("Both layers", null), new("Normal", "Normal"), new("Shifted", "Shifted")];

    public IReadOnlyList<MappingFilterOption> MappingTypeFilters { get; } =
        [new("Everything", null), new("vJoy", "vJoy"), new("Keys", "Key"), new("Axis moves", "Axis move"), new("Conflicts", ConflictsFilter)];

    private const string ConflictsFilter = "Conflicts";

    private bool _rebuildingMappingList;

    // ---- The list ----

    /// <summary>Rebuilds the Mappings list from the profile. Runs after every remap, profile
    /// change and device refresh, and when the tab is opened (to pick up renames).</summary>
    private void RebuildMappingList()
    {
        _rebuildingMappingList = true;
        var selectedDevice = (MappingDeviceFilterCombo.SelectedItem as MappingFilterOption)?.Value;

        RefreshConflicts();
        MappingRows.Clear();
        foreach (var binding in _currentProfile.Bindings)
        {
            var type = binding.Nudge != AxisNudge.None ? "Axis move" : "vJoy";
            MappingRows.Add(Row(binding.Source, binding.Part, binding.Shifted, binding.ModeName, type, DescribeBinding(binding), binding));
        }
        foreach (var keyBinding in _currentProfile.KeyBindings)
        {
            MappingRows.Add(Row(keyBinding.Source, InputParts.Of(keyBinding), keyBinding.Shifted, keyBinding.ModeName, "Key", DescribeKeyBinding(keyBinding), keyBinding));
        }
        if (_currentProfile.ShiftControl is { } shift)
        {
            MappingRows.Add(Row(new InputRef(shift.DeviceGuid, shift.Kind, shift.Index), shift.Part, false, null, "Shift",
                "Shift: hold for the shifted layer", null));
        }
        foreach (var mode in _currentProfile.Modes)
        {
            if (mode.SelectedBy is not { } control) continue;
            MappingRows.Add(Row(new InputRef(control.DeviceGuid, control.Kind, control.Index), control.Part, false, null, "Mode",
                $"Mode switch: {mode.Name}", mode));
        }

        MappingDeviceFilters.Clear();
        MappingDeviceFilters.Add(new MappingFilterOption("All devices", null));
        foreach (var group in MappingRows.GroupBy(r => r.DeviceGuid).OrderBy(g => g.First().Device))
        {
            MappingDeviceFilters.Add(new MappingFilterOption(group.First().Device, group.Key));
        }
        MappingDeviceFilterCombo.SelectedItem = MappingDeviceFilters.FirstOrDefault(f => Equals(f.Value, selectedDevice)) ?? MappingDeviceFilters[0];
        if (MappingLayerFilterCombo.SelectedIndex < 0) MappingLayerFilterCombo.SelectedIndex = 0;
        if (MappingTypeFilterCombo.SelectedIndex < 0) MappingTypeFilterCombo.SelectedIndex = 0;

        _rebuildingMappingList = false;
        ApplyMappingFilter();
        return;

        MappingListRow Row(InputRef source, InputPart part, bool shifted, string? modeName, string type, string output, object? model)
        {
            var layer = modeName is null ? shifted ? "Shifted" : "Normal" : shifted ? $"{modeName}, shifted" : modeName;
            return new MappingListRow(DeviceName(source.DeviceGuid), ControlName(source.DeviceGuid, source.Kind, source.Index, part),
                layer, type, output,
                source.DeviceGuid, source.Kind, source.Index, part, model,
                shifted, model is Binding or ClaudFlight.Core.Models.KeyBinding ? ConflictNote(model) : "");
        }
    }

    private void MappingFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebuildingMappingList) ApplyMappingFilter();
    }

    private void MappingSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_rebuildingMappingList && IsLoaded) ApplyMappingFilter();
    }

    private void ApplyMappingFilter()
    {
        var device = (MappingDeviceFilterCombo.SelectedItem as MappingFilterOption)?.Value;
        var layer = (MappingLayerFilterCombo.SelectedItem as MappingFilterOption)?.Value as string;
        var type = (MappingTypeFilterCombo.SelectedItem as MappingFilterOption)?.Value as string;
        var search = MappingSearchBox.Text.Trim();

        var view = CollectionViewSource.GetDefaultView(MappingRows);
        view.Filter = item => item is MappingListRow row &&
                              (device is null || Equals(row.DeviceGuid, device)) &&
                              (layer is null || row.Type is "Shift" or "Mode" || (layer == "Shifted") == row.IsShifted) &&
                              (type is null || (type == ConflictsFilter ? row.Warning.Length > 0 : row.Type == type)) &&
                              (search.Length == 0 || MatchesSearch(row, search));

        var shown = view.Cast<object>().Count();
        MappingCountText.Text = MappingRows.Count == 0
            ? "Nothing is mapped yet. Click \"Add...\", right-click a control on Live Monitor, or click a marker on the Layout tab."
            : shown == MappingRows.Count ? $"{MappingRows.Count} mapping(s)." : $"Showing {shown} of {MappingRows.Count} mapping(s).";
        var clashing = MappingRows.Count(r => r.Warning.Length > 0);
        if (clashing > 0) MappingCountText.Text += $"  ⚠ {clashing} clash with another mapping - pick \"{ConflictsFilter}\" to see them.";
    }

    private static bool MatchesSearch(MappingListRow row, string search) =>
        new[] { row.Device, row.Control, row.Layer, row.Type, row.Output }.Any(text => text.Contains(search, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Opens a row for changes: the remap window, or for a mode switch, the Modes window.</summary>
    private void OpenMappingRow(MappingListRow row)
    {
        if (row.Source is ModeDefinition) new ModesWindow(this).ShowDialog();
        else OpenRemap(row.DeviceGuid, row.Kind, row.Index, row.Part, row.Source);
    }

    private void MappingsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row (not the header or empty space).
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(MappingsGrid, source) is DataGridRow { Item: MappingListRow row })
        {
            OpenMappingRow(row);
        }
    }

    private void ChangeMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MappingListRow row }) OpenMappingRow(row);
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MappingListRow row }) return;
        if (row.Source is ModeDefinition mode)
        {
            ConfirmDeleteMode(mode, this);
        }
        else if (row.Source is null)
        {
            _currentProfile.ShiftControl = null;
            AfterRemapsChanged();
        }
        else
        {
            RemoveRemap(row.Source);
        }
    }

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        var picker = new PickControlWindow(this, Devices);
        if (picker.ShowDialog() == true && picker.Picked is { } picked)
        {
            OpenRemap(picked.DeviceGuid, picked.Kind, picked.Index, picked.Part);
        }
    }

    // ---- Live Monitor: right-click "Remap..." ----

    /// <summary>Live Monitor's right-click "Remap..." items; the item's Tag names the part
    /// (Whole, Up, Right, Down, Left, Positive, Negative).</summary>
    private void RemapControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: INamedControl control, Tag: string tag }) return;
        if (!Enum.TryParse<InputPart>(tag, out var part)) return;
        OpenRemap(control.DeviceGuid, control.Kind, control.ControlIndex, part);
    }

    // ---- Device card: "Map all to vJoy..." ----

    private void MapAllToVJoy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DeviceMonitorViewModel device } button) return;

        if (!_vjoyManager.IsAvailable)
        {
            MessageBox.Show(this, $"vJoy isn't available ({_vjoyManager.UnavailableReason}). Install vJoy and set up at least one vJoy device with Configure vJoy first.",
                "Map all to vJoy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var vjoyIds = Enumerable.Range(1, 16).Select(i => (uint)i)
            .Where(id => _vjoyManager.GetStatus(id) is not (VJoyDeviceStatus.Missing or VJoyDeviceStatus.Unknown)).ToList();
        if (vjoyIds.Count == 0)
        {
            MessageBox.Show(this, "No vJoy devices are set up. Open Configure vJoy (Start menu) and enable device 1.",
                "Map all to vJoy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (vjoyIds.Count == 1)
        {
            MapAllToVJoy(device, vjoyIds[0]);
            return;
        }

        // Several vJoy devices: ask which, with a little menu under the button.
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var id in vjoyIds)
        {
            var item = new MenuItem { Header = $"Onto vJoy device {id}" };
            item.Click += (_, _) => MapAllToVJoy(device, id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Maps every control on a device that isn't mapped to vJoy yet onto the given vJoy
    /// device: axes to the same-named vJoy axis where it's free (else the next free one), buttons
    /// and hats to the next free slots. Stops at what vJoy device is configured with; existing
    /// mappings are left alone.</summary>
    private void MapAllToVJoy(DeviceMonitorViewModel device, uint vjoyId)
    {
        // "Mapped" means the whole control already goes to vJoy in the normal layer; a single hat
        // direction / axis end, a shifted-layer mapping or a nudge doesn't count.
        bool AlreadyMapped(ControlKind kind, int index) => _currentProfile.Bindings.Any(b =>
            b.Source.DeviceGuid == device.DeviceGuid && b.Source.Kind == kind && b.Source.Index == index &&
            b.Part == InputPart.Whole && !b.Shifted && b.Nudge == AxisNudge.None);

        var used = _currentProfile.Bindings.Where(b => b.Target.VJoyDeviceId == vjoyId).ToList();
        // Includes axes moved by nudge buttons (trim): those are written every tick and would
        // override a physical axis mapped onto the same vJoy axis.
        var usedAxes = used.Where(b => b.Target.Kind == ControlKind.Axis).Select(b => (VJoyAxis)b.Target.Index).ToHashSet();
        var usedButtons = used.Where(b => b.Target.Kind == ControlKind.Button).Select(b => b.Target.Index).ToHashSet(); // 0-based
        var usedHats = used.Where(b => b.Target.Kind == ControlKind.Pov).Select(b => b.Target.Index).ToHashSet();

        int added = 0, skipped = 0, alreadyDone = 0;
        var newBindings = new List<Binding>();

        foreach (var axis in device.Axes)
        {
            if (AlreadyMapped(ControlKind.Axis, (int)axis.Axis)) { alreadyDone++; continue; }
            VJoyAxis? target = null;
            if (Enum.TryParse<VJoyAxis>(axis.Axis.ToString(), out var sameNamed) && !usedAxes.Contains(sameNamed) && _vjoyManager.AxisExists(vjoyId, sameNamed))
            {
                target = sameNamed;
            }
            else
            {
                target = Enum.GetValues<VJoyAxis>().Cast<VJoyAxis?>().FirstOrDefault(a => !usedAxes.Contains(a!.Value) && _vjoyManager.AxisExists(vjoyId, a.Value));
            }
            if (target is null) { skipped++; continue; }
            usedAxes.Add(target.Value);
            newBindings.Add(new Binding { Source = new InputRef(device.DeviceGuid, ControlKind.Axis, (int)axis.Axis), Target = new OutputRef(vjoyId, ControlKind.Axis, (int)target.Value) });
            added++;
        }

        var buttonCount = _vjoyManager.GetButtonCount(vjoyId);
        var nextButton = 0;
        foreach (var button in device.Buttons)
        {
            if (AlreadyMapped(ControlKind.Button, button.Index)) { alreadyDone++; continue; }
            while (usedButtons.Contains(nextButton)) nextButton++;
            if (nextButton >= buttonCount) { skipped++; continue; }
            usedButtons.Add(nextButton);
            newBindings.Add(new Binding { Source = new InputRef(device.DeviceGuid, ControlKind.Button, button.Index), Target = new OutputRef(vjoyId, ControlKind.Button, nextButton) });
            added++;
        }

        var hatCount = _vjoyManager.GetDiscretePovCount(vjoyId);
        var nextHat = 0;
        foreach (var pov in device.Povs)
        {
            if (AlreadyMapped(ControlKind.Pov, pov.Index)) { alreadyDone++; continue; }
            while (usedHats.Contains(nextHat)) nextHat++;
            if (nextHat >= hatCount) { skipped++; continue; }
            usedHats.Add(nextHat);
            newBindings.Add(new Binding { Source = new InputRef(device.DeviceGuid, ControlKind.Pov, pov.Index), Target = new OutputRef(vjoyId, ControlKind.Pov, nextHat) });
            added++;
        }

        _currentProfile.Bindings.AddRange(newBindings);
        AfterRemapsChanged();

        var message = $"Added {added} mapping(s) for '{device.Label}' onto vJoy device {vjoyId}.";
        if (alreadyDone > 0) message += $"\n\n{alreadyDone} control(s) were already mapped to vJoy and were left as they are.";
        if (skipped > 0)
        {
            message += $"\n\n{skipped} control(s) were skipped because vJoy device {vjoyId} has no free slot left for them. Give it more axes/buttons/hats in Configure vJoy (Start menu), then run this again.";
        }
        message += "\n\nSee or change them on the Mappings tab.";
        MessageBox.Show(this, message, "Map all to vJoy", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Keyboard keys switch ----

    private System.Windows.Forms.ToolStripMenuItem? _trayKeyboardItem;

    /// <summary>Whether simulated keyboard keys are allowed this session (always off at start).</summary>
    internal bool KeyboardKeysOn => _engine.KeyboardInjectionEnabled;

    private void KeyboardInjectionToggle_Changed(object sender, RoutedEventArgs e) => SetKeyboardKeys(KeyboardInjectionToggle.IsChecked == true);

    private void SetKeyboardKeys(bool on)
    {
        _engine.KeyboardInjectionEnabled = on;
        if (KeyboardInjectionToggle.IsChecked != on) KeyboardInjectionToggle.IsChecked = on;
        if (_trayKeyboardItem is not null && _trayKeyboardItem.Checked != on) _trayKeyboardItem.Checked = on;
    }

    private void AddTrayKeyboardItem(System.Windows.Forms.ContextMenuStrip menu)
    {
        _trayKeyboardItem = new System.Windows.Forms.ToolStripMenuItem("Keyboard keys on") { CheckOnClick = true };
        _trayKeyboardItem.CheckedChanged += (_, _) => SetKeyboardKeys(_trayKeyboardItem.Checked);
        menu.Items.Add(_trayKeyboardItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
    }
}
