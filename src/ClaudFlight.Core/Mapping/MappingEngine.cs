using System.Diagnostics;
using ClaudFlight.Core.Devices;
using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Mapping;

/// <summary>Drives one poll cycle: reads all physical devices, applies the active profile's
/// calibration and bindings, and writes the results to the acquired vJoy device(s) and/or
/// simulated keyboard input.
///
/// On top of plain pass-through it handles:
/// - parts of controls (hat directions, axis halves) acting as on/off switches;
/// - switch modes (hold, pulse on press, pulse on every flip, latch);
/// - the shift layer: while the profile's shift control is held, any input that has shift-layer
///   mappings uses those instead of its normal ones (inputs without any keep their normal ones);
/// - axis nudges: on/off inputs moving a vJoy axis up/down/to center (e.g. trim buttons).</summary>
public sealed class MappingEngine
{
    /// <summary>How long a pulse (SwitchMode.PulseOnPress/PulseOnBoth, key taps aside) holds its
    /// output on - long enough for a sim polling at a modest rate to see it.</summary>
    public const double PulseSeconds = 0.1;

    private const double ShiftAxisThreshold = 0.5;
    private const double MaxNudgeStepSeconds = 0.2;

    private readonly Func<IEnumerable<IPhysicalDevice>> _deviceSource;
    private readonly IVJoyOutput _vjoy;
    private readonly IKeyOutput _keys;
    private readonly Func<double> _clock;
    private Profile _profile = new();
    private Dictionary<(Guid, AxisType), AxisCalibration> _calibrations = [];
    private HashSet<(Guid, ControlKind, int, InputPart)> _shiftedSlots = [];

    private sealed class SwitchState
    {
        public bool LastRaw;
        public bool Latched;
        public double PulseUntil = double.MinValue;
    }

    private sealed class KeyState
    {
        public bool LastRaw;
        public bool Holding;
    }

    private Dictionary<Binding, SwitchState> _switchStates = [];
    private readonly Dictionary<KeyBinding, KeyState> _keyStates = [];
    private readonly Dictionary<(uint, VJoyAxis), double> _nudgedAxes = [];
    private double _lastTick = double.NaN;

    /// <summary>Off by default and never persisted - each app launch starts with keyboard
    /// injection disabled so it's never silently active without the user explicitly turning it
    /// on for that session.</summary>
    public bool KeyboardInjectionEnabled { get; set; }

    /// <param name="deviceSource">Called once per Tick to get the current set of pollable devices.
    /// A delegate rather than a single DeviceManager so callers can combine multiple device
    /// sources (DirectInput devices, the mouse, Raw Input keyboard devices) transparently.</param>
    /// <param name="vjoy">Virtual joystick output (VJoyManager in the app).</param>
    /// <param name="keys">Simulated keyboard output; SendInput if omitted.</param>
    /// <param name="clock">Seconds since some fixed point, for pulses and nudges; a Stopwatch if omitted.</param>
    public MappingEngine(Func<IEnumerable<IPhysicalDevice>> deviceSource, IVJoyOutput vjoy, IKeyOutput? keys = null, Func<double>? clock = null)
    {
        _deviceSource = deviceSource;
        _vjoy = vjoy;
        _keys = keys ?? new SendInputKeyOutput();
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            clock = () => stopwatch.Elapsed.TotalSeconds;
        }
        _clock = clock;
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

        ReleaseAllHeldKeys();

        _profile = profile;
        _calibrations = profile.AxisCalibrations.ToDictionary(c => (c.DeviceGuid, c.Axis), c => c.Calibration);

        _shiftedSlots = [];
        foreach (var binding in profile.Bindings.Where(b => b.Shifted)) _shiftedSlots.Add(SlotOf(binding));
        foreach (var keyBinding in profile.KeyBindings.Where(k => k.Shifted)) _shiftedSlots.Add(SlotOf(keyBinding));

