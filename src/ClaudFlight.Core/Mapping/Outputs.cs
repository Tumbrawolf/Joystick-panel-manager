using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Mapping;

/// <summary>Where MappingEngine writes virtual joystick output - the real vJoy driver
/// (VJoyManager), or a fake in tests.</summary>
public interface IVJoyOutput
{
    bool Acquire(uint deviceId);
    void Relinquish(uint deviceId);

    /// <param name="normalized">-1..1.</param>
    void SetAxis(uint deviceId, VJoyAxis axis, double normalized);

    /// <param name="button">1-based button number, as vJoy numbers them.</param>
    void SetButton(uint deviceId, int button, bool pressed);

    /// <param name="povIndex">1-based hat number, as vJoy numbers them.</param>
    /// <param name="direction">0=N,1=E,2=S,3=W, or -1 for centered.</param>
    void SetDiscretePov(uint deviceId, int povIndex, int direction);
}

/// <summary>Where MappingEngine sends simulated key presses - SendInput, or a fake in tests.</summary>
public interface IKeyOutput
{
    void KeyDown(int virtualKeyCode);
    void KeyUp(int virtualKeyCode);
}

/// <summary>The real keyboard: simulated key presses via SendInput.</summary>
public sealed class SendInputKeyOutput : IKeyOutput
{
    public void KeyDown(int virtualKeyCode) => KeyboardInjector.KeyDown(virtualKeyCode);
    public void KeyUp(int virtualKeyCode) => KeyboardInjector.KeyUp(virtualKeyCode);
}
