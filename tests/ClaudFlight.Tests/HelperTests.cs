using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;
using Microsoft.Win32;

namespace ClaudFlight.Tests;

public class KeySequenceTextTests
{
    [Fact]
    public void Reads_keys_combos_and_waits()
    {
        var steps = KeySequenceText.Parse("g, wait 200, Ctrl+L, enter", out var error);

        Assert.Null(error);
        Assert.NotNull(steps);
        Assert.Equal(4, steps.Count);
        Assert.Equal([0x47], steps[0].Keys);
        Assert.Equal(200, steps[1].WaitMs);
        Assert.Empty(steps[1].Keys);
        Assert.Equal([0xA2, 0x4C], steps[2].Keys);
        Assert.Equal([0x0D], steps[3].Keys);
    }

    [Theory]
    [InlineData("Shift+Plus", new[] { 0xA0, 0xBB })]
    [InlineData("Shift++", new[] { 0xA0, 0xBB })]
    [InlineData("+", new[] { 0xBB })]
    [InlineData("Numpad +", new[] { 0x6B })]
    [InlineData("Ctrl+Numpad +", new[] { 0xA2, 0x6B })]
    [InlineData("Comma", new[] { 0xBC })]
    [InlineData("/", new[] { 0xBF })]
    [InlineData("Left Shift+F5", new[] { 0xA0, 0x74 })]
    public void Reads_awkward_key_names(string text, int[] keys)
    {
        var steps = KeySequenceText.Parse(text, out var error);
        Assert.Null(error);
        Assert.Equal(keys, Assert.Single(steps!).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Banana")]
    [InlineData("wait 200")]
    [InlineData("A, wait")]
    [InlineData("A, wait 999999")]
    [InlineData("Ctrl+")]
    public void Explains_what_it_cannot_read(string text)
    {
        Assert.Null(KeySequenceText.Parse(text, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Formatting_reads_back_the_same()
    {
        var original = KeySequenceText.Parse("Ctrl+Plus, wait 50, Comma, Numpad +, F12, Left Alt+Tab", out _)!;
        var text = KeySequenceText.Format(original);
        var again = KeySequenceText.Parse(text, out var error)!;

        Assert.Null(error);
        Assert.Equal(original.Select(s => (string.Join(",", s.Keys), s.WaitMs)), again.Select(s => (string.Join(",", s.Keys), s.WaitMs)));
    }
}

public class ModelCopyTests
{
    [Fact]
    public void Copies_every_setting_onto_another_device()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var binding = new Binding
        {
            Source = new InputRef(from, ControlKind.Axis, 2),
            Target = new OutputRef(1, ControlKind.Button, 4),
            Part = InputPart.Zone,
            ZoneFrom = 0.9,
            Mode = SwitchMode.Latch,
            Trigger = PressTrigger.LongPress,
            Shifted = true,
        };
        var keys = new KeyBinding { Source = new InputRef(from, ControlKind.Button, 1), VirtualKeyCode = 0x47, Sequence = [new KeyStep { Keys = [0x47] }, new KeyStep { WaitMs = 30 }], TapOnRelease = true };

        var copy = ModelCopy.WithSourceDevice(binding, to);
        var keyCopy = ModelCopy.WithSourceDevice(keys, to);

        Assert.Equal(new InputRef(to, ControlKind.Axis, 2), copy.Source);
        Assert.Equal((InputPart.Zone, 0.9, 1.0, SwitchMode.Latch, PressTrigger.LongPress, true), (copy.Part, copy.ZoneFrom, copy.ZoneTo, copy.Mode, copy.Trigger, copy.Shifted));
        Assert.Equal(binding.Target, copy.Target);
        Assert.Equal(to, keyCopy.Source.DeviceGuid);
        Assert.True(keyCopy.TapOnRelease);
        Assert.Equal(30, keyCopy.Sequence![1].WaitMs);
        Assert.Equal(from, binding.Source.DeviceGuid); // the original is untouched
    }
}

public sealed class StartupRegistrationTests : IDisposable
{
    // A throwaway key, never the real Run key.
    private readonly string _keyPath = @"Software\ClaudFlightTests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Fact]
    public void Turns_on_and_off()
    {
        var startup = new StartupRegistration(_keyPath);
        Assert.False(startup.IsEnabled);

        startup.Enable(@"C:\Apps\ClaudFlight\ClaudFlight.App.exe");
        Assert.True(startup.IsEnabled);
        Assert.Equal("\"C:\\Apps\\ClaudFlight\\ClaudFlight.App.exe\" --tray", startup.Command);
        Assert.False(startup.PointsElsewhere(@"C:\Apps\ClaudFlight\ClaudFlight.App.exe"));
        Assert.True(startup.PointsElsewhere(@"D:\Other\ClaudFlight.App.exe"));

        startup.Disable();
        Assert.False(startup.IsEnabled);
        startup.Disable(); // already off: fine
    }
}

public class AxisTravelTests
{
    [Fact]
    public void Reports_calibrated_position_along_the_travel()
    {
        var rig = new Rig();
        rig.Load();
        Assert.Null(rig.Engine.AxisTravel(rig.Device.InstanceGuid, AxisType.X));

        rig.Device.Axes[AxisType.X] = 1000;
        rig.Tick();
        Assert.Equal(1.0, rig.Engine.AxisTravel(rig.Device.InstanceGuid, AxisType.X));
        rig.Device.Axes[AxisType.X] = 0;
        rig.Tick();
        Assert.Equal(0.5, rig.Engine.AxisTravel(rig.Device.InstanceGuid, AxisType.X));
        Assert.Null(rig.Engine.AxisTravel(Guid.NewGuid(), AxisType.X));
    }
}
