using ClaudFlight.Core.Models;
using ClaudFlight.Core.RawInput;

namespace ClaudFlight.Core.Devices;

/// <summary>A single physical device that exposes both keyboard-type and mouse-type Raw Input
/// interfaces under the same VID/PID (e.g. a macro keypad whose buttons report as keyboard VK codes
/// while its jog dial reports as mouse wheel movement - sometimes on more than one keyboard-type
/// interface at once) - RawInputKeyboardManager.Refresh merges these into one of these instead of
/// showing up as separate, unrelated-looking device cards. Buttons are each keyboard interface's
/// learned buttons, in order, followed by the mouse interface's 5 standard buttons, followed by two
/// buttons per native mouse axis that's in Buttons mode (see RawInputMouseDevice/
/// RawInputKeyboardManager.GetAxisMode); axes are whichever native mouse axes (X/Y/wheel) are still
/// in Axis mode, under their (possibly relabeled) AxisType slot.</summary>
public sealed class RawInputCombinedDevice : IPhysicalDevice
{
    private static readonly AxisType[] NativeAxes = [AxisType.X, AxisType.Y, AxisType.Z];
    private static readonly Dictionary<AxisType, string> NativeAxisNames = new()
    {
        [AxisType.X] = "X",
        [AxisType.Y] = "Y",
        [AxisType.Z] = "Wheel",
    };
    private static readonly string[] MouseButtonNames = ["Left", "Right", "Middle", "X1", "X2"];

    private readonly RawInputKeyboardManager _manager;
    private readonly IReadOnlyList<Guid> _keyboardGuids;
    private readonly Guid _mouseGuid;

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public int ButtonCount => _keyboardGuids.Sum(_manager.GetButtonCount) + MouseButtonNames.Length + AxesInButtonsMode().Count * 2;
    public int PovCount => 0;

    public IReadOnlyList<AxisType> PresentAxes
    {
        get
        {
            var seen = new List<AxisType>();
            foreach (var native in NativeAxes)
            {
                var mode = _manager.GetAxisMode(InstanceGuid, native);
                if (mode.Mode == AxisReportMode.Axis && !seen.Contains(mode.ReportAs)) seen.Add(mode.ReportAs);
            }
            return seen;
        }
    }

    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames
    {
        get
        {
            var names = new Dictionary<AxisType, string>();
            foreach (var native in NativeAxes)
            {
                var mode = _manager.GetAxisMode(InstanceGuid, native);
                if (mode.Mode == AxisReportMode.Axis) names[mode.ReportAs] = NativeAxisNames[native];
            }
            return names;
        }
    }

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
        var deltas = new Dictionary<AxisType, int>
        {
            [AxisType.X] = _manager.DrainXDelta(_mouseGuid),
            [AxisType.Y] = _manager.DrainYDelta(_mouseGuid),
            [AxisType.Z] = _manager.DrainWheelDelta(_mouseGuid),
        };

        var axes = new Dictionary<AxisType, int>();
        var extraButtons = new List<bool>();
        foreach (var native in NativeAxes)
        {
            var mode = _manager.GetAxisMode(InstanceGuid, native);
            var delta = deltas[native];
            if (mode.Mode == AxisReportMode.Axis) axes[mode.ReportAs] = delta;
            else
            {
                extraButtons.Add(delta > 0);
                extraButtons.Add(delta < 0);
            }
        }

        var buttons = new bool[ButtonCount];
        var offset = 0;
        foreach (var keyboardGuid in _keyboardGuids)
        {
            var count = _manager.GetButtonCount(keyboardGuid);
            for (var i = 0; i < count; i++) buttons[offset + i] = _manager.IsPressed(keyboardGuid, i);
            offset += count;
        }
        for (var i = 0; i < MouseButtonNames.Length; i++) buttons[offset + i] = _manager.IsMouseButtonPressed(_mouseGuid, i);
        offset += MouseButtonNames.Length;
        for (var i = 0; i < extraButtons.Count; i++) buttons[offset + i] = extraButtons[i];

        return new JoystickSnapshot { Axes = axes, Buttons = buttons, PointOfViews = [] };
    }

    public void Dispose()
    {
        // No per-device native resource: the manager owns the raw input window and device state.
    }

    private List<AxisType> AxesInButtonsMode() =>
        NativeAxes.Where(a => _manager.GetAxisMode(InstanceGuid, a).Mode == AxisReportMode.Buttons).ToList();

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
        offset += MouseButtonNames.Length;

        foreach (var native in AxesInButtonsMode())
        {
            var label = NativeAxisNames[native];
            names[offset] = $"{label} +";
            names[offset + 1] = $"{label} -";
            offset += 2;
        }
        return names;
    }
}
