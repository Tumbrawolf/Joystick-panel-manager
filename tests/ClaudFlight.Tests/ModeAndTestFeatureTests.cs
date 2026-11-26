using System.Text.Json;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Tests;

public class ModeTests
{
    /// <summary>Button 0 presses vJoy B1 in any mode, B2 in Combat (picked by holding button 5);
    /// button 1 presses B3 in any mode.</summary>
    private static Rig CombatRig()
    {
        var rig = new Rig();
        rig.Profile.Modes.Add(new ModeDefinition { Name = "Combat", SelectedBy = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 5, InputPart.Whole) });
        rig.ToButton(rig.Button(0), 0);
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 1), ModeName = "Combat" });
        rig.ToButton(rig.Button(1), 2);
        rig.Load();
        rig.Tick();
        return rig;
    }

    [Fact]
    public void With_no_mode_switch_on_only_any_mode_mappings_apply()
    {
        var rig = CombatRig();
        rig.Device.Buttons[0] = true; rig.Tick();
        Assert.Null(rig.Engine.ActiveMode);
        Assert.True(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void In_a_mode_an_input_uses_its_mappings_for_that_mode_instead()
    {
        var rig = CombatRig();
        rig.Device.Buttons[5] = true; rig.Tick();
        rig.Device.Buttons[0] = true; rig.Device.Buttons[1] = true; rig.Tick();
        Assert.Equal("combat", rig.Engine.ActiveMode, ignoreCase: true);
        Assert.False(rig.VJoy.IsPressed(1));
        Assert.True(rig.VJoy.IsPressed(2));
        Assert.True(rig.VJoy.IsPressed(3)); // nothing for Combat on button 1: its usual mapping still works
    }

    [Fact]
    public void Mode_changes_are_reported_but_not_the_mode_at_start_up()
    {
        var rig = new Rig();
        rig.Profile.Modes.Add(new ModeDefinition { Name = "Nav", SelectedBy = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 5, InputPart.Whole) });
        rig.Load();
        var changes = new List<string?>();
        rig.Engine.ModeChanged += changes.Add;

        rig.Device.Buttons[5] = true;
        rig.Tick();
        Assert.Equal("Nav", rig.Engine.ActiveMode);
        Assert.Empty(changes);

        rig.Device.Buttons[5] = false; rig.Tick();
        rig.Device.Buttons[5] = true; rig.Tick();
        Assert.Equal(new string?[] { null, "Nav" }, changes);
    }

    [Fact]
    public void Shift_works_inside_a_mode()
    {
        var rig = new Rig();
        rig.Profile.Modes.Add(new ModeDefinition { Name = "Combat", SelectedBy = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 5, InputPart.Whole) });
        rig.Profile.ShiftControl = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 6, InputPart.Whole);
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 0), ModeName = "Combat" });
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 1), ModeName = "Combat", Shifted = true });
        rig.ToButton(rig.Button(0), 2, shifted: true); // any-mode shifted: not used in Combat, which has its own
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[5] = true; rig.Device.Buttons[6] = true; rig.Device.Buttons[0] = true; rig.Tick();
        Assert.False(rig.VJoy.IsPressed(1));
        Assert.True(rig.VJoy.IsPressed(2));
        Assert.False(rig.VJoy.IsPressed(3));

        rig.Device.Buttons[6] = false; rig.Tick();
        Assert.True(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void Keys_follow_modes_too()
    {
        var rig = new Rig();
        rig.Profile.Modes.Add(new ModeDefinition { Name = "Landing", SelectedBy = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 5, InputPart.Whole) });
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = 0x47 });                         // G
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = 0x4C, ModeName = "Landing" });  // L
        rig.Load();
        rig.Engine.KeyboardInjectionEnabled = true;
        rig.Tick();

        rig.Device.Buttons[5] = true; rig.Tick();
        rig.Device.Buttons[0] = true; rig.Tick();
        Assert.Contains("+76", rig.Keys.Events);
        Assert.DoesNotContain("+71", rig.Keys.Events);
    }
}

