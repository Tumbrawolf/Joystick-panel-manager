using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClaudFlight.Core.Devices;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.RawInput;

/// <summary>Captures keyboard input via the Windows Raw Input API, keyed per physical device
/// rather than the single combined stream DirectInput exposes - this is what lets a macro
/// keypad's keystrokes be told apart from the main keyboard's and remapped individually.
///
/// Each device's button set is learned rather than assumed: a brand new device starts with zero
/// known buttons, and each one gets added (in the order first pressed) the first time it's seen -
/// a 7-button keypad ends up with 7 buttons, not the ~140-entry full key catalog. Call
/// SeedKnownKeys once at startup with previously-learned keys from a saved profile so button
/// numbering stays stable across restarts instead of re-learning from scratch every session.
///
/// This creates its own hidden native message window and registers for RIDEV_INPUTSINK keyboard
/// input, so it receives every keystroke from every keyboard-type device system-wide while the
/// app is running (background input, matching how the DirectInput devices are also polled in the
/// background). Everything stays local: key state is only used to answer "is this button
/// currently pressed" for whatever mappings the user has configured. Nothing is logged or sent
/// anywhere.</summary>
public sealed class RawInputKeyboardManager : IDisposable
{
    private const string WindowClassName = "ClaudFlightRawInputWindow";

    private readonly WndProc _wndProcDelegate;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<IntPtr, string> _handleToPath = [];
    private readonly Dictionary<string, Guid> _pathToGuid = [];

    /// <summary>Maps a combined device's InstanceGuid (see Refresh) back to the underlying keyboard
    /// sub-device's own guid, so button-reorder (keyed by whatever guid the UI hands back) keeps
    /// working transparently for the keyboard-sourced half of a combined device.</summary>
    private readonly Dictionary<Guid, Guid> _comboToKeyboardGuid = [];

    /// <summary>Per device: the VK codes seen so far, in discovery order - list index is the
    /// button index everything else (JoystickSnapshot.Buttons, bindings) uses.</summary>
    private readonly Dictionary<Guid, List<int>> _knownVKeysByDevice = [];
    private readonly Dictionary<Guid, HashSet<int>> _pressedVKeysByDevice = [];

    /// <summary>Per mouse-type device: accumulated X/Y movement and wheel delta since last drained
    /// by a Poll() (relative-per-tick, same convention the old DirectInput-based mouse device used),
    /// and which of the 5 standard buttons (Left/Right/Middle/X1/X2) are currently held.</summary>
    private readonly Dictionary<Guid, int> _xDeltaByDevice = [];
    private readonly Dictionary<Guid, int> _yDeltaByDevice = [];
    private readonly Dictionary<Guid, int> _wheelDeltaByDevice = [];
    private readonly Dictionary<Guid, bool[]> _mouseButtonsByDevice = [];

    /// <summary>Per (device, native axis) override of how that axis is exposed - see
    /// AxisReportMode. Absent entries default to reporting as-is under their native AxisType.</summary>
    private readonly Dictionary<(Guid DeviceGuid, AxisType NativeAxis), AxisModeEntry> _axisModes = [];

    /// <summary>Fires when a device's button set grows - a brand new key was seen from it for the
    /// first time. Subscribers should persist (deviceGuid, vkey) so SeedKnownKeys can restore the
    /// same button numbering next launch.</summary>
    public event Action<Guid, int>? KeyLearned;

    public RawInputKeyboardManager()
    {
        _wndProcDelegate = WndProc;
        _hwnd = CreateMessageWindow();
        RegisterForInput();
    }

    public IReadOnlyList<IPhysicalDevice> Devices { get; private set; } = [];

    /// <summary>Pre-populates axis-mode overrides from previously-saved data - called once at
    /// startup, same pattern as SeedKnownKeys.</summary>
    public void SeedAxisModes(IEnumerable<AxisModeEntry> entries)
    {
        foreach (var entry in entries) _axisModes[(entry.DeviceGuid, entry.NativeAxis)] = entry;
    }

