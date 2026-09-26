using ClaudFlight.Core.Models;
using Vortice.DirectInput;

namespace ClaudFlight.Core.Devices;

/// <summary>A physical HID game controller (stick, throttle, panel) exposed via DirectInput.</summary>
public sealed class PhysicalDevice : IPhysicalDevice
{
    /// <summary>Maps DirectInput's fixed per-axis-type object GUIDs to our AxisType. This is the
    /// reliable way to identify which physical control is which - byte offsets and HID usage
    /// codes are NOT reliable for this (verified empirically: different sticks put wildly
    /// different physical controls, e.g. a throttle wheel vs. a rotary dial, at the same offset).
    /// A device's actual "Slider"-type axis object(s) get matched separately below since two
    /// distinct axes (Slider0/Slider1) share the same ObjectGuid.Slider type.</summary>
    private static readonly Dictionary<Guid, AxisType> AxisTypeByObjectGuid = new()
    {
        [ObjectGuid.XAxis] = AxisType.X,
        [ObjectGuid.YAxis] = AxisType.Y,
        [ObjectGuid.ZAxis] = AxisType.Z,
        [ObjectGuid.RxAxis] = AxisType.RotationX,
        [ObjectGuid.RyAxis] = AxisType.RotationY,
        [ObjectGuid.RzAxis] = AxisType.RotationZ,
    };

    private readonly IDirectInputDevice8 _device;
    private readonly AxisType[] _presentAxes;
    private readonly Dictionary<AxisType, string> _axisDeviceNames;

    public Guid InstanceGuid { get; }
    public Guid ProductGuid { get; }
    public string Name { get; }
    public int ButtonCount { get; }
    public int PovCount { get; }
    public IReadOnlyList<AxisType> PresentAxes => _presentAxes;

    /// <summary>False for HID interfaces that expose buttons/axes but aren't actually game
    /// controllers - e.g. a gaming mouse's macro/RGB control channel, or a headset's volume
    /// buttons. Detected by the device's top-level HID collection usage: real controllers report
    /// "Generic Desktop" (page 1) with a control-type usage; these report Consumer Control
    /// (page 12) or System Control (page 1, usage >= 0x80) instead.</summary>
    public bool IsLikelyGameController { get; } = true;

    /// <summary>The device's own friendly name for each present axis (e.g. "Dial", "Slider",
    /// "Y Rotation") - a much better default label than the generic AxisType name.</summary>
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames => _axisDeviceNames;

    internal PhysicalDevice(IDirectInputDevice8 device, DeviceInstance instance, nint windowHandle)
    {
        _device = device;
        InstanceGuid = instance.InstanceGuid;
        ProductGuid = instance.ProductGuid;
        Name = instance.InstanceName;

        _device.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
        _device.SetDataFormat<RawJoystickState>();

        var caps = _device.Capabilities;
        ButtonCount = caps.ButtonCount;
        PovCount = caps.PovCount;

        try
        {
            var collections = _device.GetObjects(DeviceObjectTypeFlags.Collection);
            if (collections.Count > 0 && (collections[0].UsagePage != 1 || collections[0].Usage >= 0x80))
            {
                IsLikelyGameController = false;
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Couldn't tell; don't penalize the device for it.
        }

        var present = new List<AxisType>();
        _axisDeviceNames = [];
        var sliderCount = 0;
        foreach (var obj in _device.GetObjects(DeviceObjectTypeFlags.Axis))
        {
            AxisType? type = null;
            if (AxisTypeByObjectGuid.TryGetValue(obj.ObjectType, out var known))
            {
                type = known;
            }
            else if (obj.ObjectType == ObjectGuid.Slider && sliderCount < 2)
            {
                type = sliderCount == 0 ? AxisType.Slider0 : AxisType.Slider1;
                sliderCount++;
            }

            if (type is null) continue;

            present.Add(type.Value);
            _axisDeviceNames[type.Value] = obj.Name;

            try
            {
                var props = _device.GetObjectPropertiesById(obj.ObjectId);
                props.Range = new InputRange(PhysicalAxisRange.Min, PhysicalAxisRange.Max);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // Couldn't set this axis's range; it'll just report DirectInput's default range instead.
            }
        }
        _presentAxes = present.ToArray();

        _device.Acquire();
    }

    public JoystickSnapshot Poll()
    {
        JoystickState state;
        try
        {
            // Poll() itself can throw a transient DIERR_INPUTLOST-style error (more common on
            // some simpler/older HID devices) - previously only GetCurrentState was guarded,
            // so an unhandled throw here would abort this device's update for the whole tick
            // (its Live Monitor state would sit frozen until a later poll happened to succeed).
            _device.Poll();
            state = _device.GetCurrentState<JoystickState, RawJoystickState, JoystickUpdate>();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Device was unplugged or lost acquisition; try to reacquire for next poll.
            try { _device.Acquire(); } catch (SharpGen.Runtime.SharpGenException) { /* still gone */ }
            return JoystickSnapshot.Empty;
        }

        var axes = new Dictionary<AxisType, int>();
        foreach (var type in _presentAxes)
        {
            axes[type] = type switch
            {
                AxisType.X => state.X,
                AxisType.Y => state.Y,
                AxisType.Z => state.Z,
                AxisType.RotationX => state.RotationX,
                AxisType.RotationY => state.RotationY,
                AxisType.RotationZ => state.RotationZ,
                AxisType.Slider0 => state.Sliders.Length > 0 ? state.Sliders[0] : 0,
                AxisType.Slider1 => state.Sliders.Length > 1 ? state.Sliders[1] : 0,
                _ => 0,
            };
        }

        var buttons = state.Buttons.Take(ButtonCount).ToArray();
        var povs = state.PointOfViewControllers.Take(PovCount).ToArray();

        return new JoystickSnapshot { Axes = axes, Buttons = buttons, PointOfViews = povs };
    }

    public void Dispose()
    {
        try { _device.Unacquire(); } catch (SharpGen.Runtime.SharpGenException) { }
        _device.Dispose();
    }
}