public class AxisPresetAndSpeechTests
{
    [Fact]
    public void A_set_position_mapping_puts_the_axis_there()
    {
        var rig = new Rig();
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.Slider0), Nudge = AxisNudge.SetTo, NudgeAmount = 0.25 });
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(1), Target = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.Slider0), Nudge = AxisNudge.SetTo, NudgeAmount = 1 });
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true; rig.Tick();
        Assert.Equal(-0.5, rig.VJoy.Axes[(1, VJoyAxis.Slider0)], 6);
        rig.Device.Buttons[0] = false; rig.Tick();
        Assert.Equal(-0.5, rig.VJoy.Axes[(1, VJoyAxis.Slider0)], 6); // stays put once let go

        rig.Device.Buttons[1] = true; rig.Tick();
        Assert.Equal(1, rig.VJoy.Axes[(1, VJoyAxis.Slider0)], 6);
    }

    [Fact]
    public void Latch_changes_are_reported_with_their_new_state()
    {
        var rig = new Rig();
        var gear = new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 0), Mode = SwitchMode.Latch, SayOn = "Gear down", SayOff = "Gear up" };
        rig.Profile.Bindings.Add(gear);
        rig.Load();
        var heard = new List<(Binding, bool)>();
        rig.Engine.LatchChanged += (b, on) => heard.Add((b, on));
        rig.Tick();

        for (var i = 0; i < 2; i++)
        {
            rig.Device.Buttons[0] = true; rig.Tick();
            rig.Device.Buttons[0] = false; rig.Tick();
        }
        Assert.Equal([(gear, true), (gear, false)], heard);

        // A reload carrying the latch over isn't a change.
        rig.Engine.LoadProfile(JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(rig.Profile))!);
        rig.Tick();
        Assert.Equal(2, heard.Count);
    }
}

public class TestOutputTests
{
    [Fact]
    public void A_tested_button_is_pressed_briefly_then_let_go()
    {
        var rig = new Rig();
        rig.Load();
        rig.Tick();
        rig.Engine.TestButton(1, 4);
        rig.Tick(0.05);
        Assert.True(rig.VJoy.IsPressed(5));
        rig.Tick(MappingEngine.TestPressSeconds);
        Assert.False(rig.VJoy.IsPressed(5));
    }

    [Fact]
    public void A_tested_button_does_not_let_go_of_one_a_mapping_holds()
    {
        var rig = new Rig();
        rig.ToButton(rig.Button(0), 4);
        rig.Load();
        rig.Device.Buttons[0] = true;
        rig.Tick();
        rig.Engine.TestButton(1, 4);
        rig.Tick(0.05);
        rig.Tick(MappingEngine.TestPressSeconds);
        Assert.True(rig.VJoy.IsPressed(5));
    }

    [Fact]
    public void A_tested_hat_goes_round_then_centers()
    {
        var rig = new Rig();
        rig.Load();
        rig.Tick();
        rig.Engine.TestHat(1, 0);
        var seen = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            rig.Tick(0.033);
            var direction = rig.VJoy.Povs.GetValueOrDefault(((uint)1, 1), -2);
            if (seen.Count == 0 || seen[^1] != direction) seen.Add(direction);
        }
        Assert.Equal([0, 1, 2, 3, -1], seen);
    }

    [Fact]
    public void A_tested_axis_sweeps_both_ends_and_ends_centered()
    {
        var rig = new Rig();
        rig.Profile.Bindings.Add(new Binding { Source = rig.Axis(AxisType.X), Target = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.X) });
        rig.Load();
        rig.Tick();
        rig.Engine.TestAxis(1, VJoyAxis.X);
        double min = 0, max = 0;
        for (var t = 0.0; t < MappingEngine.TestSweepSeconds + 0.1; t += 0.033)
        {
            rig.Tick(0.033);
            min = Math.Min(min, rig.VJoy.Axes[(1, VJoyAxis.X)]);
            max = Math.Max(max, rig.VJoy.Axes[(1, VJoyAxis.X)]);
        }
        Assert.True(max > 0.95 && min < -0.95, $"{min}..{max}");

        rig.Device.Axes[AxisType.X] = 1000; // the hardware takes over again
        rig.Tick();
        Assert.True(rig.VJoy.Axes[(1, VJoyAxis.X)] > 0.9);
    }

    [Fact]
    public void A_tested_key_waits_for_the_delay_and_needs_keys_on()
    {
        var rig = new Rig();
        rig.Load();
        rig.Tick();
        var key = new KeyBinding { Source = rig.Button(0), VirtualKeyCode = 0x47 };
        Assert.False(rig.Engine.TestKeys(key, 1));

        rig.Engine.KeyboardInjectionEnabled = true;
        Assert.True(rig.Engine.TestKeys(key, 1));
        rig.Tick(0.5);
        Assert.Empty(rig.Keys.Events);
        for (var i = 0; i < 10; i++) rig.Tick(0.1);
        Assert.Equal(["+71", "-71"], rig.Keys.Events);
    }

    [Fact]
    public void A_tested_preset_moves_the_axis_at_once()
    {
        var rig = new Rig();
        rig.Load();
        rig.Engine.TestNudge(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.Z), Nudge = AxisNudge.SetTo, NudgeAmount = 0.75 });
        Assert.Equal(0.5, rig.VJoy.Axes[(1, VJoyAxis.Z)], 6);
    }
}

