using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudFlight.Core.StreamDock;

/// <summary>Minimal Win32 HID interop: enumerate HID interfaces, open one by path, and read its
/// attributes/report sizes. Enough to talk to a vendor-defined HID interface (like a StreamDock
/// panel's) directly with ReadFile/WriteFile, without a third-party HID library.</summary>
internal static class HidNative
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>Device paths of every present HID interface on the system.</summary>
    public static List<string> EnumerateHidPaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var hidGuid);

        var set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;

        try
        {
            var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;

                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the size of the fixed part of the
                    // struct (DWORD + one WCHAR, padded), not the whole buffer: 8 on x64, 6 on x86.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    /// <summary>Opens a HID interface for overlapped read/write, shared with other processes (so a
    /// vendor app that also has it open keeps working - Windows gives each open handle its own copy
    /// of every input report). Returns null if it can't be opened.</summary>
    public static SafeFileHandle? Open(string path)
    {
        var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    public static HiddAttributes? GetAttributes(SafeFileHandle handle)
    {
        var attributes = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
        return HidD_GetAttributes(handle, ref attributes) ? attributes : null;
    }

    public static HidpCaps? GetCaps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try
        {
            return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Requests an input report over the control pipe (HID GET_REPORT) instead of waiting
    /// for one to arrive. Returns null on failure.</summary>
    public static byte[]? GetInputReport(SafeFileHandle handle, int length)
    {
        var buffer = new byte[length]; // [0] = report ID 0x00
        return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
    }

    public static string GetSerialNumber(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (!HidD_GetSerialNumberString(handle, buffer, buffer.Length)) return "";
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }
}
