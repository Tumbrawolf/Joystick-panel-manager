using System.Runtime.InteropServices;

namespace ClaudFlight.Core.VJoy;

public enum VJoyDeviceStatus
{
    Owned = 0,
    Free = 1,
    Busy = 2,
    Missing = 3,
    Unknown = 4,
}

/// <summary>Raw P/Invoke bindings to vJoyInterface.dll (vJoy SDK). Requires the vJoy driver
/// (https://sourceforge.net/projects/vjoystick/) to be installed; otherwise every call throws
/// DllNotFoundException, which callers should treat as "vJoy not available".</summary>
internal static class VJoyNative
{
    private const string Dll = "vJoyInterface";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool vJoyEnabled();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern short GetvJoyVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool DriverMatch(ref ushort dllVersion, ref ushort drvVersion);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool AcquireVJD(uint rID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void RelinquishVJD(uint rID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDStatus(uint rID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool ResetVJD(uint rID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool SetAxis(int value, uint rID, uint axis);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool SetDiscPov(int value, uint rID, byte nPov);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDButtonNumber(uint rID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDDiscPovNumber(uint rID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool GetVJDAxisExist(uint rID, uint axis);
}
