using Vortice.DirectInput;

namespace ClaudFlight.Core.Devices;

/// <summary>Enumerates and owns the physical input devices attached to the system - HID game
/// controllers plus the mouse.</summary>
public sealed class DeviceManager : IDisposable
{
    private readonly IDirectInput8 _directInput;
    private readonly List<IPhysicalDevice> _devices = [];

    public IReadOnlyList<IPhysicalDevice> Devices => _devices;

    public DeviceManager()
    {
        _directInput = DInput.DirectInput8Create();
    }

    /// <summary>Re-enumerates attached devices. Call once at startup and whenever hardware changes.
    /// Uses DeviceClass.All rather than GameControl: many modern HID button/switch panels (e.g. the
    /// Saitek/Logitech Pro Flight Switch Panel) report as the generic DeviceType.Device rather than
    /// Joystick/Gamepad, so the narrower GameControl category silently misses them. The system mouse
    /// gets its own wrapper (MousePhysicalDevice) since it reports relative movement rather than
    /// absolute axis position. The system Keyboard pseudo-device is excluded - Windows only exposes
    /// one combined keyboard stream to DirectInput, so per-device keyboard remapping isn't supported.
    /// We also exclude anything with no controls at all, an implausible button count, or a HID
    /// collection usage that marks it as not really a game controller (see
    /// PhysicalDevice.IsLikelyGameController - filters out things like a gaming mouse's macro/RGB
    /// control interface, which otherwise looks like a small button box to DirectInput).</summary>
    public void Refresh(nint windowHandle)
    {
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();

        foreach (var instance in _directInput.GetDevices(DeviceClass.All, DeviceEnumerationFlags.AttachedOnly))
        {
            if (instance.Type is DeviceType.Keyboard) continue;

            var rawDevice = _directInput.CreateDevice(instance.InstanceGuid);

            if (instance.Type is DeviceType.Mouse)
            {
                _devices.Add(new MousePhysicalDevice(rawDevice, instance, windowHandle));
                continue;
            }

            var device = new PhysicalDevice(rawDevice, instance, windowHandle);

            var controlCount = device.PresentAxes.Count + device.ButtonCount + device.PovCount;
            if (controlCount == 0 || device.ButtonCount > 128 || !device.IsLikelyGameController)
            {
                device.Dispose();
                continue;
            }

            _devices.Add(device);
        }
    }

    public void Dispose()
    {
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
        _directInput.Dispose();
    }
}
