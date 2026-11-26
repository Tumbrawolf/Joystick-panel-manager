using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClaudFlight.Core.Devices;

namespace ClaudFlight.Core.StreamDock;

/// <summary>Finds and owns Mirabox/HotSpot "StreamDock" panels (VSD Craft devices) attached over
/// USB. These panels don't send standard keyboard/joystick reports, so neither DirectInput nor Raw
/// Input sees their keys; they're opened directly on their vendor-defined HID interface instead
/// (see StreamDockDevice for the protocol).</summary>
public sealed partial class StreamDockManager : IDisposable
{
    /// <summary>VSD M18: 15 screen keys (codes 0x01-0x0F) and 3 side buttons (0x25, 0x30, 0x31),
    /// as reported by a real panel. Which code sits at which physical position isn't documented,
    /// so the names are just numbers - rename them in Live Monitor.</summary>
    private static readonly StreamDockKey[] M18Layout =
    [
        .. Enumerable.Range(1, 15).Select(i => new StreamDockKey((byte)i, $"Key {i}")),
        new(0x25, "Side 1"),
        new(0x30, "Side 2"),
        new(0x31, "Side 3"),
    ];

    /// <summary>Known panels: USB vendor ID, product ID, display name, key layout. A model whose
    /// layout is unknown can use an empty one: its keys are then added as they're pressed.</summary>
    private static readonly (ushort Vid, ushort Pid, string Model, StreamDockKey[] Layout)[] SupportedDevices =
    [
        (0x5548, 0x1000, "VSD M18", M18Layout),
    ];

    /// <summary>Vendor-defined HID usage pages start at 0xFF00. The panels also expose a plain
    /// keyboard interface, which this filters out.</summary>
    private const ushort VendorUsagePageStart = 0xFF00;

    private readonly Dictionary<string, StreamDockDevice> _openByPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IPhysicalDevice> Devices { get; private set; } = [];

    /// <summary>Raised on a background thread when a panel's button set changes or it
    /// disconnects. Handlers should marshal to the UI thread and call Refresh.</summary>
    public event Action? DevicesChanged;

    /// <summary>Re-scans for panels. Panels that are already open and still connected stay open
    /// (so their pressed-key state isn't lost); unplugged ones are closed; new ones are opened.</summary>
    public void Refresh()
    {
        var present = new Dictionary<string, (string Model, StreamDockKey[] Layout)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in HidNative.EnumerateHidPaths())
        {
            var match = VidPidRegex().Match(path);
            if (!match.Success) continue;

            var vid = Convert.ToUInt16(match.Groups[1].Value, 16);
            var pid = Convert.ToUInt16(match.Groups[2].Value, 16);
            var supported = SupportedDevices.FirstOrDefault(d => d.Vid == vid && d.Pid == pid);
            if (supported.Model is not null) present[path] = (supported.Model, supported.Layout);
        }

        foreach (var (path, device) in _openByPath.ToList())
        {
            if (present.ContainsKey(path) && device.IsConnected) continue;
            device.Changed -= OnDeviceChanged;
            device.Dispose();
            _openByPath.Remove(path);
        }

        foreach (var (path, (model, layout)) in present)
        {
            if (_openByPath.ContainsKey(path)) continue;
            var device = TryOpen(path, model, layout);
            if (device is null) continue;
            device.Changed += OnDeviceChanged;
            _openByPath[path] = device;
        }

        Devices = _openByPath.Values.Cast<IPhysicalDevice>().ToList();
    }

    private static StreamDockDevice? TryOpen(string path, string model, StreamDockKey[] layout)
    {
        var handle = HidNative.Open(path);
        if (handle is null) return null;

        var caps = HidNative.GetCaps(handle);
        if (caps is not { } c || c.UsagePage < VendorUsagePageStart || c.InputReportByteLength < 12 || c.OutputReportByteLength < 16)
        {
            handle.Dispose();
            return null;
        }

        // A stable identity for profiles: the USB serial number survives re-plugging into a
        // different port, unlike the device path. Fall back to the path if there's no serial.
        var serial = HidNative.GetSerialNumber(handle);
        var identity = $"StreamDock|{model}|{(serial.Length > 0 ? serial : path.ToLowerInvariant())}";
        var guid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(identity)));

        try
        {
            return new StreamDockDevice(path, handle, c.InputReportByteLength, c.OutputReportByteLength, model, guid, layout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            handle.Dispose();
            return null;
        }
    }

    private void OnDeviceChanged() => DevicesChanged?.Invoke();

    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    public void Dispose()
    {
        foreach (var device in _openByPath.Values)
        {
            device.Changed -= OnDeviceChanged;
            device.Dispose();
        }
        _openByPath.Clear();
        Devices = [];
    }
}
