using System.Diagnostics;
using System.Text.Json;
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
/// - axis nudges: on/off inputs moving a vJoy axis up/down/to center (e.g. trim buttons);
/// - press triggers: a mapping can react only to a short press, a long press or a double tap;
/// - axis zones: a stretch of an axis's travel acting as a button;
/// - key sequences (macros) played step by step over the following ticks.</summary>
public sealed class MappingEngine
{
    /// <summary>How long a pulse (SwitchMode.PulseOnPress/PulseOnBoth, key taps aside) holds its
    /// output on - long enough for a sim polling at a modest rate to see it.</summary>
    public const double PulseSeconds = 0.1;

    /// <summary>How long a press has to last to count as a long press (and how short a short one).</summary>
    public const double LongPressSeconds = 0.5;

    /// <summary>The most time between two presses for them to count as a double tap.</summary>
    public const double DoubleTapSeconds = 0.35;

    /// <summary>Key sequences: how long each key is held, and the gap before the next step.</summary>
    public const double SequenceKeySeconds = 0.05;
    public const double SequenceGapSeconds = 0.05;

    private const double ShiftAxisThreshold = 0.5;
    private const double MaxNudgeStepSeconds = 0.2;

    private readonly Func<IEnumerable<IPhysicalDevice>> _deviceSource;
    private readonly IVJoyOutput _vjoy;
    private readonly IKeyOutput _keys;
    private readonly Func<double> _clock;
    private Profile _profile = new();
    private Dictionary<(Guid, AxisType), AxisCalibration> _calibrations = [];

    /// <summary>Inputs with shift-layer mappings that apply with no mode active (any-mode ones),
    /// and per mode, inputs with shift-layer mappings of that mode.</summary>
    private HashSet<(Guid, ControlKind, int, InputPart)> _shiftedSlots = [];
    private Dictionary<string, HashSet<(Guid, ControlKind, int, InputPart)>> _shiftedModeSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per mode, the inputs with mappings of that mode (which then replace their any-mode ones).</summary>
    private Dictionary<string, HashSet<(Guid, ControlKind, int, InputPart)>> _modeSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inputs with a double-tap mapping: their short-press mappings wait to rule out a double tap.</summary>
    private HashSet<(Guid, ControlKind, int, InputPart)> _doubleTapSlots = [];

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

    private sealed class TriggerState
    {
        public bool LastRaw;
        public double PressedAt = double.NaN;
        public double LastTapAt = double.NegativeInfinity;
        public bool SecondTap;
        public double PulseUntil = double.MinValue;

        /// <summary>Short press only: when a short press was let go while waiting to see whether it's
        /// the first tap of a double tap (NaN when not waiting).</summary>
        public double ShortPending = double.NaN;

        /// <summary>Short press only: this press is a double tap's second tap, so its release isn't a short press.</summary>
        public bool SkipRelease;
    }

    private sealed record PendingKey(double At, int VKey, bool Down);

    private Dictionary<Binding, SwitchState> _switchStates = [];
    private readonly Dictionary<KeyBinding, KeyState> _keyStates = [];
    private Dictionary<object, TriggerState> _triggerStates = [];
    private readonly Dictionary<KeyBinding, Queue<PendingKey>> _sequences = [];
    private readonly Dictionary<int, int> _sequenceKeysDown = [];
    private readonly Dictionary<(uint, VJoyAxis), double> _nudgedAxes = [];
    private double _lastTick = double.NaN;

    // Test presses from the remap window: until when a button is held, and when a hat/axis test began.
    private readonly Dictionary<(uint, int), double> _testButtons = [];
    private readonly Dictionary<(uint, int), double> _testHats = [];
    private readonly Dictionary<(uint, VJoyAxis), double> _testAxes = [];

    /// <summary>How long a tested button is held; a tested hat spends this long on each direction.</summary>
    public const double TestPressSeconds = 0.3;

    /// <summary>How long a tested axis takes to sweep end to end and back to center.</summary>
    public const double TestSweepSeconds = 2;

    /// <summary>A latching mapping turned on (true) or off (false) - by a press, not a profile reload.</summary>
    public event Action<Binding, bool>? LatchChanged;

