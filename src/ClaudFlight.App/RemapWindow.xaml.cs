using System.Windows;
using System.Windows.Controls;
using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App;

/// <summary>One remappable input: a physical control, or one part of it (a hat direction, an
/// axis half), plus the names to show for it.</summary>
public sealed record RemapSlot(Guid DeviceGuid, ControlKind Kind, int Index, InputPart Part, string Label, string DeviceLabel)
{
    /// <summary>True for inputs that are simply on or off (a button, a hat direction, an axis
    /// half), as opposed to a whole hat or axis passed straight through.</summary>
    public bool IsSwitch => Kind == ControlKind.Button || Part != InputPart.Whole;
}

/// <summary>Something a slot currently does: a vJoy Binding or a KeyBinding, described for display.</summary>
public sealed record RemapOutput(string Description, object Source, bool Shifted)
{
    public override string ToString() => Description;
}

/// <summary>What the connected vJoy device offers, or Available = false when vJoy isn't installed
/// (in which case every slot is offered).</summary>
public sealed record VJoyLimits(bool Available, int Buttons, int Hats, Func<VJoyAxis, bool> HasAxis);

/// <summary>Shows and edits everything one input does - vJoy outputs, simulated keys, axis nudges,
/// in the normal or shifted layer - opened by clicking its marker on the Layout tab. Changes apply
/// to the profile straight away.</summary>
public partial class RemapWindow : Window
{
    private enum OutputType { VJoyButton, VJoyAxis, VJoyHat, Key, Nudge }

    // ToString is what screen readers say for a dropdown entry.
    private sealed record Option<T>(string Display, T Value)
    {
        public override string ToString() => Display;
    }

    private sealed record TargetOption(string Display, int Index, bool InUse)
    {
        public override string ToString() => Display;
    }

    private readonly MainWindow _host;
    private readonly RemapSlot _slot;
    private bool _loading = true;

    /// <summary>The output being changed (loaded into the form), or null when the form adds a new one.</summary>
    private object? _editing;

    /// <summary>Opens the editor for one input; with editOutput, that output (one of the input's
    /// Bindings/KeyBindings) is loaded into the form ready to change.</summary>
    internal RemapWindow(MainWindow host, RemapSlot slot, object? editOutput = null)
    {
        InitializeComponent();
        Owner = host;
        _host = host;
        _slot = slot;

        Title = $"Remap {slot.Label}";
        SlotLabelText.Text = slot.Label;
        SlotDeviceText.Text = slot.DeviceLabel;
        SlotHelpText.Text = (slot.Kind, slot.Part) switch
        {
            (ControlKind.Pov, InputPart.Whole) => "The whole hat, passed straight through to a vJoy hat. To give each direction its own button or key, use the hat's ↑ → ↓ ← markers instead.",
            (ControlKind.Axis, InputPart.Whole) => "The whole axis, passed straight through to a vJoy axis. To use either end of its travel as a button or key, use its + and − markers instead.",
            (_, InputPart.Up or InputPart.Right or InputPart.Down or InputPart.Left) => "One direction of the hat, acting as a button. Pushing it diagonally presses both neighbouring directions.",
            (_, InputPart.Positive or InputPart.Negative) => "One end of the axis's travel, acting as a button: it counts as pressed once the axis is pushed past the point below.",
            _ => "A button: it can press vJoy buttons and/or keyboard keys, or move a vJoy axis - as many as you like at once.",
        };

        List<Option<OutputType>> types = (slot.Kind, slot.Part) switch
        {
            (ControlKind.Pov, InputPart.Whole) => [new("vJoy hat (all directions)", OutputType.VJoyHat)],
            (ControlKind.Axis, InputPart.Whole) => [new("vJoy axis", OutputType.VJoyAxis)],
            _ =>
            [
                new("vJoy button", OutputType.VJoyButton),
                new("Keyboard key", OutputType.Key),
                new("Move a vJoy axis (e.g. trim)", OutputType.Nudge),
            ],
        };
        OutputTypeCombo.ItemsSource = types;
        KeyCombo.ItemsSource = KeyCatalog.Keys;
        KeyCombo.SelectedIndex = 0;
        NudgeCombo.ItemsSource = new List<Option<AxisNudge>>
        {
            new("Up (increase)", AxisNudge.Increase),
            new("Down (decrease)", AxisNudge.Decrease),
            new("Back to center", AxisNudge.Center),
        };
        NudgeCombo.SelectedIndex = 0;

        UseAsShiftCheck.Visibility = slot.IsSwitch ? Visibility.Visible : Visibility.Collapsed;
        UseAsShiftCheck.IsChecked = host.IsShiftControl(slot);
        ThresholdRow.Visibility = slot.Part is InputPart.Positive or InputPart.Negative ? Visibility.Visible : Visibility.Collapsed;
        KeyboardNote.Visibility = types.Any(t => t.Value == OutputType.Key) && !host.KeyboardKeysOn ? Visibility.Visible : Visibility.Collapsed;

        OutputTypeCombo.SelectedIndex = 0;
        if (editOutput is Binding { Shifted: true } or KeyBinding { Shifted: true }) ShiftLayerRadio.IsChecked = true;
        _loading = false;
        RefreshShiftUi();
        RefreshAddRows();
        if (editOutput is not null) BeginEdit(editOutput);
    }