    /// <summary>Sets how one device's native axis should be exposed (see AxisReportMode) - called
    /// when the user changes the selection in Live Monitor. Passing Mode=Axis with ReportAs equal
    /// to nativeAxis clears any relabeling back to default.</summary>
    public void SetAxisMode(Guid deviceGuid, AxisType nativeAxis, AxisReportMode mode, AxisType reportAs)
    {
        _axisModes[(deviceGuid, nativeAxis)] = new AxisModeEntry { DeviceGuid = deviceGuid, NativeAxis = nativeAxis, Mode = mode, ReportAs = reportAs };
    }

    /// <summary>The current mode for one device's native axis - defaults to reporting as-is
    /// (Mode=Axis, ReportAs=nativeAxis) when no override has been set.</summary>
    public AxisModeEntry GetAxisMode(Guid deviceGuid, AxisType nativeAxis) =>
        _axisModes.TryGetValue((deviceGuid, nativeAxis), out var entry)
            ? entry
            : new AxisModeEntry { DeviceGuid = deviceGuid, NativeAxis = nativeAxis, Mode = AxisReportMode.Axis, ReportAs = nativeAxis };

    /// <summary>Pre-populates a device's known buttons from previously-saved data, before it's
    /// necessarily been seen again this session - called once at startup per device so a device's
    /// button count/numbering is already correct on the very first refresh, rather than starting
    /// at zero and waiting for every button to be pressed again.</summary>
    public void SeedKnownKeys(Guid deviceGuid, IEnumerable<int> vkeys)
    {
        if (!_knownVKeysByDevice.TryGetValue(deviceGuid, out var known))
        {
            known = [];
            _knownVKeysByDevice[deviceGuid] = known;
        }
        foreach (var vkey in vkeys)
        {
            if (!known.Contains(vkey)) known.Add(vkey);
        }
    }

    /// <summary>Re-enumerates connected keyboard- and mouse-type raw input devices (includes the
    /// real keyboard/mouse alongside any macro pad or wheel-emulating dial - the app's device
    /// hide/restore feature is what filters this down to just the ones actually wanted). A device
    /// that exposes exactly one keyboard-type and one mouse-type interface under the same VID/PID
    /// (e.g. a macro keypad whose buttons report as keyboard VK codes while its jog dial reports as
    /// mouse wheel movement) is merged into a single combined card instead of showing up as two
    /// unrelated-looking entries - any other combination (no pairing, or more than one of either
    /// type) is left as separate devices, since grouping would be ambiguous.</summary>
    public void Refresh()
    {
        var raw = new List<(IntPtr Handle, uint Type, string Path)>();
        foreach (var (handle, type) in RawInputNative.EnumerateDevices())
        {
            if (type != RawInputNative.RimTypeKeyboard && type != RawInputNative.RimTypeMouse) continue;

            var path = RawInputNative.GetDeviceName(handle);
            if (string.IsNullOrEmpty(path)) continue;

            _handleToPath[handle] = path;
            raw.Add((handle, type, path));
        }

        var devices = new List<IPhysicalDevice>();
        foreach (var group in raw.GroupBy(r => ExtractVidPid(r.Path) ?? r.Path))
        {
            var keyboardEntries = group.Where(r => r.Type == RawInputNative.RimTypeKeyboard).ToList();
            var mouseEntries = group.Where(r => r.Type == RawInputNative.RimTypeMouse).ToList();

            // A composite device's keyboard-type and mouse-type interfaces (e.g. a macro keypad's
            // buttons reporting as VK codes alongside its jog dial reporting as mouse wheel/X/Y
            // movement - sometimes on more than one keyboard-type interface at once, if the dial
            // also sends a keyboard shortcut for compatibility with simple multimedia-key handling)
            // get merged into one card. Only the first mouse-type interface is used if there's more
            // than one - combining multiple independent X/Y/wheel streams isn't meaningful.
            if (keyboardEntries.Count > 0 && mouseEntries.Count > 0)
            {
                var keyboardGuids = keyboardEntries.Select(e => ResolveGuid(e.Path)).ToList();
                var mouseGuid = ResolveGuid(mouseEntries[0].Path);
                var comboGuid = ResolveGuid($"COMBO:{group.Key}");
                if (keyboardGuids.Count == 1) _comboToKeyboardGuid[comboGuid] = keyboardGuids[0];
                devices.Add(new RawInputCombinedDevice(comboGuid, DescribeDevice(keyboardEntries[0].Path, "Combined"), keyboardGuids, mouseGuid, this));
                continue;
            }

            foreach (var entry in group)
            {
                var guid = ResolveGuid(entry.Path);
                devices.Add(entry.Type == RawInputNative.RimTypeKeyboard
                    ? new RawInputKeyboardDevice(guid, DescribeDevice(entry.Path, "Keyboard"), this)
                    : new RawInputMouseDevice(guid, DescribeDevice(entry.Path, "Mouse"), this));
            }
        }
        Devices = devices;
    }

