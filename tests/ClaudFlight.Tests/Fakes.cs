using ClaudFlight.Core.Devices;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Tests;

/// <summary>A physical device whose controls the test sets directly.</summary>
internal sealed class FakeDevice(int buttons = 8, int povs = 1) : IPhysicalDevice
{
    public Guid InstanceGuid { get; } = Guid.NewGuid();
    public string Name => "Fake stick";
    public bool[] Buttons { get; } = new bool[buttons];
    public int[] Povs { get; } = Enumerable.Repeat(-1, povs).ToArray();
    public Dictionary<AxisType, int> Axes { get; } = new() { [AxisType.X] = 0, [AxisType.Y] = 0 };

    public int ButtonCount => Buttons.Length;
    public int PovCount => Povs.Length;
    public IReadOnlyList<AxisType> PresentAxes => [.. Axes.Keys];
    public IReadOnlyDictionary<AxisType, string> AxisDeviceNames => new Dictionary<AxisType, string>();
    public IReadOnlyDictionary<int, string> ButtonDeviceNames => new Dictionary<int, string>();
    public bool IsLikelyGameController => true;

    public JoystickSnapshot Poll() => new()
    {
        Axes = new Dictionary<AxisType, int>(Axes),
        Buttons = [.. Buttons],
        PointOfViews = [.. Povs],
    };

    public void Dispose() { }
}

/// <summary>Records the last value written to each vJoy control.</summary>
internal sealed class FakeVJoy : IVJoyOutput
{
    public Dictionary<(uint Device, int Button), bool> Buttons { get; } = [];
    public Dictionary<(uint Device, int Pov), int> Povs { get; } = [];
    public Dictionary<(uint Device, VJoyAxis Axis), double> Axes { get; } = [];

    public bool Acquire(uint deviceId) => true;
    public void Relinquish(uint deviceId) { }
    public void SetAxis(uint deviceId, VJoyAxis axis, double normalized) => Axes[(deviceId, axis)] = normalized;
    public void SetButton(uint deviceId, int button, bool pressed) => Buttons[(deviceId, button)] = pressed;
    public void SetDiscretePov(uint deviceId, int povIndex, int direction) => Povs[(deviceId, povIndex)] = direction;

    public bool IsPressed(int button, uint device = 1) => Buttons.GetValueOrDefault((device, button));
}

/// <summary>Records simulated key events as "+vk" (down) / "-vk" (up).</summary>
internal sealed class FakeKeys : IKeyOutput
{
    public List<string> Events { get; } = [];
    public void KeyDown(int virtualKeyCode) => Events.Add($"+{virtualKeyCode}");
    public void KeyUp(int virtualKeyCode) => Events.Add($"-{virtualKeyCode}");
}

/// <summary>One fake device, fake outputs, a hand-driven clock, and the engine wired to them.</summary>
internal sealed class Rig
{
    public FakeDevice Device { get; } = new();
    public FakeVJoy VJoy { get; } = new();
    public FakeKeys Keys { get; } = new();
    public Profile Profile { get; } = new();
    public double Now { get; set; }
    public MappingEngine Engine { get; }

    public Rig()
    {
        Engine = new MappingEngine(() => [Device], VJoy, Keys, () => Now);
    }

    public InputRef Button(int index) => new(Device.InstanceGuid, ControlKind.Button, index);
    public InputRef Pov(int index = 0) => new(Device.InstanceGuid, ControlKind.Pov, index);
    public InputRef Axis(AxisType axis) => new(Device.InstanceGuid, ControlKind.Axis, (int)axis);

    /// <summary>Maps a source to vJoy device 1's button (0-based, as the profile stores it).</summary>
    public Binding ToButton(InputRef source, int buttonIndex, InputPart part = InputPart.Whole,
        SwitchMode mode = SwitchMode.Hold, bool shifted = false)
    {
        var binding = new Binding
        {
            Source = source,
            Target = new OutputRef(1, ControlKind.Button, buttonIndex),
            Part = part,
            Mode = mode,
            Shifted = shifted,
        };
        Profile.Bindings.Add(binding);
        return binding;
    }

    public void Load() => Engine.LoadProfile(Profile);

    /// <summary>Advances the clock and runs one engine tick.</summary>
    public void Tick(double seconds = 0.033)
    {
        Now += seconds;
        Engine.Tick();
    }
}
