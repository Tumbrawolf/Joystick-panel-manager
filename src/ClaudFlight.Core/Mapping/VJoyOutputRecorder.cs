using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Mapping;

/// <summary>Passes vJoy output on unchanged (to the real driver, which ignores it when vJoy isn't
/// installed) and remembers the last value written to each control - so the app can show what a
/// game would see, with or without vJoy.</summary>
public sealed class VJoyOutputRecorder(IVJoyOutput inner) : IVJoyOutput
{
    private readonly Dictionary<(uint Device, VJoyAxis Axis), double> _axes = [];
    private readonly Dictionary<(uint Device, int Button), bool> _buttons = [];
    private readonly Dictionary<(uint Device, int Hat), int> _hats = [];

    /// <summary>-1..1, or null if nothing has written to this axis yet.</summary>
    public double? Axis(uint device, VJoyAxis axis) => _axes.TryGetValue((device, axis), out var value) ? value : null;

    /// <param name="button">1-based, as vJoy numbers them.</param>
    public bool Button(uint device, int button) => _buttons.GetValueOrDefault((device, button));

    /// <param name="hat">1-based.</param>
    /// <returns>0=N, 1=E, 2=S, 3=W, or -1 for centered.</returns>
    public int Hat(uint device, int hat) => _hats.GetValueOrDefault((device, hat), -1);

    public bool Acquire(uint deviceId) => inner.Acquire(deviceId);

    public void Relinquish(uint deviceId)
    {
        inner.Relinquish(deviceId);
        foreach (var key in _axes.Keys.Where(k => k.Device == deviceId).ToList()) _axes.Remove(key);
        foreach (var key in _buttons.Keys.Where(k => k.Device == deviceId).ToList()) _buttons.Remove(key);
        foreach (var key in _hats.Keys.Where(k => k.Device == deviceId).ToList()) _hats.Remove(key);
    }

    public void SetAxis(uint deviceId, VJoyAxis axis, double normalized)
    {
        _axes[(deviceId, axis)] = Math.Clamp(normalized, -1, 1);
        inner.SetAxis(deviceId, axis, normalized);
    }

    public void SetButton(uint deviceId, int button, bool pressed)
    {
        _buttons[(deviceId, button)] = pressed;
        inner.SetButton(deviceId, button, pressed);
    }

    public void SetDiscretePov(uint deviceId, int povIndex, int direction)
    {
        _hats[(deviceId, povIndex)] = direction;
        inner.SetDiscretePov(deviceId, povIndex, direction);
    }
}

/// <summary>Passes simulated keys on unchanged and keeps the last few, newest first - for showing
/// what's been sent.</summary>
public sealed class KeyOutputRecorder(IKeyOutput inner, Func<DateTime>? clock = null) : IKeyOutput
{
    public const int Kept = 12;
    private readonly LinkedList<(DateTime Time, int VKey, bool Down)> _recent = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    /// <summary>Changes whenever a key is sent, so a view can tell when to redraw.</summary>
    public int Version { get; private set; }

    public IReadOnlyCollection<(DateTime Time, int VKey, bool Down)> Recent => _recent;

    public void KeyDown(int virtualKeyCode)
    {
        inner.KeyDown(virtualKeyCode);
        Record(virtualKeyCode, true);
    }

    public void KeyUp(int virtualKeyCode)
    {
        inner.KeyUp(virtualKeyCode);
        Record(virtualKeyCode, false);
    }

    private void Record(int vkey, bool down)
    {
        _recent.AddFirst((_clock(), vkey, down));
        while (_recent.Count > Kept) _recent.RemoveLast();
        Version++;
    }
}
