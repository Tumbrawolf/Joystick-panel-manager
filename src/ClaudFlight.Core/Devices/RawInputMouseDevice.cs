using ClaudFlight.Core.Models;
using ClaudFlight.Core.RawInput;

namespace ClaudFlight.Core.Devices;

/// <summary>A mouse-type device seen via Raw Input - every physical mouse-class HID device (your
/// actual mouse, or a macro pad's dial/jog wheel that happens to emulate a mouse rather than a
/// keyboard) gets its own instance of this, told apart from every other one. This replaces the old
/// DirectInput-based system "Mouse" device entirely: DirectInput's own mouse support is itself
/// raw-input-backed, and Windows only allows one raw-input mouse registration per process, so
/// acquiring a DirectInput mouse device silently steals the registration this class needs and
/// starves it of input - the two can't coexist. Reports X/Y movement and the wheel as its three
/// native axes (relative per-poll deltas, same convention the old device used), each independently
/// exposed either as a continuous axis (optionally relabeled to a different AxisType slot) or as
/// two buttons (see RawInputKeyboardManager.GetAxisMode/SetAxisMode) - useful for a dial better
/// suited to discrete step actions than a scroll-style axis. Also reports the 5 standard buttons
/// (Left/Right/Middle/X1/X2). Has no POVs.</summary>
public sealed class RawInputMouseDevice : IPhysicalDevice
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

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public int ButtonCount => MouseButtonNames.Length + AxesInButtonsMode().Count * 2;
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

    internal RawInputMouseDevice(Guid instanceGuid, string name, RawInputKeyboardManager manager)
    {
        InstanceGuid = instanceGuid;
        Name = name;
        _manager = manager;
    }

    public JoystickSnapshot Poll()
    {
        var deltas = new Dictionary<AxisType, int>
        {
            [AxisType.X] = _manager.DrainXDelta(InstanceGuid),
            [AxisType.Y] = _manager.DrainYDelta(InstanceGuid),
            [AxisType.Z] = _manager.DrainWheelDelta(InstanceGuid),
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

        var buttons = new bool[MouseButtonNames.Length + extraButtons.Count];
        for (var i = 0; i < MouseButtonNames.Length; i++) buttons[i] = _manager.IsMouseButtonPressed(InstanceGuid, i);
        for (var i = 0; i < extraButtons.Count; i++) buttons[MouseButtonNames.Length + i] = extraButtons[i];

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
        for (var i = 0; i < MouseButtonNames.Length; i++) names[i] = MouseButtonNames[i];

        var offset = MouseButtonNames.Length;
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
