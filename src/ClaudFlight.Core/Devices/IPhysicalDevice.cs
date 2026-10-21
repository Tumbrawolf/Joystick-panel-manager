using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Devices;

/// <summary>A physical input device - a HID game controller (PhysicalDevice) from DeviceManager, or
/// a keyboard-/mouse-type device (RawInputKeyboardDevice/RawInputMouseDevice) from
/// RawInputKeyboardManager, which also covers the system mouse. Everything downstream (calibration,
/// mapping, the UI) works against this interface and doesn't need to know which kind of device it's
/// talking to.</summary>
public interface IPhysicalDevice : IDisposable
{
    Guid InstanceGuid { get; }
    string Name { get; }
    int ButtonCount { get; }
    int PovCount { get; }
    IReadOnlyList<AxisType> PresentAxes { get; }

    /// <summary>The device's own friendly name for each present axis (e.g. "Dial", "Mouse Wheel").</summary>
    IReadOnlyDictionary<AxisType, string> AxisDeviceNames { get; }

    /// <summary>The device's own friendly name for each button, by index (e.g. a Raw Input
    /// keyboard-type device names each button after the key it sends, like "F13"). Empty for
    /// devices where a button is just a number (regular joysticks/mouse).</summary>
    IReadOnlyDictionary<int, string> ButtonDeviceNames { get; }

    /// <summary>False for HID interfaces that expose buttons/axes but aren't actually game
    /// controllers (see PhysicalDevice's implementation for details). Always true for the mouse.</summary>
    bool IsLikelyGameController { get; }

    JoystickSnapshot Poll();
}
