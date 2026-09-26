using System.Runtime.InteropServices;

namespace ClaudFlight.Core.Keyboard;

/// <summary>Synthesizes keyboard input via Windows' SendInput API - the same mechanism legitimate
/// macro/remapping tools use. This makes real key-down/key-up events indistinguishable at the OS
/// level from a physical keystroke, which is exactly what's needed for a game to react to it, but
/// is also why some anti-cheat systems flag background input injection - only enable this for
/// games/situations where that's acceptable.</summary>
internal static class KeyboardInjector
{
    private const int InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>The real Win32 union also includes MOUSEINPUT and HARDWAREINPUT variants we never
    /// use, but its size is still determined by the largest of the three (32 bytes on x64) - since
    /// this only declares the keyboard variant, Size=32 must be set explicitly, or the resulting
    /// INPUT struct undersizes to 32 bytes instead of the real 40. SendInput validates the cbSize
    /// it's given against the size Windows actually expects and silently rejects (ERROR_INVALID_PARAMETER,
    /// returning 0 events sent) anything that doesn't match - verified empirically.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numInputs, Input[] inputs, int size);

    public static void KeyDown(int virtualKeyCode) => Send(virtualKeyCode, up: false);
    public static void KeyUp(int virtualKeyCode) => Send(virtualKeyCode, up: true);

    private static void Send(int virtualKeyCode, bool up)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKeyCode,
                    Flags = up ? KeyEventFKeyUp : 0,
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }
}
