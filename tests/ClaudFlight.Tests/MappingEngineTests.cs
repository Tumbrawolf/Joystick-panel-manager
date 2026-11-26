using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Tests;

public class VJoyNumberingTests
{
    [Fact]
    public void Button_shown_as_1_presses_vJoy_button_1()
    {
        var rig = new Rig();
        rig.ToButton(rig.Button(0), buttonIndex: 0); // "vJoy button 1" in the UI
        rig.Load();

        rig.Device.Buttons[0] = true;
        rig.Tick();

        Assert.True(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.Buttons.ContainsKey((1, 0))); // vJoy has no button 0
    }

    [Fact]
    public void Hat_shown_as_1_drives_vJoy_hat_1()
    {
        var rig = new Rig();
        rig.Profile.Bindings.Add(new Binding { Source = rig.Pov(), Target = new OutputRef(1, ControlKind.Pov, 0) });
        rig.Load();

        rig.Device.Povs[0] = 9000; // east
        rig.Tick();

        Assert.Equal(1, rig.VJoy.Povs[(1, 1)]);
    }
}

public class InputPartTests
{
    [Theory]
    [InlineData(0, true, false, false, false)]       // N
    [InlineData(4500, true, true, false, false)]     // NE presses both neighbours
    [InlineData(9000, false, true, false, false)]    // E
    [InlineData(22500, false, false, true, true)]    // SW
    [InlineData(31500, true, false, false, true)]    // NW
    [InlineData(-1, false, false, false, false)]     // centered
    public void Hat_directions_act_as_separate_buttons(int angle, bool up, bool right, bool down, bool left)
    {
        var rig = new Rig();
        rig.ToButton(rig.Pov(), 0, InputPart.Up);
        rig.ToButton(rig.Pov(), 1, InputPart.Right);
        rig.ToButton(rig.Pov(), 2, InputPart.Down);
        rig.ToButton(rig.Pov(), 3, InputPart.Left);
        rig.Load();

        rig.Device.Povs[0] = angle;
        rig.Tick();

        Assert.Equal([up, right, down, left], [rig.VJoy.IsPressed(1), rig.VJoy.IsPressed(2), rig.VJoy.IsPressed(3), rig.VJoy.IsPressed(4)]);
    }

    [Theory]
    [InlineData(800, true, false)]
    [InlineData(200, false, false)]
    [InlineData(-800, false, true)]
    public void Axis_halves_press_past_their_threshold(int raw, bool positive, bool negative)
    {
        var rig = new Rig();
        rig.ToButton(rig.Axis(AxisType.X), 0, InputPart.Positive);
        rig.ToButton(rig.Axis(AxisType.X), 1, InputPart.Negative);
        rig.Load();

        rig.Device.Axes[AxisType.X] = raw;
        rig.Tick();

        Assert.Equal(positive, rig.VJoy.IsPressed(1));
        Assert.Equal(negative, rig.VJoy.IsPressed(2));
    }
}

public class SwitchModeTests
{
    [Fact]
    public void Pulse_on_press_gives_one_short_press()
    {
        var rig = new Rig();
        rig.ToButton(rig.Button(0), 0, mode: SwitchMode.PulseOnPress);
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true;
        rig.Tick();
        Assert.True(rig.VJoy.IsPressed(1));

        rig.Tick(MappingEngine.PulseSeconds + 0.01); // still held, pulse over
        Assert.False(rig.VJoy.IsPressed(1));

        rig.Device.Buttons[0] = false;
        rig.Tick();
        Assert.False(rig.VJoy.IsPressed(1)); // no pulse on release
    }

    [Fact]
    public void Pulse_on_both_also_pulses_when_switched_off()
    {
        var rig = new Rig();
        rig.ToButton(rig.Button(0), 0, mode: SwitchMode.PulseOnBoth);
        rig.Load();
        rig.Device.Buttons[0] = true;
        rig.Tick();
        rig.Tick(1);

        rig.Device.Buttons[0] = false;
        rig.Tick();
        Assert.True(rig.VJoy.IsPressed(1));
    }

    [Fact]
    public void A_switch_already_on_at_startup_does_not_pulse()
    {
        var rig = new Rig();
        rig.ToButton(rig.Button(0), 0, mode: SwitchMode.PulseOnBoth);
        rig.Device.Buttons[0] = true;
        rig.Load();

        rig.Tick();
        Assert.False(rig.VJoy.IsPressed(1));
    }

    [Fact]
    public void Latch_toggles_on_each_press()
    {
        var rig = new Rig();
        rig.ToButton(rig.Button(0), 0, mode: SwitchMode.Latch);
        rig.Load();
        rig.Tick();

        void Press()
        {
            rig.Device.Buttons[0] = true;
            rig.Tick();
            rig.Device.Buttons[0] = false;
            rig.Tick();
        }

        Press();
        Assert.True(rig.VJoy.IsPressed(1));
        Press();
        Assert.False(rig.VJoy.IsPressed(1));
    }
}

