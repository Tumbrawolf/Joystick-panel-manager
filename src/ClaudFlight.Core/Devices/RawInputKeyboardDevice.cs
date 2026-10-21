using ClaudFlight.Core.Models;
using ClaudFlight.Core.RawInput;

namespace ClaudFlight.Core.Devices;

/// <summary>A keyboard-type device seen via Raw Input (e.g. a macro keypad or streaming panel
/// that identifies itself as a keyboard) - exposed as ordinary buttons, one per key the device
/// has actually been seen to send (learned dynamically by RawInputKeyboardManager, not assumed
/// up front). Has no axes or POVs.</summary>
public sealed class RawInputKeyboardDevice : IPhysicalDevice
{
    private static readonly Dictionary<AxisType, string> NoAxisNames = [];
    private static readonly Dictionary<AxisType, int> NoAxes = [];

    private readonly RawInputKeyboardManager _manager;

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public int ButtonCount => _manager.GetButtonCount(InstanceGuid);
    public int PovCount => 0;
    public IReadOnlyList<AxisType> PresentAxes => [];
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames => NoAxisNames;
    public IReadOnlyDictionary<int, string> ButtonDeviceNames => _manager.GetButtonNames(InstanceGuid);
    public bool IsLikelyGameController => true;

    internal RawInputKeyboardDevice(Guid instanceGuid, string name, RawInputKeyboardManager manager)
    {
        InstanceGuid = instanceGuid;
        Name = name;
        _manager = manager;
    }

    public JoystickSnapshot Poll()
    {
        var count = ButtonCount;
        var buttons = new bool[count];
        for (var i = 0; i < count; i++) buttons[i] = _manager.IsPressed(InstanceGuid, i);

        return new JoystickSnapshot { Axes = NoAxes, Buttons = buttons, PointOfViews = [] };
    }

    public void Dispose()
    {
        // No per-device native resource: the manager owns the raw input window and device state.
    }
}
