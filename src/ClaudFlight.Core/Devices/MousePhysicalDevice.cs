using ClaudFlight.Core.Models;
using Vortice.DirectInput;

namespace ClaudFlight.Core.Devices;

/// <summary>The system mouse, exposed the same way a joystick is: X/Y movement and the wheel as
/// "axes" (reported as relative per-poll deltas rather than an absolute stick position - the
/// existing calibration UI's Min/Center/Max still works fine for shaping that, it just represents
/// movement speed rather than a resting position) and its buttons as ordinary buttons.</summary>
public sealed class MousePhysicalDevice : IPhysicalDevice
{
    private static readonly AxisType[] Axes = [AxisType.X, AxisType.Y, AxisType.Z];

    private readonly IDirectInputDevice8 _device;

    public Guid InstanceGuid { get; }
    public Guid ProductGuid { get; }
    public string Name { get; }
    public int ButtonCount { get; }
    public int PovCount => 0;
    public IReadOnlyList<AxisType> PresentAxes => Axes;
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames { get; } = new Dictionary<AxisType, string>
    {
        [AxisType.X] = "Mouse X",
        [AxisType.Y] = "Mouse Y",
        [AxisType.Z] = "Mouse Wheel",
    };
    public bool IsLikelyGameController => true;

    internal MousePhysicalDevice(IDirectInputDevice8 device, DeviceInstance instance, nint windowHandle)
    {
        _device = device;
        InstanceGuid = instance.InstanceGuid;
        ProductGuid = instance.ProductGuid;
        Name = instance.InstanceName;

        _device.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
        _device.SetDataFormat<RawMouseState>();

        ButtonCount = _device.Capabilities.ButtonCount;

        _device.Acquire();
    }

    public JoystickSnapshot Poll()
    {
        MouseState state;
        try
        {
            _device.Poll();
            state = _device.GetCurrentMouseState();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            try { _device.Acquire(); } catch (SharpGen.Runtime.SharpGenException) { /* still gone */ }
            return JoystickSnapshot.Empty;
        }

        var axes = new Dictionary<AxisType, int>
        {
            [AxisType.X] = state.X,
            [AxisType.Y] = state.Y,
            [AxisType.Z] = state.Z,
        };
        var buttons = state.Buttons.Take(ButtonCount).ToArray();

        return new JoystickSnapshot { Axes = axes, Buttons = buttons, PointOfViews = [] };
    }

    public void Dispose()
    {
        try { _device.Unacquire(); } catch (SharpGen.Runtime.SharpGenException) { }
        _device.Dispose();
    }
}
