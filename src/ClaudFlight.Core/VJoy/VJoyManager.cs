using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.VJoy;

/// <summary>Thin, availability-safe wrapper over the vJoy virtual joystick driver.
/// If the driver isn't installed, IsAvailable is false and all Set* calls are no-ops.</summary>
public sealed class VJoyManager : IDisposable
{
    public const int AxisMin = 0;
    public const int AxisMax = 32768;
    public const int AxisCenter = 16384;

    private readonly HashSet<uint> _acquired = [];

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    public VJoyManager()
    {
        try
        {
            if (!VJoyNative.vJoyEnabled())
            {
                UnavailableReason = "vJoy driver reports it is not enabled.";
                return;
            }

            ushort dllVer = 0, drvVer = 0;
            if (!VJoyNative.DriverMatch(ref dllVer, ref drvVer))
            {
                UnavailableReason = $"vJoy DLL/driver version mismatch (dll={dllVer}, drv={drvVer}).";
                return;
            }

            IsAvailable = true;
        }
        catch (DllNotFoundException)
        {
            UnavailableReason = "vJoyInterface.dll not found. Install vJoy from https://sourceforge.net/projects/vjoystick/.";
        }
        catch (BadImageFormatException)
        {
            UnavailableReason = "vJoyInterface.dll bitness mismatch (need matching x86/x64 build).";
        }
    }

    public VJoyDeviceStatus GetStatus(uint deviceId)
    {
        if (!IsAvailable) return VJoyDeviceStatus.Missing;
        return (VJoyDeviceStatus)VJoyNative.GetVJDStatus(deviceId);
    }

    public bool Acquire(uint deviceId)
    {
        if (!IsAvailable) return false;
        var status = GetStatus(deviceId);
        if (status != VJoyDeviceStatus.Free && status != VJoyDeviceStatus.Owned) return false;

        if (!VJoyNative.AcquireVJD(deviceId)) return false;
        VJoyNative.ResetVJD(deviceId);
        _acquired.Add(deviceId);
        return true;
    }

    public void Relinquish(uint deviceId)
    {
        if (!IsAvailable) return;
        if (_acquired.Remove(deviceId)) VJoyNative.RelinquishVJD(deviceId);
    }

    /// <param name="normalized">-1..1</param>
    public void SetAxis(uint deviceId, VJoyAxis axis, double normalized)
    {
        if (!IsAvailable) return;
        var clamped = Math.Clamp(normalized, -1.0, 1.0);
        var value = AxisCenter + (int)Math.Round(clamped * AxisCenter);
        value = Math.Clamp(value, AxisMin, AxisMax);
        VJoyNative.SetAxis(value, deviceId, (uint)axis);
    }

    /// <param name="button">1-based button number.</param>
    public void SetButton(uint deviceId, int button, bool pressed)
    {
        if (!IsAvailable) return;
        VJoyNative.SetBtn(pressed, deviceId, (byte)button);
    }

    /// <param name="direction">0=N,1=E,2=S,3=W discrete direction, or -1 for neutral.</param>
    public void SetDiscretePov(uint deviceId, int povIndex, int direction)
    {
        if (!IsAvailable) return;
        VJoyNative.SetDiscPov(direction, deviceId, (byte)povIndex);
    }

    public int GetButtonCount(uint deviceId) => IsAvailable ? VJoyNative.GetVJDButtonNumber(deviceId) : 0;

    public int GetDiscretePovCount(uint deviceId) => IsAvailable ? VJoyNative.GetVJDDiscPovNumber(deviceId) : 0;

    public bool AxisExists(uint deviceId, VJoyAxis axis) => IsAvailable && VJoyNative.GetVJDAxisExist(deviceId, (uint)axis);

    public void Dispose()
    {
        foreach (var id in _acquired.ToArray()) Relinquish(id);
    }
}