    /// <summary>The active mode changed (null = no mode).</summary>
    public event Action<string?>? ModeChanged;

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
        _shiftedModeSlots = new(StringComparer.OrdinalIgnoreCase);
        _modeSlots = new(StringComparer.OrdinalIgnoreCase);
        var mappings = profile.Bindings.Select(b => (Slot: SlotOf(b), b.Shifted, b.ModeName))
            .Concat(profile.KeyBindings.Select(k => (Slot: SlotOf(k), k.Shifted, k.ModeName)));
        foreach (var (slot, shifted, modeName) in mappings)
        {
            if (modeName is null)
            {
                if (shifted) _shiftedSlots.Add(slot);
                continue;
            }
            Add(_modeSlots, modeName, slot);
            if (shifted) Add(_shiftedModeSlots, modeName, slot);
        }
        if (ActiveMode is { } mode && !profile.Modes.Any(m => string.Equals(m.Name, mode, StringComparison.OrdinalIgnoreCase))) ActiveMode = null;

        _doubleTapSlots = [];
        foreach (var binding in profile.Bindings.Where(b => b.Trigger == PressTrigger.DoubleTap)) _doubleTapSlots.Add(SlotOf(binding));
        foreach (var keyBinding in profile.KeyBindings.Where(k => k.Trigger == PressTrigger.DoubleTap)) _doubleTapSlots.Add(SlotOf(keyBinding));

