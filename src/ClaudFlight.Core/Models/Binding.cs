namespace ClaudFlight.Core.Models;

public sealed class Binding
{
    public required InputRef Source { get; init; }
    public required OutputRef Target { get; init; }

    /// <summary>Which part of the source control this binding reads. Whole (the default) passes
    /// the control straight through to the same kind of vJoy control; any other part (one hat
    /// direction, one half of an axis) acts as an on/off switch and drives a vJoy button.</summary>
    public InputPart Part { get; init; } = InputPart.Whole;

    /// <summary>Positive/Negative parts only: how far (0..1, calibrated) the axis must move in
    /// that direction to count as pressed.</summary>
    public double AxisThreshold { get; init; } = 0.5;

    /// <summary>On/off sources (a button, a hat direction, an axis half) only: how the source
    /// drives its output - see SwitchMode.</summary>
    public SwitchMode Mode { get; init; } = SwitchMode.Hold;

    /// <summary>True = this mapping belongs to the shift layer: it's used while the profile's
    /// shift control is held (see Profile.ShiftControl).</summary>
    public bool Shifted { get; init; }

    /// <summary>Not None = an on/off source moving a vJoy axis (Target is that axis) instead of
    /// pressing something - e.g. trim up/down buttons. See NudgeAmount.</summary>
    public AxisNudge Nudge { get; init; } = AxisNudge.None;

    /// <summary>Nudge only: fraction of the axis's full travel (0..1) to move per second while
    /// held (Mode Hold), or per press (any other mode).</summary>
    public double NudgeAmount { get; init; } = 0.5;
}

/// <summary>How an on/off source (button, hat direction, axis half) drives its output.</summary>
public enum SwitchMode
{
    /// <summary>Output is on exactly while the source is on.</summary>
    Hold,

    /// <summary>A short press each time the source switches on.</summary>
    PulseOnPress,

    /// <summary>A short press each time the source flips, on or off - for a toggle switch driving
    /// a sim that expects a button press for each change.</summary>
    PulseOnBoth,

    /// <summary>Press once to turn the output on, press again to turn it off.</summary>
    Latch,
}

/// <summary>What an on/off source does to a vJoy axis it nudges.</summary>
public enum AxisNudge
{
    None,
    Increase,
    Decrease,
    Center,
}

/// <summary>Which control is the profile's shift: while it's held, controls with shift-layer
/// mappings use those instead of their normal ones.</summary>
public sealed record ShiftControl(Guid DeviceGuid, ControlKind Kind, int Index, InputPart Part);

/// <summary>A separately remappable part of a physical control: the whole control, one of a hat's
/// four directions, or one half of an axis.</summary>
public enum InputPart
{
    Whole,
    Up,
    Right,
    Down,
    Left,
    Positive,
    Negative,
}

public static class InputParts
{
    /// <summary>The parts a control of this kind can be remapped by.</summary>
    public static IReadOnlyList<InputPart> For(ControlKind kind) => kind switch
    {
        ControlKind.Pov => [InputPart.Whole, InputPart.Up, InputPart.Right, InputPart.Down, InputPart.Left],
        ControlKind.Axis => [InputPart.Whole, InputPart.Positive, InputPart.Negative],
        _ => [InputPart.Whole],
    };

    /// <summary>Appended to a control's name to name one of its parts, e.g. "Hat 1 ↑".</summary>
    public static string Suffix(InputPart part) => part switch
    {
        InputPart.Up => " ↑",
        InputPart.Right => " →",
        InputPart.Down => " ↓",
        InputPart.Left => " ←",
        InputPart.Positive => " +",
        InputPart.Negative => " −",
        _ => "",
    };

    public static bool IsDirection(InputPart part) => part is >= InputPart.Up and <= InputPart.Left;

    /// <summary>0=N, 1=E, 2=S, 3=W, matching KeyBinding.PovDirection.</summary>
    public static int ToPovDirection(InputPart part) => part - InputPart.Up;

    public static InputPart FromPovDirection(int direction) => InputPart.Up + Math.Clamp(direction, 0, 3);

    /// <summary>The part a keyboard binding reads (keys always need an on/off part, so a hat or
    /// axis key binding is always one direction or one half).</summary>
    public static InputPart Of(KeyBinding keyBinding) => keyBinding.Source.Kind switch
    {
        ControlKind.Pov => FromPovDirection(keyBinding.PovDirection),
        ControlKind.Axis => keyBinding.AxisAboveThreshold ? InputPart.Positive : InputPart.Negative,
        _ => InputPart.Whole,
    };

