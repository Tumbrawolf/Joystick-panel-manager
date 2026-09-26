using ClaudFlight.Core.Devices;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.VJoy;

namespace ClaudFlight.Core.Mapping;

/// <summary>Drives one poll cycle: reads all physical devices, applies the active profile's
/// calibration and bindings, and writes the results to the acquired vJoy device(s).</summary>
public sealed class MappingEngine
{
    private readonly DeviceManager _devices;
    private readonly VJoyManager _vjoy;
    private Profile _profile = new();
    private Dictionary<(Guid, AxisType), AxisCalibration> _calibrations = [];

    public MappingEngine(DeviceManager devices, VJoyManager vjoy)
    {
        _devices = devices;
        _vjoy = vjoy;
    }

    public void LoadProfile(Profile profile)
    {
        var needed = profile.Bindings.Select(b => b.Target.VJoyDeviceId).Distinct().ToHashSet();
        var current = _profile.Bindings.Select(b => b.Target.VJoyDeviceId).Distinct();
        foreach (var id in current)
        {
            if (!needed.Contains(id)) _vjoy.Relinquish(id);
        }
        foreach (var id in needed) _vjoy.Acquire(id);

        _profile = profile;
        _calibrations = profile.AxisCalibrations.ToDictionary(c => (c.DeviceGuid, c.Axis), c => c.Calibration);
    }

    public Profile CurrentProfile => _profile;

    /// <summary>Live-updates one axis's calibration without needing a full profile reload
    /// (used by the calibration UI while the user drags sliders and watches the effect).</summary>
    public void SetCalibration(Guid deviceGuid, AxisType axis, AxisCalibration calibration)
    {
        _calibrations[(deviceGuid, axis)] = calibration;

        var entry = _profile.AxisCalibrations.FirstOrDefault(c => c.DeviceGuid == deviceGuid && c.Axis == axis);
        if (entry is not null) _profile.AxisCalibrations.Remove(entry);
        _profile.AxisCalibrations.Add(new AxisCalibrationEntry { DeviceGuid = deviceGuid, Axis = axis, Calibration = calibration });
    }

    public AxisCalibration GetCalibration(Guid deviceGuid, AxisType axis) =>
        _calibrations.GetValueOrDefault((deviceGuid, axis), DefaultCalibration);

    /// <summary>Polls every physical device once, applies the active profile, and pushes the
    /// result to vJoy. Returns the raw per-device snapshots so the UI can render a live view.</summary>
    public IReadOnlyDictionary<Guid, JoystickSnapshot> Tick()
    {
        // Poll each device independently: one device throwing (or hanging on a driver quirk)
        // should never prevent every other device's state from updating this tick.
        var snapshots = new Dictionary<Guid, JoystickSnapshot>();
        foreach (var device in _devices.Devices)
        {
            JoystickSnapshot snapshot;
            try
            {
                snapshot = device.Poll();
            }
            catch (Exception)
            {
                snapshot = JoystickSnapshot.Empty;
            }
            snapshots[device.InstanceGuid] = snapshot;
        }

        var buttonAgg = new Dictionary<(uint DeviceId, int Button), bool>();
        var povAgg = new Dictionary<(uint DeviceId, int Pov), int>();

        foreach (var binding in _profile.Bindings)
        {
            if (!snapshots.TryGetValue(binding.Source.DeviceGuid, out var snap)) continue;

            switch (binding.Source.Kind)
            {
                case ControlKind.Axis:
                    ApplyAxis(binding, snap);
                    break;

                case ControlKind.Button:
                    ApplyButton(binding, snap, buttonAgg);
                    break;

                case ControlKind.Pov:
                    ApplyPov(binding, snap, povAgg);
                    break;
            }
        }

        foreach (var ((deviceId, button), pressed) in buttonAgg)
            _vjoy.SetButton(deviceId, button, pressed);

        foreach (var ((deviceId, povIndex), direction) in povAgg)
            _vjoy.SetDiscretePov(deviceId, povIndex, direction);

        return snapshots;
    }

    private static readonly AxisCalibration DefaultCalibration = new();

    private void ApplyAxis(Binding binding, JoystickSnapshot snap)
    {
        var axisType = (AxisType)binding.Source.Index;
        if (!snap.Axes.TryGetValue(axisType, out var raw)) return;

        var calibration = _calibrations.GetValueOrDefault((binding.Source.DeviceGuid, axisType), DefaultCalibration);
        var normalized = calibration.Normalize(raw);
        _vjoy.SetAxis(binding.Target.VJoyDeviceId, (VJoyAxis)binding.Target.Index, normalized);
    }

    private static void ApplyButton(Binding binding, JoystickSnapshot snap, Dictionary<(uint, int), bool> agg)
    {
        if (binding.Source.Index >= snap.Buttons.Count) return;
        var pressed = snap.Buttons[binding.Source.Index];

        var key = (binding.Target.VJoyDeviceId, binding.Target.Index);
        agg[key] = agg.TryGetValue(key, out var existing) ? existing || pressed : pressed;
    }

    private static void ApplyPov(Binding binding, JoystickSnapshot snap, Dictionary<(uint, int), int> agg)
    {
        if (binding.Source.Index >= snap.PointOfViews.Count) return;
        var direction = DirectInputPovToDiscrete(snap.PointOfViews[binding.Source.Index]);

        var key = (binding.Target.VJoyDeviceId, binding.Target.Index);
        if (direction != -1) agg[key] = direction;
        else agg.TryAdd(key, -1);
    }

    /// <summary>DirectInput reports POV angle in hundredths of a degree (0=N, 9000=E, ...), -1 when centered.
    /// Snaps to vJoy's 4-way discrete POV (0=N,1=E,2=S,3=W, -1=neutral).</summary>
    private static int DirectInputPovToDiscrete(int raw)
    {
        if (raw < 0) return -1;
        return (int)Math.Round(raw / 9000.0) % 4;
    }
}
