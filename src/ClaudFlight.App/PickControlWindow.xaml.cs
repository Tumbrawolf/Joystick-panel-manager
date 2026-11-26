using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudFlight.App.ViewModels;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App;

/// <summary>A control (or part of one) chosen in PickControlWindow.</summary>
public sealed record PickedControl(Guid DeviceGuid, ControlKind Kind, int Index, InputPart Part, string Display);

/// <summary>Picks any control to remap: from a list, or by pressing/moving it on the hardware
/// (watched through Live Monitor's readings, which keep updating while this is open).</summary>
public partial class PickControlWindow : Window
{
    private const int AxisMoveToPick = 500; // of the -1000..1000 raw range

    private readonly IReadOnlyList<DeviceMonitorViewModel> _devices;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<(Guid, ControlKind, int), int> _baseline = [];

    public PickedControl? Picked { get; private set; }

    internal PickControlWindow(Window owner, IReadOnlyList<DeviceMonitorViewModel> devices)
    {
        InitializeComponent();
        Owner = owner;
        _devices = devices;

        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedIndex = devices.Count > 0 ? 0 : -1;
        if (devices.Count == 0) DetectedText.Text = "No devices are connected (or all are hidden on Live Monitor).";

        // Remember where everything is now, so only a change counts as "this one".
        foreach (var device in devices)
        {
            foreach (var axis in device.Axes) _baseline[(device.DeviceGuid, ControlKind.Axis, (int)axis.Axis)] = axis.Raw;
            foreach (var button in device.Buttons) _baseline[(device.DeviceGuid, ControlKind.Button, button.Index)] = button.IsPressed ? 1 : 0;
            foreach (var pov in device.Povs) _baseline[(device.DeviceGuid, ControlKind.Pov, pov.Index)] = pov.RawHundredthDegrees;
        }

        _watch.Tick += (_, _) => Watch();
        _watch.Start();
        Closed += (_, _) => _watch.Stop();
    }

    private void DeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => FillList();

    private void FillList()
    {
        var items = new List<PickedControl>();
        if (DeviceCombo.SelectedItem is DeviceMonitorViewModel device)
        {
            IEnumerable<INamedControl> controls = [.. device.Axes, .. device.Buttons, .. device.Povs];
            foreach (var control in controls)
            {
                foreach (var part in InputParts.For(control.Kind))
                {
                    var name = control.Label + InputParts.Suffix(part) + (part == InputPart.Whole && control.Kind != ControlKind.Button
                        ? control.Kind == ControlKind.Axis ? "  (whole axis)" : "  (whole hat)"
                        : "");
                    items.Add(new PickedControl(device.DeviceGuid, control.Kind, control.ControlIndex, part, name));
                }
            }
        }
        ControlList.ItemsSource = items;
    }

    /// <summary>Selects whatever's just been pressed, pushed or moved. Controls that showed up
    /// after the window opened (Live Monitor rebuilds its readings when a device is refreshed) get
    /// their current reading as a baseline first, so a resting axis isn't mistaken for a move.</summary>
    private void Watch()
    {
        foreach (var device in _devices)
        {
            foreach (var axis in device.Axes) _baseline.TryAdd((device.DeviceGuid, ControlKind.Axis, (int)axis.Axis), axis.Raw);
            foreach (var button in device.Buttons) _baseline.TryAdd((device.DeviceGuid, ControlKind.Button, button.Index), button.IsPressed ? 1 : 0);
            foreach (var pov in device.Povs) _baseline.TryAdd((device.DeviceGuid, ControlKind.Pov, pov.Index), pov.RawHundredthDegrees);
        }

        foreach (var device in _devices)
        {
            foreach (var button in device.Buttons)
            {
                if (!button.IsPressed) _baseline[(device.DeviceGuid, ControlKind.Button, button.Index)] = 0;
                else if (_baseline.GetValueOrDefault((device.DeviceGuid, ControlKind.Button, button.Index)) == 0)
                {
                    Select(device, ControlKind.Button, button.Index, InputPart.Whole);
                    return;
                }
            }
            foreach (var pov in device.Povs)
            {
                var raw = pov.RawHundredthDegrees;
                if (raw < 0) _baseline[(device.DeviceGuid, ControlKind.Pov, pov.Index)] = -1;
                else if ( raw != _baseline.GetValueOrDefault((device.DeviceGuid, ControlKind.Pov, pov.Index), -1))
                {
                    Select(device, ControlKind.Pov, pov.Index, InputParts.FromPovDirection((int)Math.Round(raw / 9000.0) % 4));
                    return;
                }
            }
            foreach (var axis in device.Axes)
            {
                var key = (device.DeviceGuid, ControlKind.Axis, (int)axis.Axis);
                if (Math.Abs(axis.Raw - _baseline.GetValueOrDefault(key)) >= AxisMoveToPick)
                {
                    _baseline[key] = axis.Raw; // pick it once per big move, not continuously
                    Select(device, ControlKind.Axis, (int)axis.Axis, InputPart.Whole);
                    return;
                }
            }
        }
    }

    private void Select(DeviceMonitorViewModel device, ControlKind kind, int index, InputPart part)
    {
        if (!ReferenceEquals(DeviceCombo.SelectedItem, device)) DeviceCombo.SelectedItem = device;
        var item = (ControlList.ItemsSource as IEnumerable<PickedControl>)?.FirstOrDefault(c => c.Kind == kind && c.Index == index && c.Part == part);
        if (item is null || ReferenceEquals(ControlList.SelectedItem, item)) return;
        ControlList.SelectedItem = item;
        ControlList.ScrollIntoView(item);
        DetectedText.Text = $"Picked: {item.Display.Trim()} on {device.Label}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Finish();

    private void ControlList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Finish();

    private void Finish()
    {
        if (ControlList.SelectedItem is not PickedControl picked)
        {
            MessageBox.Show(this, "Pick a control first.", "Pick a control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Picked = picked;
        DialogResult = true;
    }
}