public class ConflictTests
{
    private static readonly Guid Stick = Guid.NewGuid();
    private static InputRef Button(int i) => new(Stick, ControlKind.Button, i);

    private static Binding ToButton(int source, int target, string? mode = null) =>
        new() { Source = Button(source), Target = new OutputRef(1, ControlKind.Button, target), ModeName = mode };

    [Fact]
    public void Two_inputs_pressing_one_vjoy_button_clash()
    {
        var profile = new Profile { Bindings = [ToButton(0, 4), ToButton(1, 4), ToButton(2, 5)] };
        var conflict = Assert.Single(MappingConflicts.Find(profile));
        Assert.Equal("vJoy 1 button 5", conflict.Output);
        Assert.Equal(2, conflict.Mappings.Count);
    }

    [Fact]
    public void One_input_with_two_mappings_onto_the_same_button_is_not_a_clash()
    {
        var profile = new Profile { Bindings = [ToButton(0, 4), new Binding { Source = Button(0), Target = new OutputRef(1, ControlKind.Button, 4), Trigger = PressTrigger.LongPress }] };
        Assert.Empty(MappingConflicts.Find(profile));
    }

    [Fact]
    public void Mappings_in_different_modes_never_clash_but_any_mode_ones_do()
    {
        Assert.Empty(MappingConflicts.Find(new Profile { Bindings = [ToButton(0, 4, "Nav"), ToButton(1, 4, "Combat")] }));
        Assert.Single(MappingConflicts.Find(new Profile { Bindings = [ToButton(0, 4, "Nav"), ToButton(1, 4)] }));
    }

    [Fact]
    public void Trim_buttons_sharing_an_axis_are_fine_but_an_axis_and_a_button_fight()
    {
        var axisTarget = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.Slider0);
        var up = new Binding { Source = Button(0), Target = axisTarget, Nudge = AxisNudge.Increase };
        var down = new Binding { Source = Button(1), Target = axisTarget, Nudge = AxisNudge.Decrease };
        Assert.Empty(MappingConflicts.Find(new Profile { Bindings = [up, down] }));

        var throttle = new Binding { Source = new InputRef(Stick, ControlKind.Axis, (int)AxisType.Z), Target = axisTarget };
        var conflict = Assert.Single(MappingConflicts.Find(new Profile { Bindings = [up, down, throttle] }));
        Assert.Equal(3, conflict.Mappings.Count);
    }

    [Fact]
    public void Two_inputs_pressing_one_key_clash_but_sequences_do_not()
    {
        var profile = new Profile
        {
            KeyBindings =
            [
                new KeyBinding { Source = Button(0), VirtualKeyCode = 0x47 },
                new KeyBinding { Source = Button(1), VirtualKeyCode = 0x47 },
                new KeyBinding { Source = Button(2), VirtualKeyCode = 0x47, Sequence = [new KeyStep { Keys = [0x47] }] },
            ],
        };
        var conflict = Assert.Single(MappingConflicts.Find(profile));
        Assert.Equal("key G", conflict.Output, ignoreCase: true);
        Assert.Equal(2, conflict.Mappings.Count);
    }
}

public class ModeModelTests
{
    [Fact]
    public void A_copy_in_another_mode_keeps_everything_else()
    {
        var binding = new Binding
        {
            Source = new InputRef(Guid.NewGuid(), ControlKind.Button, 3), Target = new OutputRef(2, ControlKind.Button, 7),
            Mode = SwitchMode.Latch, SayOn = "On", Trigger = PressTrigger.LongPress, ModeName = "Nav",
        };
        var copy = ModelCopy.WithModeName(binding, "Combat");
        Assert.Equal("Combat", copy.ModeName);
        Assert.Equal(SwitchMode.Latch, copy.Mode);
        Assert.Equal("On", copy.SayOn);
        Assert.Equal(PressTrigger.LongPress, copy.Trigger);
        Assert.Null(ModelCopy.WithModeName(binding, null).ModeName);
    }

    [Fact]
    public void Modes_survive_saving_and_old_profiles_load_without_any()
    {
        var profile = new Profile { AnnounceModes = true };
        profile.Modes.Add(new ModeDefinition { Name = "Nav", SelectedBy = new ShiftControl(Guid.NewGuid(), ControlKind.Button, 2, InputPart.Whole) });
        var back = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!;
        Assert.True(back.AnnounceModes);
        Assert.Equal("Nav", Assert.Single(back.Modes).Name);
        Assert.Equal(2, back.Modes[0].SelectedBy!.Index);

        var old = JsonSerializer.Deserialize<Profile>("""{"Name":"Old","Bindings":[]}""")!;
        Assert.Empty(old.Modes);
    }
}