        // Keep switch state (latches, pulses in flight) for bindings that survived the reload.
        var kept = new HashSet<Binding>(profile.Bindings);
        _switchStates = _switchStates.Where(s => kept.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value);
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
    /// result to vJoy and/or simulated key presses. Returns the raw per-device snapshots so the
    /// UI can render a live view.</summary>
    public IReadOnlyDictionary<Guid, JoystickSnapshot> Tick()
    {
        var now = _clock();
        var elapsed = double.IsNaN(_lastTick) ? 0 : Math.Clamp(now - _lastTick, 0, MaxNudgeStepSeconds);
        _lastTick = now;

        // Poll each device independently: one device throwing (or hanging on a driver quirk)
        // should never prevent every other device's state from updating this tick.
        var snapshots = new Dictionary<Guid, JoystickSnapshot>();
        foreach (var device in _deviceSource())
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

        var shiftHeld = _profile.ShiftControl is { } shift && snapshots.TryGetValue(shift.DeviceGuid, out var shiftSnap) &&
                        IsOn(shiftSnap, shift.DeviceGuid, shift.Kind, shift.Index, shift.Part, ShiftAxisThreshold);

        var buttonAgg = new Dictionary<(uint DeviceId, int Button), bool>();
        var povAgg = new Dictionary<(uint DeviceId, int Pov), int>();
        var nudgedThisProfile = new HashSet<(uint, VJoyAxis)>();

        foreach (var binding in _profile.Bindings)
        {
            if (!snapshots.TryGetValue(binding.Source.DeviceGuid, out var snap)) continue;
            var inLayer = binding.Shifted == (shiftHeld && _shiftedSlots.Contains(SlotOf(binding)));

            if (binding.Nudge != AxisNudge.None)
            {
                var axisKey = (binding.Target.VJoyDeviceId, (VJoyAxis)binding.Target.Index);
                nudgedThisProfile.Add(axisKey);
                ApplyNudge(binding, snap, inLayer, axisKey, now, elapsed);
                continue;
            }

            switch (binding.Source.Kind, binding.Part)
            {
                case (ControlKind.Axis, InputPart.Whole):
                    if (inLayer) ApplyAxis(binding, snap);
                    break;

                case (ControlKind.Pov, InputPart.Whole):
                    ApplyPov(binding, snap, inLayer, povAgg);
                    break;

                default:
                    var raw = IsOn(snap, binding.Source.DeviceGuid, binding.Source.Kind, binding.Source.Index, binding.Part, binding.AxisThreshold);
                    var pressed = ApplySwitchMode(binding, raw, inLayer, now);
                    var key = (binding.Target.VJoyDeviceId, binding.Target.Index);
                    buttonAgg[key] = buttonAgg.TryGetValue(key, out var existing) ? existing || pressed : pressed;
                    break;
            }
        }

        // Bindings store vJoy button/hat numbers 0-based (the UI shows them 1-based), but vJoy
        // numbers them from 1 - button 0 / hat 0 don't exist and would be silently ignored.
        foreach (var ((deviceId, button), pressed) in buttonAgg)
            _vjoy.SetButton(deviceId, button + 1, pressed);

        foreach (var ((deviceId, povIndex), direction) in povAgg)
            _vjoy.SetDiscretePov(deviceId, povIndex + 1, direction);

        foreach (var axisKey in nudgedThisProfile)
            _vjoy.SetAxis(axisKey.Item1, axisKey.Item2, _nudgedAxes.GetValueOrDefault(axisKey));

        if (KeyboardInjectionEnabled)
        {
            ProcessKeyBindings(snapshots, shiftHeld);
        }
        else if (_keyStates.Values.Any(s => s.Holding))
        {
            // Safety net: if injection gets toggled off mid-hold, don't leave a key stuck down.
            ReleaseAllHeldKeys();
        }

        return snapshots;
    }

    private static readonly AxisCalibration DefaultCalibration = new();

    private static (Guid, ControlKind, int, InputPart) SlotOf(Binding binding) =>
        (binding.Source.DeviceGuid, binding.Source.Kind, binding.Source.Index, binding.Part);

    private static (Guid, ControlKind, int, InputPart) SlotOf(KeyBinding keyBinding) =>
        (keyBinding.Source.DeviceGuid, keyBinding.Source.Kind, keyBinding.Source.Index, InputParts.Of(keyBinding));

