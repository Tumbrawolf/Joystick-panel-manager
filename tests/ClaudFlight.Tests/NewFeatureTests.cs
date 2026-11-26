using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;

namespace ClaudFlight.Tests;

public class PressTriggerTests
{
    private static Binding Add(Rig rig, int button, PressTrigger trigger, SwitchMode mode = SwitchMode.Hold)
    {
        var binding = new Binding
        {
            Source = rig.Button(0),
            Target = new OutputRef(1, ControlKind.Button, button),
            Trigger = trigger,
            Mode = mode,
        };
        rig.Profile.Bindings.Add(binding);
        return binding;
    }

    [Fact]
    public void Long_press_fires_only_after_being_held()
    {
        var rig = new Rig();
        Add(rig, 0, PressTrigger.LongPress);
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true;
        rig.Tick(0.2);
        Assert.False(rig.VJoy.IsPressed(1));

        rig.Tick(0.4); // 0.6 s held (seen from the first tick at 0.2)
        Assert.False(rig.VJoy.IsPressed(1));
        rig.Tick(0.15);
        Assert.True(rig.VJoy.IsPressed(1));

        rig.Device.Buttons[0] = false;
        rig.Tick();
        Assert.False(rig.VJoy.IsPressed(1));
    }

    [Fact]
    public void Short_and_long_press_share_a_button()
    {
        var rig = new Rig();
        Add(rig, 0, PressTrigger.ShortPress);
        Add(rig, 1, PressTrigger.LongPress);
        rig.Load();
        rig.Tick();

        // Quick tap: only the short-press output, once it's let go.
        rig.Device.Buttons[0] = true;
        rig.Tick(0.1);
        Assert.False(rig.VJoy.IsPressed(1));
        rig.Device.Buttons[0] = false;
        rig.Tick(0.03);
        Assert.True(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.IsPressed(2));
        rig.Tick(MappingEngine.PulseSeconds + 0.05);
        Assert.False(rig.VJoy.IsPressed(1));

        // Long hold: only the long-press output.
        rig.Device.Buttons[0] = true;
        rig.Tick(0.03);
        rig.Tick(0.6);
        Assert.True(rig.VJoy.IsPressed(2));
        rig.Device.Buttons[0] = false;
        rig.Tick(0.03);
        Assert.False(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void Double_tap_fires_on_the_second_quick_press_only()
    {
        var rig = new Rig();
        Add(rig, 0, PressTrigger.DoubleTap);
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true; rig.Tick(0.05);
        Assert.False(rig.VJoy.IsPressed(1));
        rig.Device.Buttons[0] = false; rig.Tick(0.1);
        rig.Device.Buttons[0] = true; rig.Tick(0.05);
        Assert.True(rig.VJoy.IsPressed(1));
        rig.Device.Buttons[0] = false; rig.Tick(0.05);
        Assert.False(rig.VJoy.IsPressed(1));

        // Two presses too far apart don't count.
        rig.Tick(1);
        rig.Device.Buttons[0] = true; rig.Tick(0.05);
        rig.Device.Buttons[0] = false; rig.Tick(0.5);
        rig.Device.Buttons[0] = true; rig.Tick(0.05);
        Assert.False(rig.VJoy.IsPressed(1));
    }

    [Fact]
    public void Short_press_can_drive_a_latch()
    {
        var rig = new Rig();
        Add(rig, 0, PressTrigger.ShortPress, SwitchMode.Latch);
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true; rig.Tick(0.1);
        rig.Device.Buttons[0] = false; rig.Tick(0.03);
        Assert.True(rig.VJoy.IsPressed(1));
        rig.Tick(1);
        Assert.True(rig.VJoy.IsPressed(1));
        Assert.Contains((rig.Device.InstanceGuid, ControlKind.Button, 0, InputPart.Whole), rig.Engine.LatchedSlots());
    }
}

public class AxisZoneTests
{
    [Theory]
    [InlineData(-1000, true, false)]  // 0% of travel
    [InlineData(0, false, false)]     // 50%
    [InlineData(900, false, true)]    // 95%
    public void Zones_act_as_buttons(int raw, bool idle, bool afterburner)
    {
        var rig = new Rig();
        var axis = rig.Axis(AxisType.X);
        rig.Profile.Bindings.Add(new Binding { Source = axis, Target = new OutputRef(1, ControlKind.Button, 0), Part = InputPart.Zone, ZoneFrom = 0, ZoneTo = 0.05 });
        rig.Profile.Bindings.Add(new Binding { Source = axis, Target = new OutputRef(1, ControlKind.Button, 1), Part = InputPart.Zone, ZoneFrom = 0.9, ZoneTo = 1 });
        rig.Profile.AxisCalibrations.Add(new AxisCalibrationEntry { DeviceGuid = rig.Device.InstanceGuid, Axis = AxisType.X, Calibration = new AxisCalibration { DeadZone = 0 } });
        rig.Load();

        rig.Device.Axes[AxisType.X] = raw;
        rig.Tick();

        Assert.Equal(idle, rig.VJoy.IsPressed(1));
        Assert.Equal(afterburner, rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void A_key_can_be_held_while_the_axis_is_in_a_zone()
    {
        var rig = new Rig();
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Axis(AxisType.X), VirtualKeyCode = 0x41, IsZone = true, ZoneFrom = 0.4, ZoneTo = 0.6 });
        rig.Load();
        rig.Engine.KeyboardInjectionEnabled = true;
        rig.Device.Axes[AxisType.X] = -1000;
        rig.Tick();

        rig.Device.Axes[AxisType.X] = 0;
        rig.Tick();
        rig.Device.Axes[AxisType.X] = 1000;
        rig.Tick();

        Assert.Equal(["+65", "-65"], rig.Keys.Events);
        Assert.Equal(InputPart.Zone, InputParts.Of(rig.Profile.KeyBindings[0]));
    }
}

public class KeySequenceTests
{
    private static KeyStep Tap(params int[] keys) => new() { Keys = [.. keys] };

    [Fact]
    public void A_press_plays_the_whole_sequence_in_order()
    {
        var rig = new Rig();
        rig.Profile.KeyBindings.Add(new KeyBinding
        {
            Source = rig.Button(0),
            VirtualKeyCode = 0,
            Sequence = [Tap(0x47), new KeyStep { WaitMs = 200 }, Tap(0xA2, 0x4C)], // G, wait, Ctrl+L
        });
        rig.Load();
        rig.Engine.KeyboardInjectionEnabled = true;
        rig.Tick();

        rig.Device.Buttons[0] = true;
        rig.Tick(0.01);
        Assert.Equal(["+71"], rig.Keys.Events);

        for (var i = 0; i < 30; i++) rig.Tick(0.033);

        Assert.Equal(["+71", "-71", "+162", "+76", "-76", "-162"], rig.Keys.Events);
    }

    [Fact]
    public void Switching_keys_off_mid_sequence_lets_go_of_held_keys()
    {
        var rig = new Rig();
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = 0, Sequence = [Tap(0xA2, 0x4C), Tap(0x47)] });
        rig.Load();
        rig.Engine.KeyboardInjectionEnabled = true;
        rig.Tick();
        rig.Device.Buttons[0] = true;
        rig.Tick(0.01);
        Assert.Equal(["+162", "+76"], rig.Keys.Events);

        rig.Engine.KeyboardInjectionEnabled = false;
        rig.Tick(0.01);
        for (var i = 0; i < 10; i++) rig.Tick(0.033);

        Assert.Equal(2, rig.Keys.Events.Count(e => e.StartsWith('-')));
        Assert.DoesNotContain("+71", rig.Keys.Events);
    }