    /// <summary>Pulls "VID_xxxx_PID_xxxx" out of a raw input device path for grouping interfaces
    /// that belong to the same physical device - null if the path doesn't contain both.</summary>
    private static string? ExtractVidPid(string path)
    {
        var vidIndex = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIndex = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex < 0 || pidIndex < 0 || vidIndex + 8 > path.Length || pidIndex + 8 > path.Length) return null;
        return $"{path.Substring(vidIndex, 8)}_{path.Substring(pidIndex, 8)}";
    }

    public int GetButtonCount(Guid deviceGuid) =>
        _knownVKeysByDevice.TryGetValue(deviceGuid, out var known) ? known.Count : 0;

    /// <summary>The VK codes learned for a device, in their current order (list index = button
    /// index). Empty for anything that isn't a Raw Input keyboard-type device (or the keyboard half
    /// of a combined device) - used both to display/persist the order and as the "is this
    /// reorderable" check (ReorderKnownKeys is a no-op otherwise). Accepts either a plain keyboard
    /// device's own guid or a combined device's guid, transparently resolving the latter to its
    /// underlying keyboard sub-device.</summary>
    public IReadOnlyList<int> GetKnownVKeys(Guid deviceGuid)
    {
        deviceGuid = _comboToKeyboardGuid.GetValueOrDefault(deviceGuid, deviceGuid);
        return _knownVKeysByDevice.TryGetValue(deviceGuid, out var known) ? known : [];
    }

    /// <summary>Replaces a device's learned-key order wholesale (e.g. after the user drags a
    /// button lamp to fix a mis-press during discovery) - every button index for that device
    /// changes to match, so callers must rebuild anything depending on the old numbering. Accepts
    /// either a plain keyboard device's own guid or a combined device's guid (see GetKnownVKeys).</summary>
    public void ReorderKnownKeys(Guid deviceGuid, IReadOnlyList<int> newOrder)
    {
        deviceGuid = _comboToKeyboardGuid.GetValueOrDefault(deviceGuid, deviceGuid);
        if (!_knownVKeysByDevice.ContainsKey(deviceGuid)) return;
        _knownVKeysByDevice[deviceGuid] = newOrder.ToList();
    }

    public IReadOnlyDictionary<int, string> GetButtonNames(Guid deviceGuid)
    {
        if (!_knownVKeysByDevice.TryGetValue(deviceGuid, out var known)) return EmptyNames;

        var names = new Dictionary<int, string>();
        for (var i = 0; i < known.Count; i++)
        {
            names[i] = VirtualKeyTable.TryGetIndex(known[i], out var catalogIndex) ? VirtualKeyTable.NameAt(catalogIndex) : $"VK 0x{known[i]:X2}";
        }
        return names;
    }

    internal bool IsPressed(Guid deviceGuid, int buttonIndex)
    {
        if (!_knownVKeysByDevice.TryGetValue(deviceGuid, out var known) || buttonIndex >= known.Count) return false;
        return _pressedVKeysByDevice.TryGetValue(deviceGuid, out var pressed) && pressed.Contains(known[buttonIndex]);
    }

    /// <summary>Returns the wheel movement accumulated since the last call for this device, then
    /// resets it to zero - matching the relative-per-poll convention the rest of the app already
    /// uses for mouse-style axes.</summary>
    internal int DrainWheelDelta(Guid deviceGuid) => Drain(_wheelDeltaByDevice, deviceGuid);
    internal int DrainXDelta(Guid deviceGuid) => Drain(_xDeltaByDevice, deviceGuid);
    internal int DrainYDelta(Guid deviceGuid) => Drain(_yDeltaByDevice, deviceGuid);

    private static int Drain(Dictionary<Guid, int> deltas, Guid deviceGuid)
    {
        if (!deltas.TryGetValue(deviceGuid, out var delta)) return 0;
        deltas[deviceGuid] = 0;
        return delta;
    }

    internal bool IsMouseButtonPressed(Guid deviceGuid, int buttonIndex) =>
        _mouseButtonsByDevice.TryGetValue(deviceGuid, out var buttons) && buttonIndex < buttons.Length && buttons[buttonIndex];

    private static readonly Dictionary<int, string> EmptyNames = [];

    private static string DescribeDevice(string path, string kind)
    {
        // Raw input only gives a device path like \\?\HID#VID_1038&PID_2206&...; pull the
        // vendor/product IDs out of it for a slightly more readable default than the full path.
        var vidIndex = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIndex = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex >= 0 && pidIndex >= 0 && vidIndex + 8 <= path.Length && pidIndex + 8 <= path.Length)
        {
            return $"{kind} device (VID_{path.Substring(vidIndex + 4, 4)} PID_{path.Substring(pidIndex + 4, 4)})";
        }
        return $"{kind} device";
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();

        uint size = 0;
        RawInputNative.GetRawInputData(hRawInput, RawInputNative.RidInput, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var written = RawInputNative.GetRawInputData(hRawInput, RawInputNative.RidInput, buffer, ref size, headerSize);
            if (written != size) return;

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type == RawInputNative.RimTypeKeyboard) HandleKeyboardMessage(header, buffer);
            else if (header.Type == RawInputNative.RimTypeMouse) HandleMouseMessage(header, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void HandleKeyboardMessage(RawInputHeader header, IntPtr buffer)
    {
        var message = Marshal.PtrToStructure<RawInputKeyboardMessage>(buffer);
        var vkey = (int)message.Keyboard.VKey;
        if (!VirtualKeyTable.TryGetIndex(vkey, out _)) return;

        var path = ResolveDevicePath(header.Device);
        if (path is null) return;
        var guid = ResolveGuid(path);

        var pressed = (message.Keyboard.Flags & RawInputNative.RiKeyBreak) == 0;

        if (!_knownVKeysByDevice.TryGetValue(guid, out var known))
        {
            known = [];
            _knownVKeysByDevice[guid] = known;
        }
        if (!known.Contains(vkey))
        {
            known.Add(vkey);
            KeyLearned?.Invoke(guid, vkey);
        }

        if (!_pressedVKeysByDevice.TryGetValue(guid, out var pressedSet))
        {
            pressedSet = [];
            _pressedVKeysByDevice[guid] = pressedSet;
        }
        if (pressed) pressedSet.Add(vkey);
        else pressedSet.Remove(vkey);
    }

    private void HandleMouseMessage(RawInputHeader header, IntPtr buffer)
    {
        var message = Marshal.PtrToStructure<RawInputMouseMessage>(buffer);

        var path = ResolveDevicePath(header.Device);
        if (path is null) return;
        var guid = ResolveGuid(path);

        var flags = message.Mouse.ButtonFlags;

        _xDeltaByDevice[guid] = _xDeltaByDevice.GetValueOrDefault(guid) + message.Mouse.LastX;
        _yDeltaByDevice[guid] = _yDeltaByDevice.GetValueOrDefault(guid) + message.Mouse.LastY;

        if ((flags & RawInputNative.RiMouseWheel) != 0)
        {
            // ButtonData holds the signed wheel delta (multiples of 120 per notch) when the
            // wheel flag is set - same field, reinterpreted, per the Win32 RAWMOUSE union.
            var delta = unchecked((short)message.Mouse.ButtonData);
            _wheelDeltaByDevice[guid] = _wheelDeltaByDevice.GetValueOrDefault(guid) + delta;
        }

        if (!_mouseButtonsByDevice.TryGetValue(guid, out var buttons))
        {
            buttons = new bool[5];
            _mouseButtonsByDevice[guid] = buttons;
        }
        ApplyMouseButtonFlag(buttons, 0, flags, RawInputNative.RiMouseLeftButtonDown, RawInputNative.RiMouseLeftButtonUp);
        ApplyMouseButtonFlag(buttons, 1, flags, RawInputNative.RiMouseRightButtonDown, RawInputNative.RiMouseRightButtonUp);
        ApplyMouseButtonFlag(buttons, 2, flags, RawInputNative.RiMouseMiddleButtonDown, RawInputNative.RiMouseMiddleButtonUp);
        ApplyMouseButtonFlag(buttons, 3, flags, RawInputNative.RiMouseButton4Down, RawInputNative.RiMouseButton4Up);
        ApplyMouseButtonFlag(buttons, 4, flags, RawInputNative.RiMouseButton5Down, RawInputNative.RiMouseButton5Up);
    }

    private static void ApplyMouseButtonFlag(bool[] buttons, int index, ushort flags, ushort downFlag, ushort upFlag)
    {
        if ((flags & downFlag) != 0) buttons[index] = true;
        else if ((flags & upFlag) != 0) buttons[index] = false;
    }

    private string? ResolveDevicePath(IntPtr handle)
    {
        if (_handleToPath.TryGetValue(handle, out var cached)) return cached;

        var path = RawInputNative.GetDeviceName(handle);
        if (path is null) return null;

        _handleToPath[handle] = path;
        return path;
    }

    /// <summary>Raw Input's device HANDLE is only valid for the current session; the device PATH
    /// string is stable across reboots and reconnects for the same physical device+port, so we
    /// hash it into a Guid to get a stable identity our profiles can key renames/bindings on.</summary>
    private Guid ResolveGuid(string path)
    {
        if (_pathToGuid.TryGetValue(path, out var cached)) return cached;

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(path));
        var guid = new Guid(hash);
        _pathToGuid[path] = guid;
        return guid;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == RawInputNative.WmInput)
        {
            HandleRawInput(lParam);
        }
        return RawInputNative.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr CreateMessageWindow()
    {
        var instance = RawInputNative.GetModuleHandle(null);

        var wndClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = _wndProcDelegate,
            Instance = instance,
            ClassName = WindowClassName,
        };
        RawInputNative.RegisterClassEx(ref wndClass);

        return RawInputNative.CreateWindowEx(
            0, WindowClassName, WindowClassName, 0,
            0, 0, 0, 0,
            RawInputNative.HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
    }

    private void RegisterForInput()
    {
        // Keyboard and Mouse usages - the only two that deliver RIM_TYPEKEYBOARD/RIM_TYPEMOUSE
        // messages, which is what HandleRawInput parses. (Consumer Control/System Control usages
        // deliver RIM_TYPEHID messages with raw report bytes instead, which would need a separate
        // HID report parser to decode - not needed unless a device turns out to require it.)
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RawInputNative.RidevInputSink, TargetWindow = _hwnd }, // Keyboard
            new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RawInputNative.RidevInputSink, TargetWindow = _hwnd }, // Mouse
        };
        RawInputNative.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) RawInputNative.DestroyWindow(_hwnd);
    }
}
