using System.Diagnostics;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.StreamDock;
using Microsoft.Win32.SafeHandles;

namespace ClaudFlight.Core.Devices;

/// <summary>One physical control on a StreamDock panel: the key code the panel reports for it, the
/// default name ClaudFlight shows, and the image slot of its screen (null if it has none).</summary>
public sealed record StreamDockKey(byte Code, string Name, byte? ImageSlot = null);

/// <summary>A Mirabox/HotSpot "StreamDock" panel (e.g. the VSD M18) driven directly over its
/// vendor-defined HID interface, without its "VSD Craft" software. Each physical key is exposed as
/// an ordinary button, so it can be monitored, renamed, mapped to vJoy, or bound to a keystroke like
/// any other device's buttons.
///
/// Protocol (VSD Craft's own traffic, recorded from a real M18):
/// - Output reports: report ID 0x00, then the command, zero-padded to the report length. Commands
///   start with "CRT\0\0" (e.g. "DIS" wake, "LIG" brightness, "CLE" clear, "STP" refresh).
/// - Key images: "BAT" + length + image slot, then the raw JPEG (64x64, baseline) in report-sized
///   chunks, then "STP" to show it (see SendImage).
/// - Input reports: report ID 0x00, "ACK\0\0OK\0\0", then the key code and its state
///   (1 = down, 0 = up).
/// - The panel only reports keys after a host has run its start-up sequence (see StartUp), and
///   stops again when a host sends the disconnect command ("CLE\0\0DC", sent by VSD Craft when it
///   quits) or the panel loses power.
///
/// VSD Craft can still run alongside: input reports go to every open handle, so this device keeps
/// receiving key presses either way. While VSD Craft is running this device sends nothing, so the
/// two never fight over the panel; when VSD Craft quits (which switches the panel off), this device
/// runs the start-up sequence again.</summary>
public sealed class StreamDockDevice : IPhysicalDevice
{
    private const int MaxButtons = 64;
    private const byte DefaultBrightness = 60; // what VSD Craft uses
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly string[] VendorProcessNames = ["VSD Craft", "StreamDock"];

    private static readonly Dictionary<AxisType, string> NoAxisNames = [];
    private static readonly Dictionary<AxisType, int> NoAxes = [];

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly int _inputReportLength;
    private readonly int _outputReportLength;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _timer;
    private readonly object _stateLock = new();
    private readonly object _writeLock = new();
    private readonly bool[] _pressed = new bool[MaxButtons];
    private readonly Dictionary<byte, int> _indexByCode = [];
    private readonly List<string> _buttonNames = [];
    private readonly List<byte?> _imageSlots = [];

    // Guarded by _writeLock: the images the user wants on each button, and what's actually on
    // each image slot right now (null/missing = blank).
    private Dictionary<int, byte[]> _wantedImages = [];
    private Dictionary<int, byte[]> _wantedPressedImages = [];
    private readonly Dictionary<byte, byte[]> _shownImages = [];
    private volatile bool _isConnected = true;
    private volatile bool _disposed;
    private volatile bool _vendorAppWasRunning;
    private DateTime _lastHeartbeat = DateTime.MinValue;

    public Guid InstanceGuid { get; }
    public string Name { get; }
    public string DevicePath { get; }
    public int ButtonCount { get { lock (_stateLock) return _buttonNames.Count; } }
    public int PovCount => 0;
    public IReadOnlyList<AxisType> PresentAxes => [];
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames => NoAxisNames;
    public IReadOnlyDictionary<int, string> ButtonDeviceNames
    {
        get { lock (_stateLock) return _buttonNames.Select((name, i) => (name, i)).ToDictionary(x => x.i, x => x.name); }
    }
    public bool IsLikelyGameController => true;

    /// <summary>False once the device has been unplugged or its handle failed.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Raised (on a background thread) when a key with an unknown code is pressed and gets
    /// added as a new button, or when the device disconnects - either way the UI should refresh its
    /// device list.</summary>
    public event Action? Changed;

    internal StreamDockDevice(string devicePath, SafeFileHandle handle, int inputReportLength, int outputReportLength,
        string name, Guid instanceGuid, IReadOnlyList<StreamDockKey> layout)
    {
        DevicePath = devicePath;
        Name = name;
        InstanceGuid = instanceGuid;
        _handle = handle;
        _inputReportLength = inputReportLength;
        _outputReportLength = outputReportLength;
        foreach (var key in layout.Take(MaxButtons))
        {
            _indexByCode[key.Code] = _buttonNames.Count;
            _buttonNames.Add(key.Name);
            _imageSlots.Add(key.ImageSlot);
        }

        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);