    [Fact]
    public void A_sequence_with_a_key_outside_the_catalog_never_plays()
    {
        var rig = new Rig();
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = 0, Sequence = [Tap(0x47), Tap(0x5B)] }); // 0x5B = Windows key
        rig.Load();
        rig.Engine.KeyboardInjectionEnabled = true;
        rig.Tick();
        rig.Device.Buttons[0] = true;
        for (var i = 0; i < 10; i++) rig.Tick(0.033);

        Assert.Empty(rig.Keys.Events);
    }
}

public class OutputRecorderTests
{
    [Fact]
    public void Records_what_the_engine_writes()
    {
        var rig = new Rig();
        var recorder = new VJoyOutputRecorder(rig.VJoy);
        var engine = new MappingEngine(() => [rig.Device], recorder, rig.Keys, () => rig.Now);
        rig.ToButton(rig.Button(0), 4);
        rig.Profile.Bindings.Add(new Binding { Source = rig.Axis(AxisType.X), Target = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.Y) });
        engine.LoadProfile(rig.Profile);

        rig.Device.Buttons[0] = true;
        rig.Device.Axes[AxisType.X] = 1000;
        engine.Tick();

        Assert.True(recorder.Button(1, 5));
        Assert.False(recorder.Button(1, 1));
        Assert.Equal(1.0, recorder.Axis(1, VJoyAxis.Y));
        Assert.Null(recorder.Axis(1, VJoyAxis.X));
        Assert.True(rig.VJoy.IsPressed(5)); // still passed on
    }

    [Fact]
    public void Shift_held_is_reported()
    {
        var rig = new Rig();
        rig.Profile.ShiftControl = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 7, InputPart.Whole);
        rig.Load();
        rig.Tick();
        Assert.False(rig.Engine.ShiftHeld);
        rig.Device.Buttons[7] = true;
        rig.Tick();
        Assert.True(rig.Engine.ShiftHeld);
    }
}

