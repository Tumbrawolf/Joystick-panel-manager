using System.Windows;
using ClaudFlight.App.ViewModels;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;
using Binding = ClaudFlight.Core.Models.Binding;
using KeyBinding = ClaudFlight.Core.Models.KeyBinding;

namespace ClaudFlight.App;

// Modes (whole mapping layers picked by a switch), spoken latches, conflict warnings, and the remap
// window's Test button.
public partial class MainWindow
{
    private readonly Speaker _speaker = new();
    private string? _shownMode = "\0"; // never a real mode, so the first check shows it
    private int _shownModeCount = -1;
    private Dictionary<object, MappingConflict> _conflictOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>How long a key test waits before sending, so there's time to switch to the game.</summary>
    private const double KeyTestDelaySeconds = 3;

    private void HookEngineEvents()
    {
        _engine.LatchChanged += (binding, on) =>
        {
            if ((on ? binding.SayOn : binding.SayOff) is { Length: > 0 } words) _speaker.Say(words);
        };
        _engine.ModeChanged += mode =>
        {
            if (_currentProfile.AnnounceModes) _speaker.Say(mode ?? "No mode");
        };
    }

    // ---- Modes ----

    internal IReadOnlyList<ModeDefinition> Modes => _currentProfile.Modes;
    internal IReadOnlyList<string> ModeNames => _currentProfile.Modes.Select(m => m.Name).ToList();
    internal IReadOnlyList<DeviceMonitorViewModel> PickableDevices => Devices;

    internal bool AnnounceModes
    {
        get => _currentProfile.AnnounceModes;
        set => _currentProfile.AnnounceModes = value;
    }

