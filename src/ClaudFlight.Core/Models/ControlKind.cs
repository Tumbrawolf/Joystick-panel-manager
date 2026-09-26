namespace ClaudFlight.Core.Models;

public enum ControlKind
{
    Axis,
    Button,
    Pov,
}

/// <summary>Identifies one control on a physical device.</summary>
/// <param name="Index">AxisType cast to int for Axis; 0-based button index for Button; 0-based POV index for Pov.</param>
public sealed record InputRef(Guid DeviceGuid, ControlKind Kind, int Index);

/// <summary>Identifies one control on a virtual (vJoy) device.</summary>
/// <param name="Index">HID usage code (see VJoyAxis) for Axis; 1-based button number for Button; 0-based POV index for Pov.</param>
public sealed record OutputRef(uint VJoyDeviceId, ControlKind Kind, int Index);

/// <summary>HID usage codes vJoy expects for SetAxis.</summary>
public enum VJoyAxis
{
    X = 0x30,
    Y = 0x31,
    Z = 0x32,
    RotationX = 0x33,
    RotationY = 0x34,
    RotationZ = 0x35,
    Slider0 = 0x36,
    Slider1 = 0x37,
}
