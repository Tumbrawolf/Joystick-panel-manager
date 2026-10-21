using ClaudFlight.Core.Models;
using ClaudFlight.Core.RawInput;

namespace ClaudFlight.Core.Devices;

/// <summary>A mouse-type device seen via Raw Input - every physical mouse-class HID device (your
/// actual mouse, or a macro pad's dial/jog wheel that happens to emulate a mouse rather than a
/// keyboard) gets its own instance of this, told apart from every other one. This replaces the old
/// DirectInput-based system "Mouse" device entirely: DirectInput's own mouse support is itself
/// raw-input-backed, and Windows only allows one raw-input mouse registration per process, so
/// acquiring a DirectInput mouse device silently steals the registration this class needs and
/// starves it of input - the two can't coexist. Reports X/Y movement and the wheel as axes (relative
/// per-poll deltas, same convention the old device used) and the 5 standard buttons
/// (Left/Right/Middle/X1/X2). Has no POVs.</summary>
public sealed class RawInputMouseDevice : IPhysicalDevice
{
    private static readonly AxisType[] Axes = [AxisType.X, AxisType.Y, AxisType.Z];
    private static readonly string[] ButtonNames = ["Left", "Right", "Middle", "X1", "X2"];

    private readonly RawInputKeyboardManager _manager;

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public int ButtonCount => ButtonNames.Length;
    public int PovCount => 0;
    public IReadOnlyList<AxisType> PresentAxes => Axes;
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames { get; } = new Dictionary<AxisType, string>
    {
        [AxisType.X] = "X",
        [AxisType.Y] = "Y",
        [AxisType.Z] = "Wheel",
    };
    public IReadOnlyDictionary<int, string> ButtonDeviceNames { get; } = BuildButtonNames();
    public bool IsLikelyGameController => true;

    internal RawInputMouseDevice(Guid instanceGuid, string name, RawInputKeyboardManager manager)
    {
        InstanceGuid = instanceGuid;
        Name = name;
        _manager = manager;
    }

    public JoystickSnapshot Poll()
    {
        var axes = new Dictionary<AxisType, int>
        {
            [AxisType.X] = _manager.DrainXDelta(InstanceGuid),
            [AxisType.Y] = _manager.DrainYDelta(InstanceGuid),
            [AxisType.Z] = _manager.DrainWheelDelta(InstanceGuid),
        };

        var buttons = new bool[ButtonCount];
        for (var i = 0; i < buttons.Length; i++) buttons[i] = _manager.IsMouseButtonPressed(InstanceGuid, i);

        return new JoystickSnapshot { Axes = axes, Buttons = buttons, PointOfViews = [] };
    }

    public void Dispose()
    {
        // No per-device native resource: the manager owns the raw input window and device state.
    }

    private static Dictionary<int, string> BuildButtonNames()
    {
        var names = new Dictionary<int, string>();
        for (var i = 0; i < ButtonNames.Length; i++) names[i] = ButtonNames[i];
        return names;
    }
}