    /// <summary>True if a hat reading (hundredths of a degree, -1 = centered) is pushed toward
    /// this direction. Diagonals count for both neighbouring directions, so an 8-way hat pushed
    /// up-right presses both the Up and Right outputs.</summary>
    public static bool IsPovDirectionActive(int rawHundredthDegrees, InputPart direction)
    {
        if (rawHundredthDegrees < 0) return false;
        var angle = rawHundredthDegrees / 100.0;
        var target = ToPovDirection(direction) * 90.0;
        var difference = Math.Abs((angle - target + 540.0) % 360.0 - 180.0);
        return difference < 67.5;
    }
}

/// <summary>Calibration for one physical axis, shared by every binding that reads that axis
/// (calibrating a stick's centering/deadzone shouldn't need to be redone per virtual target).</summary>
public sealed class AxisCalibrationEntry
{
    public required Guid DeviceGuid { get; init; }
    public required AxisType Axis { get; init; }
    public required AxisCalibration Calibration { get; init; }
}

/// <summary>A user-friendly rename of one physical control (e.g. axis "RotationZ" -&gt; "Rudder",
/// or button 3 on the switch panel -&gt; "Master Battery"). Purely cosmetic - does not affect
/// how the control is read or mapped.</summary>
public sealed class InputLabelEntry
{
    public required Guid DeviceGuid { get; init; }
    public required ControlKind Kind { get; init; }
    public required int Index { get; init; }
    public required string Label { get; init; }
}

/// <summary>Maps one axis/button/POV to a simulated keyboard key press, for games/situations that
/// don't take joystick input for something but do take keyboard input. Purely additive alongside
/// vJoy Bindings - the same physical control can have both at once.</summary>
public sealed class KeyBinding
{
    public required InputRef Source { get; init; }
    public required int VirtualKeyCode { get; init; }

    /// <summary>Button/Pov only: true = the key stays held down for as long as the control is
    /// held; false = a single tap (down then up) is sent each time the control is pressed.</summary>
    public bool HoldWhilePressed { get; init; } = true;

    /// <summary>Pov only: which discrete direction (0=N, 1=E, 2=S, 3=W) activates this binding.</summary>
    public int PovDirection { get; init; }

    /// <summary>Axis only: normalized magnitude (0..1) the axis must cross to be considered
    /// "active" for key-press purposes.</summary>
    public double AxisThreshold { get; init; } = 0.5;

    /// <summary>Axis only: true = active when the calibrated value is &gt;= threshold, false =
    /// active when &lt;= -threshold.</summary>
    public bool AxisAboveThreshold { get; init; } = true;

    /// <summary>Tap mode only (HoldWhilePressed false): also tap when the control switches off,
    /// so a toggle switch taps the key on every flip.</summary>
    public bool TapOnRelease { get; init; }

    /// <summary>True = this key belongs to the shift layer (see Binding.Shifted).</summary>
    public bool Shifted { get; init; }
}