        // Keep switch state (latches, pulses in flight) for mappings that survived the reload - the
        // same object, or failing that an identical copy (undo, or a profile read back from disk), so
        // reloading never lets go of a latched output by itself.
        _switchStates = CarryOver(_switchStates, profile.Bindings);
        _triggerStates = CarryOver(_triggerStates, profile.Bindings.Cast<object>().Concat(profile.KeyBindings));
        LatchVersion++;
    }

    /// <summary>The states of the current mappings: each keeps its own, else takes an identical
    /// old mapping's (matched by content), else starts fresh.</summary>
    private static Dictionary<TKey, TValue> CarryOver<TKey, TValue>(Dictionary<TKey, TValue> old, IEnumerable<TKey> current) where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        var unmatched = new List<TKey>();
        foreach (var key in current)
        {
            if (old.Remove(key, out var value)) result[key] = value;
            else unmatched.Add(key);
        }
        if (unmatched.Count == 0 || old.Count == 0) return result;

        var byContent = old.GroupBy(p => ContentKey(p.Key)).ToDictionary(g => g.Key, g => new Queue<TValue>(g.Select(p => p.Value)));
        foreach (var key in unmatched)
        {
            if (byContent.TryGetValue(ContentKey(key), out var queue) && queue.Count > 0) result[key] = queue.Dequeue();
        }
        return result;
    }

    private static string ContentKey(object mapping) => mapping.GetType().Name + JsonSerializer.Serialize(mapping, mapping.GetType());

    private static void Add(Dictionary<string, HashSet<(Guid, ControlKind, int, InputPart)>> sets, string key, (Guid, ControlKind, int, InputPart) slot)
    {
        if (!sets.TryGetValue(key, out var set)) sets[key] = set = [];
        set.Add(slot);
    }

    /// <summary>The mode picked by its switch at the last tick, or null for none.</summary>
    public string? ActiveMode { get; private set; }

    /// <summary>Whether a mapping is the one its input uses right now: its mode is the active one
    /// (or it's an any-mode mapping on an input with nothing for the active mode), and its layer is
    /// the active one (shifted while shift is held, if the input has shifted mappings in that mode).</summary>
    private bool InLayer((Guid, ControlKind, int, InputPart) slot, bool shifted, string? modeName, bool shiftHeld)
    {
        var mode = ActiveMode;
        var usesMode = mode is not null && _modeSlots.TryGetValue(mode, out var modeSlots) && modeSlots.Contains(slot);
        if (usesMode ? !string.Equals(modeName, mode, StringComparison.OrdinalIgnoreCase) : modeName is not null) return false;

        var hasShifted = usesMode
            ? _shiftedModeSlots.TryGetValue(mode!, out var shiftedSlots) && shiftedSlots.Contains(slot)
            : _shiftedSlots.Contains(slot);
        return shifted == (shiftHeld && hasShifted);
    }

    /// <summary>Changes whenever a latch flips or the profile is reloaded, so a view of LatchedSlots
    /// can tell cheaply when to look again.</summary>
    public int LatchVersion { get; private set; }

    /// <summary>Whether the shift control was held at the last tick.</summary>
    public bool ShiftHeld { get; private set; }

    /// <summary>Inputs (or parts of them) with a latching mapping that's currently latched on.</summary>
    public IReadOnlySet<(Guid DeviceGuid, ControlKind Kind, int Index, InputPart Part)> LatchedSlots() =>
        _switchStates.Where(s => s.Key.Mode == SwitchMode.Latch && s.Value.Latched).Select(s => SlotOf(s.Key)).ToHashSet();

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

    private IReadOnlyDictionary<Guid, JoystickSnapshot> _lastSnapshots = new Dictionary<Guid, JoystickSnapshot>();

    /// <summary>Where an axis was along its travel at the last tick (0..1, after calibration - the
    /// scale axis zones use), or null if the device or axis wasn't there.</summary>
    public double? AxisTravel(Guid deviceGuid, AxisType axis) =>
        _lastSnapshots.TryGetValue(deviceGuid, out var snap) && snap.Axes.TryGetValue(axis, out var raw)
            ? InputParts.TravelPosition(GetCalibration(deviceGuid, axis).Normalize(raw))
            : null;

    /// <summary>Polls every physical device once, applies the active profile, and pushes the
    /// result to vJoy and/or simulated key presses. Returns the raw per-device snapshots so the
    /// UI can render a live view.</summary>
    public IReadOnlyDictionary<Guid, JoystickSnapshot> Tick()
    {
        var now = _clock();
        var firstTick = double.IsNaN(_lastTick);
        var elapsed = firstTick ? 0 : Math.Clamp(now - _lastTick, 0, MaxNudgeStepSeconds);
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
        _lastSnapshots = snapshots;

        var shiftHeld = _profile.ShiftControl is { } shift && snapshots.TryGetValue(shift.DeviceGuid, out var shiftSnap) &&
                        IsOn(shiftSnap, shift.DeviceGuid, shift.Kind, shift.Index, shift.Part, ShiftAxisThreshold);
        ShiftHeld = shiftHeld;

        string? activeMode = null;
        foreach (var mode in _profile.Modes)
        {
            if (mode.SelectedBy is { } control && snapshots.TryGetValue(control.DeviceGuid, out var modeSnap) &&
                IsOn(modeSnap, control.DeviceGuid, control.Kind, control.Index, control.Part, ShiftAxisThreshold))
            {
                activeMode = mode.Name;
                break;
            }
        }
        if (!string.Equals(activeMode, ActiveMode, StringComparison.Ordinal))
        {
            ActiveMode = activeMode;
            if (!firstTick) ModeChanged?.Invoke(activeMode); // the mode at start-up isn't a change
        }

        var buttonAgg = new Dictionary<(uint DeviceId, int Button), bool>();
        var povAgg = new Dictionary<(uint DeviceId, int Pov), int>();
        var nudgedThisProfile = new HashSet<(uint, VJoyAxis)>();

        foreach (var binding in _profile.Bindings)
        {
            if (!snapshots.TryGetValue(binding.Source.DeviceGuid, out var snap)) continue;
            var inLayer = InLayer(SlotOf(binding), binding.Shifted, binding.ModeName, shiftHeld);

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
                    var raw = IsOn(snap, binding.Source.DeviceGuid, binding.Source.Kind, binding.Source.Index, binding.Part,
                        binding.AxisThreshold, binding.ZoneFrom, binding.ZoneTo);
                    raw = ApplyTrigger(binding, binding.Trigger, raw, now, SlotOf(binding));
                    var pressed = ApplySwitchMode(binding, raw, inLayer, now);
                    var key = (binding.Target.VJoyDeviceId, binding.Target.Index);
                    buttonAgg[key] = buttonAgg.TryGetValue(key, out var existing) ? existing || pressed : pressed;
                    break;
            }
        }

        ApplyTests(now, buttonAgg, povAgg);

        // Bindings store vJoy button/hat numbers 0-based (the UI shows them 1-based), but vJoy
        // numbers them from 1 - button 0 / hat 0 don't exist and would be silently ignored.
        foreach (var ((deviceId, button), pressed) in buttonAgg)
            _vjoy.SetButton(deviceId, button + 1, pressed);

        foreach (var ((deviceId, povIndex), direction) in povAgg)
            _vjoy.SetDiscretePov(deviceId, povIndex + 1, direction);

        foreach (var axisKey in nudgedThisProfile)
            _vjoy.SetAxis(axisKey.Item1, axisKey.Item2, _nudgedAxes.GetValueOrDefault(axisKey));

        ApplyAxisTests(now);

        if (KeyboardInjectionEnabled)
        {
            ProcessKeyBindings(snapshots, shiftHeld, now);
            PlaySequences(now);
        }
        else if (_keyStates.Values.Any(s => s.Holding) || _sequences.Count > 0)
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
    private bool IsOn(JoystickSnapshot snap, Guid deviceGuid, ControlKind kind, int index, InputPart part, double axisThreshold,
        double zoneFrom = 0, double zoneTo = 1)
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
                if (part is not (InputPart.Positive or InputPart.Negative or InputPart.Zone)) return false;
                var axisType = (AxisType)index;
                if (!snap.Axes.TryGetValue(axisType, out var raw)) return false;
                var normalized = _calibrations.GetValueOrDefault((deviceGuid, axisType), DefaultCalibration).Normalize(raw);
                return part switch
                {
                    InputPart.Zone => InputParts.InZone(InputParts.TravelPosition(normalized), zoneFrom, zoneTo),
                    InputPart.Positive => normalized >= axisThreshold,
                    _ => normalized <= -axisThreshold,
                };

            default:
                return false;
        }
    }

    /// <summary>Turns an on/off input into "pressed" as one mapping's trigger sees it: straight
    /// through, or only a short press (a pulse once it's let go), a long press (once held long
    /// enough), or a double tap (the second press). A press already held when the mapping is first
    /// seen never counts. On an input that also has a double-tap mapping, a short press waits
    /// DoubleTapSeconds after it's let go, and doesn't fire if that turns out to be a double tap.</summary>
    private bool ApplyTrigger(object output, PressTrigger trigger, bool raw, double now, (Guid, ControlKind, int, InputPart) slot)
    {
        if (trigger == PressTrigger.Press) return raw;

        if (!_triggerStates.TryGetValue(output, out var state))
        {
            state = new TriggerState { LastRaw = raw };
            _triggerStates[output] = state;
        }
        var rising = raw && !state.LastRaw;
        var falling = !raw && state.LastRaw;
        state.LastRaw = raw;
        if (rising) state.PressedAt = now;

        switch (trigger)
        {
            case PressTrigger.LongPress:
                return raw && !double.IsNaN(state.PressedAt) && now - state.PressedAt >= LongPressSeconds;

            case PressTrigger.ShortPress:
            {
                if (rising && !double.IsNaN(state.ShortPending))
                {
                    // Pressed again in time: a double tap, not a short press.
                    state.ShortPending = double.NaN;
                    state.SkipRelease = true;
                }
                if (falling)
                {
                    var wasShort = !double.IsNaN(state.PressedAt) && now - state.PressedAt < LongPressSeconds;
                    if (state.SkipRelease) state.SkipRelease = false;
                    else if (wasShort && _doubleTapSlots.Contains(slot)) state.ShortPending = now;
                    else if (wasShort) state.PulseUntil = now + PulseSeconds;
                    state.PressedAt = double.NaN;
                }
                if (!double.IsNaN(state.ShortPending) && now - state.ShortPending > DoubleTapSeconds)
                {
                    state.ShortPending = double.NaN;
                    state.PulseUntil = now + PulseSeconds;
                }
                return now < state.PulseUntil;
            }

            case PressTrigger.DoubleTap:
                if (rising)
                {
                    state.SecondTap = now - state.LastTapAt <= DoubleTapSeconds;
                    // A second tap ends the pair, so a third quick tap starts a new one.
                    state.LastTapAt = state.SecondTap ? double.NegativeInfinity : now;
                }
                if (!raw) state.SecondTap = false;
                return raw && state.SecondTap;

            default:
                return raw;
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
                if (rising && inLayer)
                {
                    state.Latched = !state.Latched;
                    LatchVersion++;
                    LatchChanged?.Invoke(binding, state.Latched);
                }
                return state.Latched;
            default:
                return raw && inLayer;
        }
    }

    private void ApplyNudge(Binding binding, JoystickSnapshot snap, bool inLayer, (uint, VJoyAxis) axisKey, double now, double elapsed)
    {
        var raw = IsOn(snap, binding.Source.DeviceGuid, binding.Source.Kind, binding.Source.Index, binding.Part,
            binding.AxisThreshold, binding.ZoneFrom, binding.ZoneTo);
        raw = ApplyTrigger(binding, binding.Trigger, raw, now, SlotOf(binding));

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
        if (binding.Nudge is AxisNudge.Center or AxisNudge.SetTo)
        {
            if (raw) value = NudgeTarget(binding);
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

    /// <summary>Where a Center or SetTo nudge puts its axis (-1..1).</summary>
    private static double NudgeTarget(Binding binding) =>
        binding.Nudge == AxisNudge.SetTo ? Math.Clamp(binding.NudgeAmount, 0, 1) * 2 - 1 : 0;

    // ---- Testing a mapping without the hardware (the remap window's Test button) ----

    /// <summary>Presses a vJoy button (0-based, like a Binding's target) for TestPressSeconds.</summary>
    public void TestButton(uint vjoyDeviceId, int button)
    {
        AcquireForTest(vjoyDeviceId);
        _testButtons[(vjoyDeviceId, button)] = _clock() + TestPressSeconds;
    }

    /// <summary>Pushes a vJoy hat up, right, down and left in turn, then lets it go.</summary>
    public void TestHat(uint vjoyDeviceId, int hat)
    {
        AcquireForTest(vjoyDeviceId);
        _testHats[(vjoyDeviceId, hat)] = _clock();
    }

    /// <summary>Sweeps a vJoy axis to one end, the other, and back to center over TestSweepSeconds.</summary>
    public void TestAxis(uint vjoyDeviceId, VJoyAxis axis)
    {
        AcquireForTest(vjoyDeviceId);
        _testAxes[(vjoyDeviceId, axis)] = _clock();
    }

    /// <summary>Does what one press of an axis-moving mapping does (a held one: half a second's worth).</summary>
    public void TestNudge(Binding binding)
    {
        if (binding.Nudge == AxisNudge.None) return;
        var key = (binding.Target.VJoyDeviceId, (VJoyAxis)binding.Target.Index);
        var value = _nudgedAxes.GetValueOrDefault(key);
        var sign = binding.Nudge == AxisNudge.Decrease ? -1 : 1;
        value = binding.Nudge switch
        {
            AxisNudge.Center or AxisNudge.SetTo => NudgeTarget(binding),
            _ => value + sign * binding.NudgeAmount * 2 * (binding.Mode == SwitchMode.Hold ? 0.5 : 1),
        };
        _nudgedAxes[key] = Math.Clamp(value, -1, 1);
        AcquireForTest(key.VJoyDeviceId);
        _vjoy.SetAxis(key.VJoyDeviceId, key.Item2, _nudgedAxes[key]);
    }

    /// <summary>Taps a key mapping's key, or plays its sequence, after delaySeconds (time to switch
    /// to the game). False (and nothing sent) while keyboard keys are off, or for a key outside the catalog.</summary>
    public bool TestKeys(KeyBinding keyBinding, double delaySeconds = 0)
    {
        if (!KeyboardInjectionEnabled) return false;
        var played = keyBinding.Sequence is { Count: > 0 }
            ? keyBinding
            : new KeyBinding { Source = keyBinding.Source, VirtualKeyCode = keyBinding.VirtualKeyCode, Sequence = [new KeyStep { Keys = [keyBinding.VirtualKeyCode] }] };
        if (played.Sequence!.Any(s => s.Keys.Any(k => !IsCatalogKey(k)))) return false;
        StartSequence(played, _clock(), delaySeconds);
        return true;
    }

    /// <summary>Takes a vJoy device a test writes to, unless the profile already has it (taking it
    /// again would reset everything it's showing).</summary>
    private void AcquireForTest(uint vjoyDeviceId)
    {
        if (!_profile.Bindings.Any(b => b.Target.VJoyDeviceId == vjoyDeviceId)) _vjoy.Acquire(vjoyDeviceId);
    }

    private void ApplyTests(double now, Dictionary<(uint DeviceId, int Button), bool> buttonAgg, Dictionary<(uint DeviceId, int Pov), int> povAgg)
    {
        foreach (var (key, until) in _testButtons.ToList())
        {
            if (now < until) buttonAgg[key] = true;
            else
            {
                buttonAgg.TryAdd(key, false); // let go, unless a mapping is pressing it
                _testButtons.Remove(key);
            }
        }
        foreach (var (key, start) in _testHats.ToList())
        {
            var step = (int)((now - start) / TestPressSeconds);
            if (step < 4) povAgg[key] = step;
            else
            {
                povAgg.TryAdd(key, -1);
                _testHats.Remove(key);
            }
        }
    }

    private void ApplyAxisTests(double now)
    {
        foreach (var (key, start) in _testAxes.ToList())
        {
            // 0 -> +1 -> -1 -> 0, each leg a third of the sweep.
            var t = (now - start) / TestSweepSeconds;
            var value = t < 1.0 / 3 ? t * 3
                : t < 2.0 / 3 ? 1 - (t - 1.0 / 3) * 6
                : t < 1 ? -1 + (t - 2.0 / 3) * 3
                : 0;
            _vjoy.SetAxis(key.Item1, key.Item2, Math.Clamp(value, -1, 1));
            if (t >= 1) _testAxes.Remove(key);
        }
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

    private void ProcessKeyBindings(IReadOnlyDictionary<Guid, JoystickSnapshot> snapshots, bool shiftHeld, double now)
    {
        foreach (var keyBinding in _profile.KeyBindings)
        {
            // Only ever inject a key from the curated catalog. A KeyBinding's VirtualKeyCode is
            // validated against this when created through the UI, but a hand-edited or shared
            // profile.json could set it to anything - re-checking here (the one place that
            // actually calls SendInput) means a malicious profile file can never make this
            // simulate a key outside the app's own approved list, however it was loaded.
            // (Key sequences are checked the same way, step by step, in StartSequence.)
            var isSequence = keyBinding.Sequence is { Count: > 0 };
            if (!isSequence && !IsCatalogKey(keyBinding.VirtualKeyCode)) continue;

            if (!snapshots.TryGetValue(keyBinding.Source.DeviceGuid, out var snap)) continue;

            var part = InputParts.Of(keyBinding);
            var raw = IsOn(snap, keyBinding.Source.DeviceGuid, keyBinding.Source.Kind, keyBinding.Source.Index, part,
                keyBinding.AxisThreshold, keyBinding.ZoneFrom, keyBinding.ZoneTo);
            raw = ApplyTrigger(keyBinding, keyBinding.Trigger, raw, now, SlotOf(keyBinding));
            var inLayer = InLayer(SlotOf(keyBinding), keyBinding.Shifted, keyBinding.ModeName, shiftHeld);
            var holds = !isSequence && (keyBinding.Source.Kind == ControlKind.Axis || keyBinding.HoldWhilePressed);

            if (!_keyStates.TryGetValue(keyBinding, out var state))
            {
                state = new KeyState { LastRaw = raw };
                _keyStates[keyBinding] = state;
                if (!holds) continue; // a tap or sequence never fires for a press already held at first sight
            }

            var rising = raw && !state.LastRaw;
            var falling = !raw && state.LastRaw;
            state.LastRaw = raw;

            if (isSequence)
            {
                if (rising && inLayer) StartSequence(keyBinding, now);
            }
            else if (holds)
            {
                var shouldHold = raw && inLayer;
                if (shouldHold && !state.Holding) _keys.KeyDown(keyBinding.VirtualKeyCode);
                else if (!shouldHold && state.Holding && !KeyHeldElsewhere(keyBinding.VirtualKeyCode, keyBinding)) _keys.KeyUp(keyBinding.VirtualKeyCode);
                state.Holding = shouldHold;
            }
            else if (inLayer && (rising || (falling && keyBinding.TapOnRelease)))
            {
                // Tap: a single down+up on the edge rather than holding the key down (the up is
                // left out if another mapping is holding that key, so it stays held).
                _keys.KeyDown(keyBinding.VirtualKeyCode);
                if (!KeyHeldElsewhere(keyBinding.VirtualKeyCode, keyBinding)) _keys.KeyUp(keyBinding.VirtualKeyCode);
            }
        }
    }

    private static bool IsCatalogKey(int virtualKeyCode) => KeyCatalog.Keys.Any(k => k.VKey == virtualKeyCode);

    /// <summary>True if a hold mapping (other than except) or a playing sequence has the key down,
    /// so letting go of it now would cut that one short.</summary>
    private bool KeyHeldElsewhere(int vkey, KeyBinding? except = null) =>
        _sequenceKeysDown.ContainsKey(vkey) ||
        _keyStates.Any(s => s.Value.Holding && !ReferenceEquals(s.Key, except) && s.Key.VirtualKeyCode == vkey);

    /// <summary>Queues a key sequence's presses and releases at their times from now. Ignored
    /// while that sequence is still playing, and refused outright if any step names a key outside
    /// the catalog (see ProcessKeyBindings).</summary>
    private void StartSequence(KeyBinding keyBinding, double now, double delaySeconds = 0)
    {
        if (_sequences.ContainsKey(keyBinding) || keyBinding.Sequence is not { Count: > 0 } steps) return;
        if (steps.Any(s => s.Keys.Any(k => !IsCatalogKey(k)))) return;

        var queue = new Queue<PendingKey>();
        var at = now + Math.Max(0, delaySeconds);
        foreach (var step in steps)
        {
            if (step.Keys.Count == 0)
            {
                at += Math.Clamp(step.WaitMs, 0, 60_000) / 1000.0;
                continue;
            }
            foreach (var key in step.Keys) queue.Enqueue(new PendingKey(at, key, Down: true));
            at += SequenceKeySeconds;
            foreach (var key in Enumerable.Reverse(step.Keys)) queue.Enqueue(new PendingKey(at, key, Down: false));
            at += SequenceGapSeconds;
        }
        if (queue.Count > 0) _sequences[keyBinding] = queue;
        PlaySequences(now);
    }

    /// <summary>Sends every queued sequence key press/release that's due.</summary>
    private void PlaySequences(double now)
    {
        foreach (var (keyBinding, queue) in _sequences.ToList())
        {
            while (queue.Count > 0 && queue.Peek().At <= now)
            {
                var pending = queue.Dequeue();
                if (pending.Down)
                {
                    _keys.KeyDown(pending.VKey);
                    _sequenceKeysDown[pending.VKey] = _sequenceKeysDown.GetValueOrDefault(pending.VKey) + 1;
                }
                else
                {
                    // Only the last sequence holding the key lets go of it, and not while a hold mapping has it down.
                    if (_sequenceKeysDown.GetValueOrDefault(pending.VKey) <= 1) _sequenceKeysDown.Remove(pending.VKey);
                    else _sequenceKeysDown[pending.VKey]--;
                    if (!KeyHeldElsewhere(pending.VKey)) _keys.KeyUp(pending.VKey);
                }
            }
            if (queue.Count == 0) _sequences.Remove(keyBinding);
        }
    }

    /// <summary>Releases every key currently held down by a KeyBinding or a playing sequence (e.g.
    /// when injection is toggled off, the profile changes, or the app is closing) and stops the
    /// sequences, so nothing gets left stuck.</summary>
    private void ReleaseAllHeldKeys()
    {
        foreach (var (keyBinding, state) in _keyStates)
        {
            if (state.Holding) _keys.KeyUp(keyBinding.VirtualKeyCode);
        }
        _keyStates.Clear();

        foreach (var key in _sequenceKeysDown.Keys) _keys.KeyUp(key);
        _sequenceKeysDown.Clear();
        _sequences.Clear();
    }

    /// <summary>DirectInput reports POV angle in hundredths of a degree (0=N, 9000=E, ...), -1 when centered.
    /// Snaps to vJoy's 4-way discrete POV (0=N,1=E,2=S,3=W, -1=neutral).</summary>
    private static int DirectInputPovToDiscrete(int raw)
    {
        if (raw < 0) return -1;
        return (int)Math.Round(raw / 9000.0) % 4;
    }
}