        _vendorAppWasRunning = IsVendorAppRunning();
        if (!_vendorAppWasRunning) StartUp();
        _timer = new Timer(_ => OnTimer(), null, CheckInterval, CheckInterval);

        _ = Task.Run(ReadLoopAsync);
    }

    public JoystickSnapshot Poll()
    {
        bool[] buttons;
        lock (_stateLock)
        {
            buttons = new bool[_buttonNames.Count];
            if (_isConnected) Array.Copy(_pressed, buttons, buttons.Length);
        }
        return new JoystickSnapshot { Axes = NoAxes, Buttons = buttons, PointOfViews = [] };
    }

    /// <summary>Width and height, in pixels, of the JPEG each key screen takes.</summary>
    public const int KeyImageSize = 64;

    /// <summary>True if this button has its own screen that can show an image.</summary>
    public bool HasKeyScreen(int buttonIndex)
    {
        lock (_stateLock) return buttonIndex >= 0 && buttonIndex < _imageSlots.Count && _imageSlots[buttonIndex] is not null;
    }

    /// <summary>Sets the image on every key screen: a baseline JPEG of KeyImageSize x KeyImageSize
    /// per button index; buttons left out are blanked. A key with a pressed image shows that
    /// instead while it's held (swapped by this device as the press arrives). Only screens whose
    /// image actually changed are redrawn. While VSD Craft is running nothing is sent (it owns the
    /// screens then); the images are drawn when ClaudFlight takes the panel back.</summary>
    public void SetButtonImages(IReadOnlyDictionary<int, byte[]> jpegByButton, IReadOnlyDictionary<int, byte[]>? pressedJpegByButton = null)
    {
        lock (_writeLock)
        {
            _wantedImages = new Dictionary<int, byte[]>(jpegByButton);
            _wantedPressedImages = pressedJpegByButton is null ? [] : new Dictionary<int, byte[]>(pressedJpegByButton);
            if (_disposed || !_isConnected || IsVendorAppRunning()) return;
            if (SyncImages()) SendCrt("STP"u8);
        }
    }

    /// <summary>VSD Craft's start-up sequence, byte for byte: read the firmware version, wake the
    /// screen, set brightness, two QUCMD configuration packets (the ones that switch key reporting
    /// on), LED-bar setup, clear all keys, refresh - plus redrawing ClaudFlight's own key images
    /// after the clear.</summary>
    private void StartUp()
    {
        lock (_writeLock)
        {
            HidNative.GetInputReport(_handle, _inputReportLength);
            SendCrt("DIS\0\0\0"u8);
            SendCrt([.. "LIG\0\0"u8, DefaultBrightness]);
            SendCrt([.. "QUCMD"u8, 0x11, 0x11, 0x00, 0x11, 0x00, 0x11]);
            SendCrt([.. "LBLIG"u8, 0x3C]);
            SendCrt([.. "LBLIG"u8, 0x37]);
            SendCrt("DELED"u8);
            SendCrt([.. "QUCMD"u8, 0x1F]);
            SendCrt([.. "CLE\0\0\0"u8, 0xFF]);
            _shownImages.Clear();
            SyncImages();
            SendCrt("STP"u8);
            _lastHeartbeat = DateTime.UtcNow;
        }
    }

    /// <summary>Sends every key image that differs from what's on the panel (call with _writeLock
    /// held, so a heartbeat can't land in the middle of an image's data). The panel only shows the
    /// changes after an STP, which the caller sends. Returns true if anything was sent.</summary>
    private bool SyncImages()
    {
        byte?[] slots;
        bool[] pressed;
        lock (_stateLock)
        {
            slots = [.. _imageSlots];
            pressed = _pressed[..slots.Length];
        }

        var changed = false;
        for (var index = 0; index < slots.Length; index++)
        {
            if (slots[index] is not { } slot) continue;
            var wanted = pressed[index] && _wantedPressedImages.TryGetValue(index, out var pressedImage)
                ? pressedImage
                : _wantedImages.GetValueOrDefault(index);
            var shown = _shownImages.GetValueOrDefault(slot);
            if (wanted is null ? shown is null : shown is not null && wanted.AsSpan().SequenceEqual(shown)) continue;

            if (wanted is null)
            {
                SendCrt([.. "CLE\0\0\0"u8, slot]);
                _shownImages.Remove(slot);
            }
            else
            {
                SendImage(slot, wanted);
                _shownImages[slot] = wanted;
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>"BAT" + the JPEG's length (4 bytes, big-endian) + the image slot, then the JPEG
    /// itself in raw report-sized chunks with no header.</summary>
    private void SendImage(byte slot, byte[] jpeg)
    {
        var length = jpeg.Length;
        SendCrt([.. "BAT"u8, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, slot]);
        var chunkSize = _outputReportLength - 1;
        for (var offset = 0; offset < length; offset += chunkSize)
        {
            SendRaw(jpeg.AsSpan(offset, Math.Min(chunkSize, length - offset)));
        }
    }

    private void OnTimer()
    {
        if (_disposed || !_isConnected) return;

        var vendorRunning = IsVendorAppRunning();
        if (vendorRunning)
        {
            _vendorAppWasRunning = true;
            return;
        }

        if (_vendorAppWasRunning)
        {
            // VSD Craft just quit, which sends the disconnect command and switches the panel's key
            // reporting off - take over and switch it back on.
            _vendorAppWasRunning = false;
            StartUp();
            return;
        }

        if (DateTime.UtcNow - _lastHeartbeat >= HeartbeatInterval)
        {
            SendCrt("CONNECT"u8);
            _lastHeartbeat = DateTime.UtcNow;
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[_inputReportLength];
        while (!_cts.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                MarkDisconnected();
                return;
            }

            if (read == 0)
            {
                MarkDisconnected();
                return;
            }

            HandleReport(buffer.AsSpan(0, read));
        }
    }

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        // Windows hands back the report ID (0x00 for this device) as the first byte.
        var data = report.Length > 0 && report[0] == 0 ? report[1..] : report;

        // The panel can ask for a handshake; VSD Craft answers with a bare "HAN".
        if (data.StartsWith("HANDLER"u8))
        {
            if (!IsVendorAppRunning()) SendRaw("HAN"u8);
            return;
        }

        if (data.Length < 11 || !data.StartsWith("ACK"u8) || data[5] != (byte)'O' || data[6] != (byte)'K') return;

        var code = data[9];
        var isDown = data[10] != 0;
        if (code == 0) return;

        bool added = false;
        lock (_stateLock)
        {
            if (!_indexByCode.TryGetValue(code, out var index))
            {
                // A control missing from the model's layout: add it as a new button so it can
                // still be used, named after its raw code.
                if (_buttonNames.Count >= MaxButtons) return;
                index = _buttonNames.Count;
                _indexByCode[code] = index;
                _buttonNames.Add($"Code 0x{code:X2}");
                _imageSlots.Add(null);
                added = true;
            }
            _pressed[index] = isDown;
        }

        if (added) Changed?.Invoke();
        else RedrawForPress();
    }

    /// <summary>Swaps a key between its normal and pressed image as it's pressed/released.</summary>
    private void RedrawForPress()
    {
        lock (_writeLock)
        {
            if (_wantedPressedImages.Count == 0 || _disposed || !_isConnected || _vendorAppWasRunning) return;
            if (SyncImages()) SendCrt("STP"u8);
        }
    }

    private void SendCrt(ReadOnlySpan<byte> command)
    {
        Span<byte> body = stackalloc byte[5 + command.Length];
        "CRT\0\0"u8.CopyTo(body);
        command.CopyTo(body[5..]);
        SendRaw(body);
    }

    private void SendRaw(ReadOnlySpan<byte> body)
    {
        if (_outputReportLength < 1 + body.Length) return;

        var report = new byte[_outputReportLength]; // [0] = report ID 0x00
        body.CopyTo(report.AsSpan(1));

        lock (_writeLock)
        {
            if (_disposed) return;
            try
            {
                _stream.Write(report, 0, report.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                MarkDisconnected();
            }
        }
    }

    private void MarkDisconnected()
    {
        if (_disposed || !_isConnected) return;
        lock (_stateLock)
        {
            _isConnected = false;
            Array.Clear(_pressed);
        }
        Changed?.Invoke();
    }

    private static bool IsVendorAppRunning()
    {
        foreach (var name in VendorProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (running) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _timer.Dispose();
        lock (_writeLock) _stream.Dispose();
        _cts.Dispose();
    }
}
