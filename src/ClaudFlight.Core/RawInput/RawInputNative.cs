using System.Runtime.InteropServices;

namespace ClaudFlight.Core.RawInput;

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDevice
{
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public IntPtr TargetWindow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    public uint Type;
    public uint Size;
    public IntPtr Device;
    public IntPtr WParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawKeyboardData
{
    public ushort MakeCode;
    public ushort Flags;
    public ushort Reserved;
    public ushort VKey;
    public uint Message;
    public uint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputKeyboardMessage
{
    public RawInputHeader Header;
    public RawKeyboardData Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawMouseData
{
    public ushort Flags;
    // Native RAWMOUSE has a 4-byte union (ulButtons, or usButtonFlags+usButtonData) right after
    // usFlags; that union needs 4-byte alignment, so there's 2 bytes of compiler-inserted padding
    // between usFlags and it that must be modeled explicitly here, or ButtonFlags/ButtonData end
    // up reading 2 bytes too early.
    private readonly ushort _reservedForAlignment;
    public ushort ButtonFlags;
    public ushort ButtonData;
    public uint RawButtons;
    public int LastX;
    public int LastY;
    public uint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputMouseMessage
{
    public RawInputHeader Header;
    public RawMouseData Mouse;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDeviceListEntry
{
    public IntPtr Device;
    public uint Type;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WndClassEx
{
    public uint Size;
    public uint Style;
    public WndProc WndProc;
    public int ClsExtra;
    public int WndExtra;
    public IntPtr Instance;
    public IntPtr Icon;
    public IntPtr Cursor;
    public IntPtr Background;
    public string? MenuName;
    public string ClassName;
    public IntPtr IconSm;
}

internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

internal static class RawInputNative
{
    public const uint RimTypeMouse = 0;
    public const uint RimTypeKeyboard = 1;
    public const uint RimTypeHid = 2;
    public const uint RidevInputSink = 0x00000100;
    public const uint RidInput = 0x10000003;
    public const uint RidiDeviceName = 0x20000007;
    public const int WmInput = 0x00FF;
    public const ushort RiKeyBreak = 0x01;

    public const ushort RiMouseLeftButtonDown = 0x0001;
    public const ushort RiMouseLeftButtonUp = 0x0002;
    public const ushort RiMouseRightButtonDown = 0x0004;
    public const ushort RiMouseRightButtonUp = 0x0008;
    public const ushort RiMouseMiddleButtonDown = 0x0010;
    public const ushort RiMouseMiddleButtonUp = 0x0020;
    public const ushort RiMouseButton4Down = 0x0040;
    public const ushort RiMouseButton4Up = 0x0080;
    public const ushort RiMouseButton5Down = 0x0100;
    public const ushort RiMouseButton5Up = 0x0200;
    public const ushort RiMouseWheel = 0x0400;

    public static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint numDevices, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList(IntPtr deviceList, ref uint numDevices, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    /// <summary>Resolves a raw input device handle to its stable device path (e.g.
    /// "\\?\HID#VID_1038&amp;PID_2206&amp;..."), used as a persistent identity for the device
    /// since the OS-assigned handle itself is only valid for the current session.</summary>
    public static string? GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RidiDeviceName, IntPtr.Zero, ref size);
        if (size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            var written = GetRawInputDeviceInfo(hDevice, RidiDeviceName, buffer, ref size);
            return written == unchecked((uint)-1) ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Enumerates every raw input device (keyboards, mice, HID) currently attached.
    /// Uses a manually-allocated unmanaged buffer rather than a managed struct array: passing a
    /// RawInputDeviceListEntry[] directly to GetRawInputDeviceList reports success but silently
    /// leaves every entry zeroed (a marshaling quirk with this particular API on .NET) - verified
    /// empirically. Reading each entry back with Marshal.PtrToStructure works correctly.</summary>
    public static IReadOnlyList<(IntPtr Handle, uint Type)> EnumerateDevices()
    {
        var entrySize = (uint)Marshal.SizeOf<RawInputDeviceListEntry>();

        uint count = 0;
        GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
        if (count == 0) return [];

        var buffer = Marshal.AllocHGlobal((int)(entrySize * count));
        try
        {
            var result = GetRawInputDeviceList(buffer, ref count, entrySize);
            if (result == unchecked((uint)-1)) return [];

            var devices = new List<(IntPtr, uint)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<RawInputDeviceListEntry>(IntPtr.Add(buffer, i * (int)entrySize));
                devices.Add((entry.Device, entry.Type));
            }
            return devices;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
