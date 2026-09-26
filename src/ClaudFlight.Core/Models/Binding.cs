namespace ClaudFlight.Core.Models;

public sealed class Binding
{
    public required InputRef Source { get; init; }
    public required OutputRef Target { get; init; }
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
}

/// <summary>One axis/button/POV hidden from view on an otherwise-visible device.</summary>
public sealed class ExcludedControlEntry
{
    public required Guid DeviceGuid { get; init; }
    public required ControlKind Kind { get; init; }
    public required int Index { get; init; }
}