    private OutputType SelectedType => (OutputTypeCombo.SelectedItem as Option<OutputType>)?.Value ?? OutputType.VJoyButton;
    private AxisNudge SelectedNudge => (NudgeCombo.SelectedItem as Option<AxisNudge>)?.Value ?? AxisNudge.Increase;
    private bool ShiftedLayer => ShiftLayerRadio.IsChecked == true;

    // ---- Shift ----

    private void RefreshShiftUi()
    {
        var isShift = UseAsShiftCheck.IsChecked == true;
        // The shift control itself doesn't get mappings of its own in the shifted layer (holding
        // it is what makes that layer active).
        LayerRow.IsEnabled = !isShift;
        if (isShift) NormalLayerRadio.IsChecked = true;
        ShiftHintText.Text =
            isShift ? "This is the shift control. It can still have normal mappings of its own."
            : _host.ShiftControlName() is { } name ? $"Shift control: {name}."
            : "No shift control set yet: open a button's remap window and tick \"Use this as the shift control\".";
        RefreshOutputs();
    }

    private void UseAsShiftCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.SetShiftControl(_slot, UseAsShiftCheck.IsChecked == true);
        RefreshShiftUi();
    }

    private void Layer_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (_editing is not null) EndEdit(); // the list being changed from is gone
        RefreshOutputs();
    }

    private void RefreshOutputs()
    {
        var outputs = _host.GetRemaps(_slot.DeviceGuid, _slot.Kind, _slot.Index, _slot.Part)
            .Where(o => o.Shifted == ShiftedLayer).ToList();
        OutputsList.ItemsSource = outputs;
        NoOutputsText.Visibility = outputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutputsGroup.Header = ShiftedLayer ? "While shift is held, does" : "Currently does";
    }

    // ---- The "Add" form ----

    private void OutputTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) RefreshAddRows();
    }

    private void NudgeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) RefreshAddRows();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshNudgeUnit();

    private void RefreshAddRows()
    {
        var type = SelectedType;
        var isKey = type == OutputType.Key;
        var isNudge = type == OutputType.Nudge;

        VJoyIdRow.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
        TargetRow.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
        KeyRow.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
        NudgeRow.Visibility = isNudge ? Visibility.Visible : Visibility.Collapsed;
        NudgeAmountRow.Visibility = isNudge && SelectedNudge != AxisNudge.Center ? Visibility.Visible : Visibility.Collapsed;
        TargetLabel.Text = type switch
        {
            OutputType.VJoyAxis or OutputType.Nudge => "Axis:",
            OutputType.VJoyHat => "Hat:",
            _ => "Button:",
        };

        // How an on/off input drives its output.
        List<Option<SwitchMode>> modes = type switch
        {
            OutputType.VJoyButton =>
            [
                new("Hold while pressed", SwitchMode.Hold),
                new("Short press when switched on", SwitchMode.PulseOnPress),
                new("Short press on every flip (on and off)", SwitchMode.PulseOnBoth),
                new("Latch (press for on, press again for off)", SwitchMode.Latch),
            ],
            // Axis halves always hold their key while past the point.
            OutputType.Key when _slot.Kind != ControlKind.Axis =>
            [
                new("Hold the key while pressed", SwitchMode.Hold),
                new("Tap the key when switched on", SwitchMode.PulseOnPress),
                new("Tap the key on every flip (on and off)", SwitchMode.PulseOnBoth),
            ],
            OutputType.Nudge when SelectedNudge != AxisNudge.Center =>
            [
                new("Keep moving while held", SwitchMode.Hold),
                new("One step per press", SwitchMode.PulseOnPress),
            ],
            _ => [],
        };
        ModeCombo.ItemsSource = modes;
        ModeCombo.SelectedIndex = modes.Count > 0 ? 0 : -1;
        ModeRow.Visibility = modes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshNudgeUnit();
        RefreshTargets();
    }

    private void RefreshNudgeUnit()
    {
        var perPress = (ModeCombo.SelectedItem as Option<SwitchMode>)?.Value is SwitchMode.PulseOnPress;
        NudgeAmountUnit.Text = perPress ? "% of full travel per press" : "% of full travel per second";
        if (_loading) return;
        NudgeAmountBox.Text = perPress ? "5" : "50";
    }

    private void VJoyIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) RefreshTargets();
    }

    private bool TryGetVJoyId(out uint id) => uint.TryParse(VJoyIdBox.Text.Trim(), out id) && id is >= 1 and <= 16;

    /// <summary>Lists the vJoy slots the chosen vJoy device actually has (all of them if vJoy
    /// isn't installed), marking ones other mappings already write to, and preselects the first
    /// free one.</summary>
    private void RefreshTargets()
    {
        VJoyLimitText.Text = "";
        if (SelectedType == OutputType.Key) return;
        var id = TryGetVJoyId(out var parsed) ? parsed : 1;
        var limits = _host.GetVJoyLimits(id);

        var options = new List<TargetOption>();
        switch (SelectedType)
        {
            case OutputType.VJoyButton:
                for (var i = 0; i < (limits.Available ? limits.Buttons : 128); i++) options.Add(Target($"Button {i + 1}", ControlKind.Button, i));
                break;
            case OutputType.VJoyAxis or OutputType.Nudge:
                foreach (var axis in Enum.GetValues<VJoyAxis>())
                {
                    if (!limits.Available || limits.HasAxis(axis)) options.Add(Target(axis.ToString(), ControlKind.Axis, (int)axis));
                }
                break;
            case OutputType.VJoyHat:
                for (var i = 0; i < (limits.Available ? limits.Hats : 4); i++) options.Add(Target($"Hat {i + 1}", ControlKind.Pov, i));
                break;
        }

        TargetCombo.ItemsSource = options;
        TargetCombo.SelectedItem = options.FirstOrDefault(o => !o.InUse) ?? options.FirstOrDefault();

        if (!limits.Available)
        {
            VJoyLimitText.Text = "vJoy isn't available, so every slot is listed - check how vJoy device " + id + " is set up in Configure vJoy.";
        }
        else if (options.Count == 0)
        {
            var what = SelectedType switch { OutputType.VJoyButton => "buttons", OutputType.VJoyHat => "hats", _ => "axes" };
            VJoyLimitText.Text = $"vJoy device {id} has no {what}. Add some in Configure vJoy (Start menu), then reopen this window.";
        }

        TargetOption Target(string name, ControlKind kind, int index)
        {
            var inUse = _host.IsVJoyTargetUsed(id, kind, index);
            return new TargetOption(inUse ? $"{name}  (already used)" : name, index, inUse);
        }
    }

    // ---- Changing an existing output ----

    private void EditOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RemapOutput output }) BeginEdit(output.Source);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => EndEdit();

    /// <summary>Fills the form from an existing output; "Save" then replaces it.</summary>
    private void BeginEdit(object output)
    {
        var mode = SwitchMode.Hold;
        double threshold;
        switch (output)
        {
            case Binding binding:
            {
                var type = binding.Nudge != AxisNudge.None ? OutputType.Nudge
                    : binding.Target.Kind switch { ControlKind.Axis => OutputType.VJoyAxis, ControlKind.Pov => OutputType.VJoyHat, _ => OutputType.VJoyButton };
                if (!SelectOption(OutputTypeCombo, type)) return;
                if (binding.Nudge != AxisNudge.None) SelectOption(NudgeCombo, binding.Nudge);
                VJoyIdBox.Text = binding.Target.VJoyDeviceId.ToString();
                RefreshTargets();
                TargetCombo.SelectedItem = (TargetCombo.ItemsSource as IEnumerable<TargetOption>)?.FirstOrDefault(t => t.Index == binding.Target.Index) ?? TargetCombo.SelectedItem;
                mode = binding.Mode;
                SelectOption(ModeCombo, mode);
                NudgeAmountBox.Text = (binding.NudgeAmount * 100).ToString("0.#");
                threshold = binding.AxisThreshold;
                break;
            }
            case KeyBinding keyBinding:
                if (!SelectOption(OutputTypeCombo, OutputType.Key)) return;
                KeyCombo.SelectedItem = KeyCatalog.Keys.FirstOrDefault(k => k.VKey == keyBinding.VirtualKeyCode) ?? KeyCombo.SelectedItem;
                mode = keyBinding.HoldWhilePressed ? SwitchMode.Hold : keyBinding.TapOnRelease ? SwitchMode.PulseOnBoth : SwitchMode.PulseOnPress;
                SelectOption(ModeCombo, mode);
                threshold = keyBinding.AxisThreshold;
                break;
            default:
                return;
        }
        ThresholdBox.Text = (threshold * 100).ToString("0.#");

        _editing = output;
        AddGroup.Header = "Change";
        AddButton.Content = "Save";
        CancelEditButton.Visibility = Visibility.Visible;
    }

    private void EndEdit()
    {
        _editing = null;
        AddGroup.Header = "Add";
        AddButton.Content = "Add";
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private static bool SelectOption<T>(ComboBox combo, T value)
    {
        var match = (combo.ItemsSource as IEnumerable<Option<T>>)?.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value));
        if (match is null) return false;
        combo.SelectedItem = match;
        return true;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (BuildOutput() is not { } output) return;

        if (_editing is not null) _host.ReplaceRemap(_editing, output);
        else _host.AddRemap(output);
        EndEdit();

        RefreshOutputs();
        RefreshTargets();
    }

    /// <summary>The Binding/KeyBinding the form describes, or null (after saying why) if it's incomplete.</summary>
    private object? BuildOutput()
    {
        var source = new InputRef(_slot.DeviceGuid, _slot.Kind, _slot.Index);
        var mode = (ModeCombo.SelectedItem as Option<SwitchMode>)?.Value ?? SwitchMode.Hold;

        var threshold = 0.5;
        if (_slot.Part is InputPart.Positive or InputPart.Negative)
        {
            if (!TryReadPercent(ThresholdBox.Text, 5, 95, out threshold))
            {
                Warn("Enter how far the axis has to move, as a percentage from 5 to 95.");
                return null;
            }
        }

        if (SelectedType == OutputType.Key)
        {
            if (KeyCombo.SelectedItem is not KeyOption key) return null;
            return new KeyBinding
            {
                Source = source,
                VirtualKeyCode = key.VKey,
                HoldWhilePressed = mode == SwitchMode.Hold,
                TapOnRelease = mode == SwitchMode.PulseOnBoth,
                PovDirection = InputParts.IsDirection(_slot.Part) ? InputParts.ToPovDirection(_slot.Part) : 0,
                AxisThreshold = threshold,
                AxisAboveThreshold = _slot.Part != InputPart.Negative,
                Shifted = ShiftedLayer,
            };
        }
        else
        {
            if (!TryGetVJoyId(out var id))
            {
                Warn("The vJoy device number has to be from 1 to 16.");
                return null;
            }
            if (TargetCombo.SelectedItem is not TargetOption target)
            {
                Warn("There's nothing to pick on that vJoy device - see the note under it.");
                return null;
            }

            var nudge = SelectedType == OutputType.Nudge ? SelectedNudge : AxisNudge.None;
            var nudgeAmount = 0.5;
            if (nudge is AxisNudge.Increase or AxisNudge.Decrease && !TryReadPercent(NudgeAmountBox.Text, 1, 100, out nudgeAmount))
            {
                Warn("Enter how far to move the axis, as a percentage from 1 to 100.");
                return null;
            }

            var targetKind = SelectedType switch
            {
                OutputType.VJoyAxis or OutputType.Nudge => ControlKind.Axis,
                OutputType.VJoyHat => ControlKind.Pov,
                _ => ControlKind.Button,
            };
            return new Binding
            {
                Source = source,
                Target = new OutputRef(id, targetKind, target.Index),
                Part = _slot.Part,
                AxisThreshold = threshold,
                Mode = mode,
                Shifted = ShiftedLayer,
                Nudge = nudge,
                NudgeAmount = nudgeAmount,
            };
        }
    }

    private static bool TryReadPercent(string text, double min, double max, out double fraction)
    {
        fraction = 0;
        if (!double.TryParse(text.Trim().TrimEnd('%'), out var percent) || percent < min || percent > max) return false;
        fraction = percent / 100.0;
        return true;
    }

    private void Warn(string message) => MessageBox.Show(this, message, "Remap", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void RemoveOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RemapOutput output }) return;
        if (ReferenceEquals(output.Source, _editing)) EndEdit();
        _host.RemoveRemap(output.Source);
        RefreshOutputs();
        RefreshTargets();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