    /// <summary>Whether an input counts as "on": a button held, a hat pushed (toward a given
    /// direction, or at all for the whole hat), or an axis pushed past threshold in one direction.</summary>
    private bool IsOn(JoystickSnapshot snap, Guid deviceGuid, ControlKind kind, int index, InputPart part, double axisThreshold)
    {
        switch (kind)
        {
            case ControlKind.Button:
                return index < snap.Buttons.Count && snap.Buttons[index];

            case ControlKind.Pov:
                if (index >= snap.PointOfViews.Count) return false;
                var pov = snap.PointOfViews[index];
                return part == InputPart.Whole ? pov >= 0 : InputParts.IsPovDirectionActive(pov, part);

            case ControlKind.Axis:
                if (part is not (InputPart.Positive or InputPart.Negative)) return false;
                var axisType = (AxisType)index;
                if (!snap.Axes.TryGetValue(axisType, out var raw)) return false;
                var normalized = _calibrations.GetValueOrDefault((deviceGuid, axisType), DefaultCalibration).Normalize(raw);
                return part == InputPart.Positive ? normalized >= axisThreshold : normalized <= -axisThreshold;

            default:
                return false;
        }
    }

    /// <summary>Turns an on/off input into the output's on/off state per the binding's mode.
    /// Edges are tracked on the raw input whatever the layer, so switching layers never fires a
    /// spurious pulse; a press only counts while the binding's layer is the active one.</summary>
    private bool ApplySwitchMode(Binding binding, bool raw, bool inLayer, double now)
    {
        if (binding.Mode == SwitchMode.Hold) return raw && inLayer;

        if (!_switchStates.TryGetValue(binding, out var state))
        {
            state = new SwitchState { LastRaw = raw }; // first sight: not an edge
            _switchStates[binding] = state;
        }

        var rising = raw && !state.LastRaw;
        var falling = !raw && state.LastRaw;
        state.LastRaw = raw;

        switch (binding.Mode)
        {
            case SwitchMode.PulseOnPress:
                if (rising && inLayer) state.PulseUntil = now + PulseSeconds;
                return now < state.PulseUntil;
            case SwitchMode.PulseOnBoth:
                if ((rising || falling) && inLayer) state.PulseUntil = now + PulseSeconds;
                return now < state.PulseUntil;
            case SwitchMode.Latch:
                if (rising && inLayer) state.Latched = !state.Latched;
                return state.Latched;
            default:
                return raw && inLayer;
        }
    }

    private void ApplyNudge(Binding binding, JoystickSnapshot snap, bool inLayer, (uint, VJoyAxis) axisKey, double now, double elapsed)
    {
        var raw = IsOn(snap, binding.Source.DeviceGuid, binding.Source.Kind, binding.Source.Index, binding.Part, binding.AxisThreshold);

        if (!_switchStates.TryGetValue(binding, out var state))
        {
            state = new SwitchState { LastRaw = raw };
            _switchStates[binding] = state;
        }
        var rising = raw && !state.LastRaw;
        state.LastRaw = raw;
        if (!inLayer) return;

        var value = _nudgedAxes.GetValueOrDefault(axisKey);
        var sign = binding.Nudge == AxisNudge.Decrease ? -1 : 1;
        if (binding.Nudge == AxisNudge.Center)
        {
            if (raw) value = 0;
        }
        else if (binding.Mode == SwitchMode.Hold)
        {
            // Full travel is 2 (-1..1), so "fraction of full travel per second" doubles here.
            if (raw) value += sign * binding.NudgeAmount * 2 * elapsed;
        }
        else if (rising)
        {
            value += sign * binding.NudgeAmount * 2;
        }
        _nudgedAxes[axisKey] = Math.Clamp(value, -1, 1);
    }

