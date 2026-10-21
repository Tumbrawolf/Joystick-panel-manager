using ClaudFlight.Core.Models;
using ClaudFlight.Core.RawInput;

namespace ClaudFlight.Core.Devices;

/// <summary>A single physical device that exposes both keyboard-type and mouse-type Raw Input
/// interfaces under the same VID/PID (e.g. a macro keypad whose buttons report as keyboard VK codes
/// while its jog dial reports as mouse wheel movement - sometimes on more than one keyboard-type
/// interface at once) - RawInputKeyboardManager.Refresh merges these into one of these instead of
/// showing up as separate, unrelated-looking device cards. Buttons are each keyboard interface's
/// learned buttons, in order, followed by the mouse interface's 5 standard buttons; axes are the
/// mouse interface's X/Y/wheel.</summary>
public sealed class RawInputCombinedDevice : IPhysicalDevice
{
    private static readonly AxisType[] Axes = [AxisType.X, AxisType.Y, AxisType.Z];
    private static readonly string[] MouseButtonNames = ["Left", "Right", "Middle", "X1", "X2"];

    private readonly RawInputKeyboardManager _manager;
    private readonly IReadOnlyList<Guid> _keyboardGuids;
    private readonly Guid _mouseGuid;

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public int ButtonCount => _keyboardGuids.Sum(_manager.GetButtonCount) + MouseButtonNames.Length;
    public int PovCount => 0;
    public IReadOnlyList<AxisType> PresentAxes => Axes;
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames { get; } = new Dictionary<AxisType, string>
    {
        [AxisType.X] = "X",
        [AxisType.Y] = "Y",
        [AxisType.Z] = "Wheel",
    };
    public IReadOnlyDictionary<int, string> ButtonDeviceNames => BuildButtonNames();
    public bool IsLikelyGameController => true;

    internal RawInputCombinedDevice(Guid instanceGuid, string name, IReadOnlyList<Guid> keyboardGuids, Guid mouseGuid, RawInputKeyboardManager manager)
    {
        InstanceGuid = instanceGuid;
        Name = name;
        _keyboardGuids = keyboardGuids;
        _mouseGuid = mouseGuid;
        _manager = manager;
    }

    public JoystickSnapshot Poll()
    {
        var axes = new Dictionary<AxisType, int>
        {
            [AxisType.X] = _manager.DrainXDelta(_mouseGuid),
            [AxisType.Y] = _manager.DrainYDelta(_mouseGuid),
            [AxisType.Z] = _manager.DrainWheelDelta(_mouseGuid),
        };

        var buttons = new bool[ButtonCount];
        var offset = 0;
        foreach (var keyboardGuid in _keyboardGuids)
        {
            var count = _manager.GetButtonCount(keyboardGuid);
            for (var i = 0; i < count; i++) buttons[offset + i] = _manager.IsPressed(keyboardGuid, i);
            offset += count;
        }
        for (var i = 0; i < MouseButtonNames.Length; i++) buttons[offset + i] = _manager.IsMouseButtonPressed(_mouseGuid, i);

        return new JoystickSnapshot { Axes = axes, Buttons = buttons, PointOfViews = [] };
    }

    public void Dispose()
    {
        // No per-device native resource: the manager owns the raw input window and device state.
    }

    private Dictionary<int, string> BuildButtonNames()
    {
        var names = new Dictionary<int, string>();
        var offset = 0;
        foreach (var keyboardGuid in _keyboardGuids)
        {
            foreach (var (index, label) in _manager.GetButtonNames(keyboardGuid))
            {
                names[offset + index] = label;
            }
            offset += _manager.GetButtonCount(keyboardGuid);
        }
        for (var i = 0; i < MouseButtonNames.Length; i++) names[offset + i] = MouseButtonNames[i];
        return names;
    }
}