    private static bool SameMode(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal string ModeSwitchName(ShiftControl? control) => control is null
        ? "no switch"
        : $"{ControlName(control.DeviceGuid, control.Kind, control.Index, control.Part)} ({DeviceName(control.DeviceGuid, "not connected")})";

    internal int MappingsInMode(string name) =>
        _currentProfile.Bindings.Count(b => SameMode(b.ModeName, name)) + _currentProfile.KeyBindings.Count(k => SameMode(k.ModeName, name));

    internal ModeDefinition AddMode(string name, ShiftControl selectedBy)
    {
        var mode = new ModeDefinition { Name = name, SelectedBy = selectedBy };
        _currentProfile.Modes.Add(mode);
        AfterRemapsChanged();
        return mode;
    }

    internal void SetModeSwitch(ModeDefinition mode, ShiftControl selectedBy)
    {
        mode.SelectedBy = selectedBy;
        AfterRemapsChanged();
    }

    /// <summary>Renames a mode, taking its mappings along.</summary>
    internal void RenameMode(ModeDefinition mode, string name)
    {
        var old = mode.Name;
        var bindings = _currentProfile.Bindings;
        for (var i = 0; i < bindings.Count; i++)
        {
            if (SameMode(bindings[i].ModeName, old)) bindings[i] = ModelCopy.WithModeName(bindings[i], name);
        }
        var keys = _currentProfile.KeyBindings;
        for (var i = 0; i < keys.Count; i++)
        {
            if (SameMode(keys[i].ModeName, old)) keys[i] = ModelCopy.WithModeName(keys[i], name);
        }
        mode.Name = name;
        AfterRemapsChanged();
    }

    /// <summary>Asks, then removes a mode and its mappings. True if it was removed.</summary>
    internal bool ConfirmDeleteMode(ModeDefinition mode, Window owner)
    {
        var count = MappingsInMode(mode.Name);
        var message = count == 0
            ? $"Remove mode '{mode.Name}'?"
            : $"Remove mode '{mode.Name}' and its {count} mapping(s)? Undo (Ctrl+Z) brings them back.";
        if (MessageBox.Show(owner, message, "Modes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;

        _currentProfile.Bindings.RemoveAll(b => SameMode(b.ModeName, mode.Name));
        _currentProfile.KeyBindings.RemoveAll(k => SameMode(k.ModeName, mode.Name));
        _currentProfile.Modes.Remove(mode);
        AfterRemapsChanged();
        return true;
    }

    private void Modes_Click(object sender, RoutedEventArgs e) => new ModesWindow(this).ShowDialog();

    /// <summary>The top bar's "Mode: Nav" (hidden when the profile has no modes).</summary>
    private void RefreshModeStatus()
    {
        var mode = _engine.ActiveMode;
        var count = _currentProfile.Modes.Count;
        if (mode == _shownMode && count == _shownModeCount) return;
        _shownMode = mode;
        _shownModeCount = count;
        ModeStatusText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ModeStatusText.Text = mode is null ? "Mode: none" : $"Mode: {mode}";
    }

    // ---- Conflicts ----

    private void RefreshConflicts()
    {
        _conflictOf = new Dictionary<object, MappingConflict>(ReferenceEqualityComparer.Instance);
        foreach (var conflict in MappingConflicts.Find(_currentProfile))
        {
            foreach (var mapping in conflict.Mappings) _conflictOf.TryAdd(mapping, conflict);
        }
    }

    private sealed record MappingFacts((Guid, ControlKind, int, InputPart) Slot, string? ModeName, bool IsNudge, string SourceName);

    private MappingFacts? Facts(object mapping) => mapping switch
    {
        Binding b => new MappingFacts((b.Source.DeviceGuid, b.Source.Kind, b.Source.Index, b.Part), b.ModeName, b.Nudge != AxisNudge.None,
            SourceName(b.Source, b.Part)),
        KeyBinding k => new MappingFacts((k.Source.DeviceGuid, k.Source.Kind, k.Source.Index, InputParts.Of(k)), k.ModeName, false,
            SourceName(k.Source, InputParts.Of(k))),
        _ => null,
    };

    private string SourceName(InputRef source, InputPart part) =>
        $"{ControlName(source.DeviceGuid, source.Kind, source.Index, part)} ({DeviceName(source.DeviceGuid, "not connected")})";

    /// <summary>"⚠ vJoy 1 button 5 is also driven by ..." for a mapping that clashes with others, else "".</summary>
    internal string ConflictNote(object mapping)
    {
        if (!_conflictOf.TryGetValue(mapping, out var conflict) || Facts(mapping) is not { } mine) return "";
        var others = conflict.Mappings
            .Where(m => !ReferenceEquals(m, mapping))
            .Select(Facts)
            .Where(f => f is not null && f.Slot != mine.Slot && !(f.IsNudge && mine.IsNudge) && MappingConflicts.ModesOverlap(f.ModeName, mine.ModeName))
            .Select(f => f!.SourceName)
            .Distinct()
            .ToList();
        return others.Count == 0 ? "" : $"⚠ {conflict.Output} is also driven by {string.Join(", ", others)}";
    }

    private bool IsOtherSlot(InputRef source, InputPart part, RemapSlot slot) =>
        source.DeviceGuid != slot.DeviceGuid || source.Kind != slot.Kind || source.Index != slot.Index || part != slot.Part;

    /// <summary>For the remap window: other inputs already driving a vJoy output.</summary>
    internal string TargetClashNote(OutputRef target, bool isNudge, RemapSlot slot, string? modeName)
    {
        var others = _currentProfile.Bindings
            .Where(b => b.Target == target && IsOtherSlot(b.Source, b.Part, slot) && !(isNudge && b.Nudge != AxisNudge.None) &&
                        MappingConflicts.ModesOverlap(b.ModeName, modeName))
            .Select(b => SourceName(b.Source, b.Part))
            .Distinct()
            .ToList();
        return others.Count == 0 ? "" : $"⚠ Already driven by {string.Join(", ", others)} - both will work it.";
    }

    /// <summary>For the remap window: other inputs already pressing a key.</summary>
    internal string KeyClashNote(int virtualKey, RemapSlot slot, string? modeName)
    {
        var others = _currentProfile.KeyBindings
            .Where(k => k.Sequence is not { Count: > 0 } && k.VirtualKeyCode == virtualKey && IsOtherSlot(k.Source, InputParts.Of(k), slot) &&
                        MappingConflicts.ModesOverlap(k.ModeName, modeName))
            .Select(k => SourceName(k.Source, InputParts.Of(k)))
            .Distinct()
            .ToList();
        return others.Count == 0 ? "" : $"⚠ Already pressed by {string.Join(", ", others)}.";
    }

    // ---- Test button ----

    /// <summary>Sends what a mapping would, once, without the hardware. Returns what was done and
    /// how long the test keeps going (the window holds off another test until then).</summary>
    internal (string Message, double BusySeconds) TestRemap(object output)
    {
        if (output is KeyBinding key)
        {
            if (!KeyboardKeysOn) return ("Keyboard keys are off, so nothing was sent. Tick \"Keyboard keys on\" at the top of the main window first.", 0);
            if (!_engine.TestKeys(key, KeyTestDelaySeconds)) return ("That key can't be sent.", 0);
            return ($"Switch to the game now: {(key.Sequence is { Count: > 0 } ? "the keys are" : "the key is")} sent in {KeyTestDelaySeconds:0} seconds.",
                KeyTestDelaySeconds + 1);
        }
        if (output is not Binding binding) return ("", 0);
        if (!_vjoyManager.IsAvailable) return ("vJoy isn't available, so nothing was sent.", 0);

        var target = binding.Target;
        if (binding.Nudge != AxisNudge.None)
        {
            _engine.TestNudge(binding);
            return ($"Moved vJoy {target.VJoyDeviceId} {(VJoyAxis)target.Index} as one press would.", 0);
        }
        switch (target.Kind)
        {
            case ControlKind.Axis:
                _engine.TestAxis(target.VJoyDeviceId, (VJoyAxis)target.Index);
                return ($"Sweeping vJoy {target.VJoyDeviceId} {(VJoyAxis)target.Index} to each end and back.", MappingEngine.TestSweepSeconds);
            case ControlKind.Pov:
                _engine.TestHat(target.VJoyDeviceId, target.Index);
                return ($"Pushing vJoy {target.VJoyDeviceId} hat {target.Index + 1} up, right, down and left.", MappingEngine.TestPressSeconds * 4);
            default:
                _engine.TestButton(target.VJoyDeviceId, target.Index);
                return ($"Pressed vJoy {target.VJoyDeviceId} button {target.Index + 1}.", MappingEngine.TestPressSeconds);
        }
    }
}
