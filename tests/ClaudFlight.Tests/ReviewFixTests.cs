using System.Text.Json;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;

namespace ClaudFlight.Tests;

public class ReviewFixTests
{
    [Fact]
    public void Reloading_identical_copies_keeps_latches()
    {
        var rig = new Rig();
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 0), Mode = SwitchMode.Latch });
        rig.Load();
        rig.Tick();
        rig.Device.Buttons[0] = true; rig.Tick();
        rig.Device.Buttons[0] = false; rig.Tick();
        Assert.True(rig.VJoy.IsPressed(1));
        var version = rig.Engine.LatchVersion;

        // What undo does: the same profile, read back as brand-new objects.
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(rig.Profile))!;
        rig.Engine.LoadProfile(copy);
        rig.Tick();

        Assert.True(rig.VJoy.IsPressed(1));
        Assert.NotEqual(version, rig.Engine.LatchVersion);
    }

    [Fact]
    public void Latch_version_changes_when_a_latch_flips()
    {
        var rig = new Rig();
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 0), Mode = SwitchMode.Latch });
        rig.Load();
        rig.Tick();
        var before = rig.Engine.LatchVersion;
        rig.Tick();
        Assert.Equal(before, rig.Engine.LatchVersion);
        rig.Device.Buttons[0] = true; rig.Tick();
        Assert.NotEqual(before, rig.Engine.LatchVersion);
    }

    private static Rig ShortAndDouble()
    {
        var rig = new Rig();
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 0), Trigger = PressTrigger.ShortPress });
        rig.Profile.Bindings.Add(new Binding { Source = rig.Button(0), Target = new OutputRef(1, ControlKind.Button, 1), Trigger = PressTrigger.DoubleTap });
        rig.Load();
        rig.Tick();
        return rig;
    }

    [Fact]
    public void A_double_tap_does_not_also_fire_the_short_press()
    {
        var rig = ShortAndDouble();
        var shortFired = false;
        void Step(bool pressed, double seconds)
        {
            rig.Device.Buttons[0] = pressed;
            rig.Tick(seconds);
            shortFired |= rig.VJoy.IsPressed(1);
        }

        Step(true, 0.05); Step(false, 0.1); Step(true, 0.05);
        Assert.True(rig.VJoy.IsPressed(2));
        Step(false, 0.05);
        for (var i = 0; i < 20; i++) Step(false, 0.05);

        Assert.False(shortFired);
    }

    [Fact]
    public void A_single_tap_fires_the_short_press_once_the_double_tap_window_has_passed()
    {
        var rig = ShortAndDouble();
        rig.Device.Buttons[0] = true; rig.Tick(0.05);
        rig.Device.Buttons[0] = false; rig.Tick(0.05);
        rig.Tick(0.2);
        Assert.False(rig.VJoy.IsPressed(1)); // still waiting for a possible second tap
        rig.Tick(0.2);
        Assert.True(rig.VJoy.IsPressed(1));
        Assert.False(rig.VJoy.IsPressed(2));
    }

    [Fact]
    public void A_sequence_does_not_let_go_of_a_key_a_hold_mapping_has_down()
    {
        var rig = new Rig();
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(0), VirtualKeyCode = 0xA0, HoldWhilePressed = true });
        rig.Profile.KeyBindings.Add(new KeyBinding { Source = rig.Button(1), VirtualKeyCode = 0, Sequence = [new KeyStep { Keys = [0xA0, 0x70] }] });
        rig.Load();
        rig.Engine.KeyboardInjectionEnabled = true;
        rig.Tick();

        rig.Device.Buttons[0] = true; rig.Tick();
        rig.Device.Buttons[1] = true;
        for (var i = 0; i < 10; i++) rig.Tick();
        Assert.DoesNotContain("-160", rig.Keys.Events);
        Assert.Contains("-112", rig.Keys.Events);

        rig.Device.Buttons[0] = false; rig.Tick();
        Assert.Equal("-160", rig.Keys.Events[^1]);
    }
}

public sealed class BackupFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClaudFlightTests", Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store;

    public BackupFixTests() => _store = new ProfileStore(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Deleting_a_profile_deletes_its_backups()
    {
        _store.Save(new Profile { Name = "P" });
        _store.BackUpIfDue("P", TimeSpan.Zero);
        _store.Delete("P");
        _store.Save(new Profile { Name = "P" });
        Assert.Empty(_store.ListBackups("P"));
    }

    [Fact]
    public void Renaming_onto_a_name_with_backups_merges_them()
    {
        _store.Save(new Profile { Name = "Old" });
        _store.BackUpIfDue("Old", TimeSpan.Zero, now: new DateTime(2026, 1, 1, 10, 0, 0));
        // A stray folder for the new name, e.g. left by an older version of the app.
        Directory.CreateDirectory(Path.Combine(_store.BackupsDirectory, "New"));
        File.WriteAllText(Path.Combine(_store.BackupsDirectory, "New", "2025-06-01 09-00-00.json"), _store.Serialize(new Profile { Name = "New" }));

        _store.Rename("Old", "New");

        Assert.Equal(2, _store.ListBackups("New").Count);
        Assert.False(Directory.Exists(Path.Combine(_store.BackupsDirectory, "Old")));
    }

    [Fact]
    public void A_backup_is_still_made_after_the_clock_goes_back()
    {
        _store.Save(new Profile { Name = "P" });
        var t = new DateTime(2026, 10, 25, 2, 55, 0);
        Assert.True(_store.BackUpIfDue("P", TimeSpan.FromMinutes(10), now: t));
        Assert.True(_store.BackUpIfDue("P", TimeSpan.FromMinutes(10), now: t.AddMinutes(-50)));
        Assert.Equal(2, _store.ListBackups("P").Count);
    }
}