public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public List<Binding> Bindings { get; set; } = [];
    public List<KeyBinding> KeyBindings { get; set; } = [];
    public List<AxisCalibrationEntry> AxisCalibrations { get; set; } = [];
    public List<InputLabelEntry> InputLabels { get; set; } = [];

    /// <summary>User-friendly labels for physical devices (e.g. "Left Stick") keyed by InstanceGuid, since
    /// identical hardware (two T.16000M units) can't be told apart by name alone.</summary>
    public Dictionary<Guid, string> DeviceLabels { get; set; } = [];

    /// <summary>Devices the user has explicitly hidden from Live Monitor/Calibration/Mapping
    /// (e.g. a stray HID interface from an RGB mouse or headset that isn't a real game
    /// controller), keyed by InstanceGuid. Hidden devices are still detected but ignored.</summary>
    public List<Guid> ExcludedDeviceGuids { get; set; } = [];

    /// <summary>User-chosen display order for device cards in Live Monitor (drag/drop), by
    /// InstanceGuid. Devices not listed here (newly connected, never reordered) are appended
    /// after the ordered ones in their natural detection order.</summary>
    public List<Guid> DeviceOrder { get; set; } = [];

    /// <summary>Individual axes/buttons/POVs the user has hidden from Live Monitor and
    /// Calibration for a still-visible device (e.g. buttons that don't exist on the panel, or
    /// axes not worth calibrating). Purely a view preference - existing mappings using a hidden
    /// control keep working, since the mapping engine reads hardware directly and never
    /// consults this list.</summary>
    public List<ExcludedControlEntry> ExcludedControls { get; set; } = [];

    /// <summary>Raw Input keyboard-type devices (macro keypads, streaming panels, or just the
    /// regular keyboard) ever seen before, by their stable device-path-derived guid. The first
    /// time a new one shows up it's auto-added here AND to ExcludedDeviceGuids, so Live Monitor
    /// doesn't get flooded with every keyboard-type device on the system by default - only ones
    /// the user explicitly restores stay visible. Devices already in this list are left alone
    /// (so restoring one doesn't get immediately re-hidden on the next refresh).</summary>
    public List<Guid> KnownRawInputDeviceGuids { get; set; } = [];

    /// <summary>Virtual-key codes learned for each Raw Input keyboard-type device, in the order
    /// first seen - list order is the button index everything else (bindings, calibration rows)
    /// keys on, so this is what makes a device's button numbering stable across restarts instead
    /// of starting back at zero known buttons every launch.</summary>
    public List<RawInputLearnedKey> RawInputLearnedKeys { get; set; } = [];

    /// <summary>Per-device overrides for how a Raw Input mouse-style device's native axes are
    /// exposed (see AxisReportMode) - e.g. relabeling a dial's wheel axis to a different slot, or
    /// turning it into two buttons instead of a continuous axis.</summary>
    public List<AxisModeEntry> AxisModes { get; set; } = [];

    /// <summary>Pictures shown on buttons that have their own screen (StreamDock panel keys).</summary>
    public List<ButtonImageEntry> ButtonImages { get; set; } = [];

    /// <summary>The Layout tab's pages: background pictures with control markers placed on them.</summary>
    public List<LayoutPage> LayoutPages { get; set; } = [];

    /// <summary>The control that switches to the shift layer while held, or null for none.</summary>
    public ShiftControl? ShiftControl { get; set; }

    /// <summary>Per-panel options for StreamDock panels (key name pictures, press highlight).</summary>
    public List<KeyScreenSettings> KeyScreenSettings { get; set; } = [];
}

/// <summary>Options for one StreamDock panel's key screens.</summary>
public sealed class KeyScreenSettings
{
    public required Guid DeviceGuid { get; init; }

    /// <summary>Keys without a picture of their own show their name (and what they're mapped to).</summary>
    public bool ShowNames { get; set; }

    /// <summary>Keys without a pressed picture of their own light up while held.</summary>
    public bool HighlightPressed { get; set; }
}

/// <summary>The picture shown on one button's screen: a JPEG already sized for that device's
/// screens (stored base64-encoded in the profile's JSON).</summary>
public sealed class ButtonImageEntry
{
    public required Guid DeviceGuid { get; init; }
    public required int ButtonIndex { get; init; }

    /// <summary>The normal picture, or null if this entry only sets a pressed picture.</summary>
    public byte[]? Jpeg { get; set; }

    /// <summary>Shown instead while the key is held, or null for no change when pressed.</summary>
    public byte[]? PressedJpeg { get; set; }
}

/// <summary>One virtual-key code a specific Raw Input device has been seen to send.</summary>
public sealed class RawInputLearnedKey
{
    public required Guid DeviceGuid { get; init; }
    public required int VKey { get; init; }
}

/// <summary>One axis/button/POV hidden from view on an otherwise-visible device.</summary>
public sealed class ExcludedControlEntry
{
    public required Guid DeviceGuid { get; init; }
    public required ControlKind Kind { get; init; }
    public required int Index { get; init; }
}

/// <summary>How a Raw Input mouse-style device's native axis (its X/Y movement or wheel, each
/// reported as a relative per-poll delta) should be exposed: as a continuous axis - optionally
/// under a different AxisType slot than its native one, e.g. exposing a dial's "wheel" delta as
/// Slider0 so it calibrates/binds like a rotary control instead - or converted into two buttons
/// (one that pulses true for a poll when the delta goes positive, one for negative), which suits a
/// detent-based dial better than a continuous axis for things like flap/trim step actions.</summary>
public enum AxisReportMode
{
    Axis,
    Buttons,
}

/// <summary>Per-device override for how one native axis (see AxisReportMode) is exposed. Entries
/// are only meaningful for Raw Input mouse-style devices (RawInputMouseDevice/
/// RawInputCombinedDevice); other device types always report axes as-is.</summary>
public sealed class AxisModeEntry
{
    public required Guid DeviceGuid { get; init; }
    public required AxisType NativeAxis { get; init; }
    public AxisReportMode Mode { get; init; } = AxisReportMode.Axis;

    /// <summary>Axis mode only: which AxisType slot this native axis's value is reported under -
    /// defaults to NativeAxis itself (no relabeling).</summary>
    public AxisType ReportAs { get; init; }
}