public sealed class ProfileBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClaudFlightTests", Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store;

    public ProfileBackupTests() => _store = new ProfileStore(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Backups_are_spaced_out_and_capped()
    {
        _store.Save(new Profile { Name = "P" });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.True(_store.BackUpIfDue("P", TimeSpan.FromMinutes(10), keep: 3, now: t));
        Assert.False(_store.BackUpIfDue("P", TimeSpan.FromMinutes(10), keep: 3, now: t.AddMinutes(5)));
        for (var i = 1; i <= 4; i++) Assert.True(_store.BackUpIfDue("P", TimeSpan.FromMinutes(10), keep: 3, now: t.AddMinutes(10 * i)));

        var backups = _store.ListBackups("P");
        Assert.Equal(3, backups.Count);
        Assert.Equal(t.AddMinutes(40), backups[0].Time);
        Assert.DoesNotContain(_store.ListProfileNames(), n => n != "P"); // backups aren't listed as profiles
    }

    [Fact]
    public void Restoring_a_backup_keeps_the_current_version_too()
    {
        _store.Save(new Profile { Name = "P", DeviceOrder = [Guid.NewGuid()] });
        _store.BackUpIfDue("P", TimeSpan.Zero, now: new DateTime(2026, 1, 1));
        _store.Save(new Profile { Name = "P" }); // later change: order cleared

        var restored = _store.RestoreBackup("P", _store.ListBackups("P").Last());

        Assert.Single(restored.DeviceOrder);
        Assert.Single(_store.Load("P").DeviceOrder);
        Assert.Equal(2, _store.ListBackups("P").Count);
        Assert.Empty(_store.LoadFromFile(_store.ListBackups("P")[0].Path).DeviceOrder);
    }

    [Fact]
    public void Renaming_moves_the_backups()
    {
        _store.Save(new Profile { Name = "Old" });
        _store.BackUpIfDue("Old", TimeSpan.Zero);
        _store.Rename("Old", "New");
        Assert.Empty(_store.ListBackups("Old"));
        Assert.Single(_store.ListBackups("New"));
    }
}
