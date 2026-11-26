using System.Collections.ObjectModel;
using System.Windows;
using ClaudFlight.App.ViewModels;
using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App;

// The vJoy Output tab: what the mappings are sending right now - the vJoy axes, buttons and hats
// a game sees, and the last keyboard keys pressed. Works without vJoy installed, so mappings can
// be checked before installing it.
public partial class MainWindow
{
    private VJoyOutputRecorder _vjoyRecorder = null!;
    private KeyOutputRecorder _keyRecorder = null!;
    private int _shownKeyVersion = -1;
    private object? _outputBuiltFor;

    public ObservableCollection<VJoyOutputDeviceViewModel> VJoyOutputs { get; } = [];
    public ObservableCollection<string> RecentKeys { get; } = [];

    private bool IsOutputTabShown => ReferenceEquals(MainTabs.SelectedItem, OutputTab) && IsVisible;

    /// <summary>Lists every vJoy control the profile writes to (both layers), each with what drives it.</summary>
    private void RebuildOutputView()
    {
        _outputBuiltFor = _currentProfile.Bindings;
        VJoyOutputs.Clear();
        var bySource = _currentProfile.Bindings
            .GroupBy(b => (b.Target.VJoyDeviceId, Kind: b.Target.Kind == ControlKind.Axis || b.Nudge != AxisNudge.None ? ControlKind.Axis : b.Target.Kind, b.Target.Index))
            .ToDictionary(g => g.Key, g => string.Join("\n", g.Select(DescribeOutputSource).Distinct()));

        foreach (var deviceId in bySource.Keys.Select(k => k.VJoyDeviceId).Distinct().Order())
        {
            var device = new VJoyOutputDeviceViewModel(deviceId);
            foreach (var ((_, kind, index), drivenBy) in bySource.Where(p => p.Key.VJoyDeviceId == deviceId).OrderBy(p => p.Key.Index))
            {
                switch (kind)
                {
                    case ControlKind.Axis: device.Axes.Add(new VJoyAxisOutputViewModel((VJoyAxis)index, drivenBy)); break;
                    case ControlKind.Button: device.Buttons.Add(new VJoyButtonOutputViewModel(index + 1, drivenBy)); break;
                    default: device.Hats.Add(new VJoyHatOutputViewModel(index + 1, drivenBy)); break;
                }
            }
            VJoyOutputs.Add(device);
        }

        OutputEmptyText.Visibility = VJoyOutputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutputVJoyNote.Visibility = _vjoyManager.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        _shownKeyVersion = -1;
        UpdateOutputView();
    }

    /// <summary>"Trigger (Stick) - latch, ⇧" style line for one mapping, for the outputs' tooltips.</summary>
    private string DescribeOutputSource(Binding binding)
    {
        var source = binding.Source;
        var text = $"{ControlName(source.DeviceGuid, source.Kind, source.Index, binding.Part)} ({DeviceName(source.DeviceGuid, "not connected")})";
        var extras = new List<string>();
        var trigger = TriggerName(binding.Trigger);
        if (trigger.Length > 0) extras.Add(trigger);
        if (binding.Part == InputPart.Zone) extras.Add(ZoneText(binding.ZoneFrom, binding.ZoneTo));
        if (binding.Shifted) extras.Add("⇧ shifted");
        return extras.Count == 0 ? text : $"{text} - {string.Join(", ", extras)}";
    }

    /// <summary>Copies the recorded values into the tab - only while it's showing.</summary>
    private void UpdateOutputView()
    {
        foreach (var device in VJoyOutputs)
        {
            foreach (var axis in device.Axes) axis.Set(_vjoyRecorder.Axis(device.Id, axis.Axis));
            foreach (var button in device.Buttons) button.IsPressed = _vjoyRecorder.Button(device.Id, button.Number);
            foreach (var hat in device.Hats) hat.Direction = _vjoyRecorder.Hat(device.Id, hat.Number);
        }

        if (_keyRecorder.Version == _shownKeyVersion) return;
        _shownKeyVersion = _keyRecorder.Version;
        RecentKeys.Clear();
        foreach (var (time, vkey, down) in _keyRecorder.Recent)
        {
            RecentKeys.Add($"{time:HH:mm:ss.f}   {(down ? "▼ down" : "▲ up  ")}   {KeyName(vkey)}");
        }
        OutputNoKeysText.Visibility = RecentKeys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string KeyName(int vkey) => KeyCatalog.Keys.FirstOrDefault(k => k.VKey == vkey)?.Name ?? $"0x{vkey:X2}";
}