    private void ApplyAxis(Binding binding, JoystickSnapshot snap)
    {
        var axisType = (AxisType)binding.Source.Index;
        if (!snap.Axes.TryGetValue(axisType, out var raw)) return;

        var calibration = _calibrations.GetValueOrDefault((binding.Source.DeviceGuid, axisType), DefaultCalibration);
        var normalized = calibration.Normalize(raw);
        _vjoy.SetAxis(binding.Target.VJoyDeviceId, (VJoyAxis)binding.Target.Index, normalized);
    }

    private static void ApplyPov(Binding binding, JoystickSnapshot snap, bool inLayer, Dictionary<(uint, int), int> agg)
    {
        var key = (binding.Target.VJoyDeviceId, binding.Target.Index);
        if (!inLayer || binding.Source.Index >= snap.PointOfViews.Count)
        {
            agg.TryAdd(key, -1);
            return;
        }

        var direction = DirectInputPovToDiscrete(snap.PointOfViews[binding.Source.Index]);
        if (direction != -1) agg[key] = direction;
        else agg.TryAdd(key, -1);
    }

    private void ProcessKeyBindings(IReadOnlyDictionary<Guid, JoystickSnapshot> snapshots, bool shiftHeld)
    {
        foreach (var keyBinding in _profile.KeyBindings)
        {
            // Only ever inject a key from the curated catalog. A KeyBinding's VirtualKeyCode is
            // validated against this when created through the UI, but a hand-edited or shared
            // profile.json could set it to anything - re-checking here (the one place that
            // actually calls SendInput) means a malicious profile file can never make this
            // simulate a key outside the app's own approved list, however it was loaded.
            if (!KeyCatalog.Keys.Any(k => k.VKey == keyBinding.VirtualKeyCode)) continue;

            if (!snapshots.TryGetValue(keyBinding.Source.DeviceGuid, out var snap)) continue;

            var part = InputParts.Of(keyBinding);
            var raw = IsOn(snap, keyBinding.Source.DeviceGuid, keyBinding.Source.Kind, keyBinding.Source.Index, part, keyBinding.AxisThreshold);
            var inLayer = keyBinding.Shifted == (shiftHeld && _shiftedSlots.Contains(SlotOf(keyBinding)));

            if (!_keyStates.TryGetValue(keyBinding, out var state))
            {
                state = new KeyState { LastRaw = raw };
                _keyStates[keyBinding] = state;
                if (!(keyBinding.Source.Kind == ControlKind.Axis || keyBinding.HoldWhilePressed)) continue;
            }

            var rising = raw && !state.LastRaw;
            var falling = !raw && state.LastRaw;
            state.LastRaw = raw;

            if (keyBinding.Source.Kind == ControlKind.Axis || keyBinding.HoldWhilePressed)
            {
                var shouldHold = raw && inLayer;
                if (shouldHold && !state.Holding) _keys.KeyDown(keyBinding.VirtualKeyCode);
                else if (!shouldHold && state.Holding) _keys.KeyUp(keyBinding.VirtualKeyCode);
                state.Holding = shouldHold;
            }
            else if (inLayer && (rising || (falling && keyBinding.TapOnRelease)))
            {
                // Tap: a single down+up on the edge rather than holding the key down.
                _keys.KeyDown(keyBinding.VirtualKeyCode);
                _keys.KeyUp(keyBinding.VirtualKeyCode);
            }
        }
    }

    /// <summary>Releases every key currently held down by a KeyBinding (e.g. when injection is
    /// toggled off, the profile changes, or the app is closing) so nothing gets left stuck.</summary>
    private void ReleaseAllHeldKeys()
    {
        foreach (var (keyBinding, state) in _keyStates)
        {
            if (state.Holding) _keys.KeyUp(keyBinding.VirtualKeyCode);
        }
        _keyStates.Clear();
    }

    /// <summary>DirectInput reports POV angle in hundredths of a degree (0=N, 9000=E, ...), -1 when centered.
    /// Snaps to vJoy's 4-way discrete POV (0=N,1=E,2=S,3=W, -1=neutral).</summary>
    private static int DirectInputPovToDiscrete(int raw)
    {
        if (raw < 0) return -1;
        return (int)Math.Round(raw / 9000.0) % 4;
    }
}