public class ShiftLayerTests
{
    private static Rig ShiftRig()
    {
        var rig = new Rig();
        rig.Profile.ShiftControl = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 7, InputPart.Whole);
        rig.ToButton(rig.Button(0), 0);                  // button 1 -> vJoy 1
        rig.ToButton(rig.Button(0), 1, shifted: true);   // shift + button 1 -> vJoy 2
        rig.ToButton(rig.Button(1), 2);                  // button 2 -> vJoy 3, no shifted mapping
        rig.Load();
        return rig;
    }

    [Fact]
    public void Without_shift_the_normal_mapping_is_used()
    {
        var rig = ShiftRig();
        rig.Device.Buttons[0] = true;
        rig.Tick();

        Assert.True(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void With_shift_held_the_shifted_mapping_replaces_the_normal_one()
    {
        var rig = ShiftRig();
        rig.Device.Buttons[7] = true;
        rig.Device.Buttons[0] = true;
        rig.Tick();

        Assert.False(rig.VJoy.IsPressed(1));
        Assert.True(rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void Inputs_without_a_shifted_mapping_keep_working_while_shifted()
    {
        var rig = ShiftRig();
        rig.Device.Buttons[7] = true;
        rig.Device.Buttons[1] = true;
        rig.Tick();

        Assert.True(rig.VJoy.IsPressed(3));
    }

    [Fact]
    public void Pressing_shift_while_a_toggle_is_on_does_not_fire_a_pulse()
    {
        var rig = new Rig();
        rig.Profile.ShiftControl = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 7, InputPart.Whole);
        rig.ToButton(rig.Button(0), 0, mode: SwitchMode.PulseOnBoth);
        rig.ToButton(rig.Button(0), 1, shifted: true);
        rig.Load();
        rig.Device.Buttons[0] = true;
        rig.Tick();
        rig.Tick(1);

        rig.Device.Buttons[7] = true;
        rig.Tick();
        Assert.False(rig.VJoy.IsPressed(1));
    }
}

public class AxisNudgeTests
{
    private static Binding Nudge(Rig rig, int button, AxisNudge nudge, double amount, SwitchMode mode = SwitchMode.Hold)
    {
        var binding = new Binding
        {
            Source = rig.Button(button),
            Target = new OutputRef(1, ControlKind.Axis, (int)VJoyAxis.Slider0),
            Nudge = nudge,
            NudgeAmount = amount,
            Mode = mode,
        };
        rig.Profile.Bindings.Add(binding);
        return binding;
    }

    private static double Slider(Rig rig) => rig.VJoy.Axes[(1, VJoyAxis.Slider0)];

    [Fact]
    public void Holding_moves_the_axis_at_the_set_rate()
    {
        var rig = new Rig();
        Nudge(rig, 0, AxisNudge.Increase, 0.25); // a quarter of full travel per second
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true;
        for (var i = 0; i < 10; i++) rig.Tick(0.1);

        Assert.Equal(0.5, Slider(rig), 3); // -1..1 axis: a quarter of travel per second for 1s = 0.5
    }

    [Fact]
    public void Each_press_moves_one_step_and_center_resets()
    {
        var rig = new Rig();
        Nudge(rig, 0, AxisNudge.Decrease, 0.1, SwitchMode.PulseOnPress);
        Nudge(rig, 1, AxisNudge.Center, 0);
        rig.Load();
        rig.Tick();

        for (var i = 0; i < 3; i++)
        {
            rig.Device.Buttons[0] = true;
            rig.Tick();
            rig.Device.Buttons[0] = false;
            rig.Tick();
        }
        Assert.Equal(-0.6, Slider(rig), 3);

        rig.Device.Buttons[1] = true;
        rig.Tick();
        Assert.Equal(0, Slider(rig), 3);
    }
}

public class KeyBindingTests
{
    private static readonly int Key = KeyCatalog.Keys[0].VKey;

    [Fact]
    public void Tap_on_release_taps_on_every_flip()
    {
        var rig = new Rig { Engine = { KeyboardInjectionEnabled = true } };
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = Key, HoldWhilePressed = false, TapOnRelease = true });
        rig.Load();
        rig.Tick();

        rig.Device.Buttons[0] = true;
        rig.Tick();
        rig.Device.Buttons[0] = false;
        rig.Tick();

        Assert.Equal([$"+{Key}", $"-{Key}", $"+{Key}", $"-{Key}"], rig.Keys.Events);
    }

    [Fact]
    public void Shifted_key_only_fires_with_shift_held()
    {
        var rig = new Rig { Engine = { KeyboardInjectionEnabled = true } };
        rig.Profile.ShiftControl = new ShiftControl(rig.Device.InstanceGuid, ControlKind.Button, 7, InputPart.Whole);
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = Key, Shifted = true });
        rig.Load();

        rig.Device.Buttons[0] = true;
        rig.Tick();
        Assert.Empty(rig.Keys.Events);

        rig.Device.Buttons[7] = true;
        rig.Tick();
        Assert.Equal([$"+{Key}"], rig.Keys.Events);
    }
}
