using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Devices;

/// <summary>A physical input device DeviceManager can poll - a HID game controller (PhysicalDevice)
/// or the system mouse (MousePhysicalDevice). Everything downstream (calibration, mapping, the UI)
/// works against this interface and doesn't need to know which kind of device it's talking to.</summary>
public interface IPhysicalDevice : IDisposable
{
    Guid InstanceGuid { get; }
    Guid ProductGuid { get; }
    string Name { get; }
    int ButtonCount { get; }
    int PovCount { get; }
    IReadOnlyList<AxisType> PresentAxes { get; }

    /// <summary>The device's own friendly name for each present axis (e.g. "Dial", "Mouse Wheel").</summary>
    IReadOnlyDictionary<AxisType, string> AxisDeviceNames { get; }

    /// <summary>False for HID interfaces that expose buttons/axes but aren't actually game
    /// controllers (see PhysicalDevice's implementation for details). Always true for the mouse.</summary>
    bool IsLikelyGameController { get; }

    JoystickSnapshot Poll();
}
