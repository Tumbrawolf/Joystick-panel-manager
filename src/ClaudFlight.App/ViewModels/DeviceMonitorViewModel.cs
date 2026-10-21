using System.Collections.ObjectModel;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

public sealed class DeviceMonitorViewModel(Guid deviceGuid, string deviceName) : ObservableBase
{
    public Guid DeviceGuid { get; } = deviceGuid;
    public string DeviceName { get; } = deviceName;

    private string _label = deviceName;
    /// <summary>User-editable friendly label (e.g. "Left Stick") since identical hardware shares a name.</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    public IReadOnlyList<AxisType> PresentAxes { get; init; } = [];

    public ObservableCollection<AxisReadingViewModel> Axes { get; } = [];
    public ObservableCollection<ButtonReadingViewModel> Buttons { get; } = [];
    public ObservableCollection<PovReadingViewModel> Povs { get; } = [];

    /// <summary>Raw Input mouse-style devices only (RawInputMouseDevice/RawInputCombinedDevice):
    /// one row per native axis (X/Y movement, wheel) letting the user choose how it's exposed -
    /// see AxisModeRowViewModel. Empty for every other device type.</summary>
    public ObservableCollection<AxisModeRowViewModel> AxisModeRows { get; } = [];

    /// <summary>Axes/buttons/POVs hidden from view on this device - listed so they can be restored.</summary>
    public ObservableCollection<HiddenControlViewModel> HiddenControls { get; } = [];

    private HiddenControlViewModel? _selectedHiddenControl;
    public HiddenControlViewModel? SelectedHiddenControl
    {
        get => _selectedHiddenControl;
        set => SetField(ref _selectedHiddenControl, value);
    }

    public void ApplySnapshot(JoystickSnapshot snapshot)
    {
        foreach (var axisVm in Axes)
        {
            if (snapshot.Axes.TryGetValue(axisVm.Axis, out var raw)) axisVm.Raw = raw;
        }

        // Match by each row's own device-level index rather than its position in this (possibly
        // shorter) visible list - a hidden button/POV earlier in the device's control list would
        // otherwise shift every later one's reading off by however many are hidden before it.
        foreach (var buttonVm in Buttons)
        {
            if (buttonVm.Index < snapshot.Buttons.Count) buttonVm.IsPressed = snapshot.Buttons[buttonVm.Index];
        }

        foreach (var povVm in Povs)
        {
            if (povVm.Index < snapshot.PointOfViews.Count) povVm.RawHundredthDegrees = snapshot.PointOfViews[povVm.Index];
        }
    }

    /// <summary>Finds the shared live-monitor control instance for a (Kind, Index) pair, so other
    /// tabs (Calibration, Mapping) can bind straight to its Label instead of holding a copy.</summary>
    public INamedControl? FindControl(ControlKind kind, int index) => kind switch
    {
        ControlKind.Axis => Axes.FirstOrDefault(a => (int)a.Axis == index),
        ControlKind.Button => Buttons.FirstOrDefault(b => b.Index == index),
        ControlKind.Pov => Povs.FirstOrDefault(p => p.Index == index),
        _ => null,
    };
}

/// <summary>A detected device the user has hidden from view - listed so it can be brought back.</summary>
public sealed record HiddenDeviceViewModel(Guid DeviceGuid, string Name);

/// <summary>An axis/button/POV hidden from view on an otherwise-visible device.</summary>
public sealed record HiddenControlViewModel(ControlKind Kind, int Index, string Label)
{
    public override string ToString() => Label;
}

public sealed class AxisReadingViewModel(Guid deviceGuid, AxisType axis) : ObservableBase, INamedControl
{
    public Guid DeviceGuid { get; } = deviceGuid;
    public AxisType Axis { get; } = axis;
    public ControlKind Kind => ControlKind.Axis;
    public int ControlIndex => (int)Axis;

    private string _label = axis.ToString();
    /// <summary>User-editable friendly name (e.g. "Rudder" for RotationZ).</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    private int _raw;
    public int Raw
    {
        get => _raw;
        set
        {
            if (SetField(ref _raw, value)) Raise(nameof(Normalized));
        }
    }

    /// <summary>Raw mapped from [-1000,1000] to [0,1] purely for the live monitor progress bar.</summary>
    public double Normalized => (Raw - PhysicalAxisRange.Min) / (double)(PhysicalAxisRange.Max - PhysicalAxisRange.Min);
}

public sealed class ButtonReadingViewModel(Guid deviceGuid, int index) : ObservableBase, INamedControl
{
    public Guid DeviceGuid { get; } = deviceGuid;
    public int Index { get; } = index;
    public ControlKind Kind => ControlKind.Button;
    public int ControlIndex => Index;

    /// <summary>1-based number shown on the lamp - matches what Mapping's "Source #"/"Target #"
    /// fields expect, so the number you see is the number you type (DirectInput/vJoy indices are
    /// 0-based internally, but showing that would just invite off-by-one mistakes).</summary>
    public int DisplayNumber => Index + 1;

    private string _label = $"{index + 1}";
    /// <summary>User-editable friendly name (e.g. "Master Battery" for button 3).</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    private bool _isPressed;
    public bool IsPressed
    {
        get => _isPressed;
        set => SetField(ref _isPressed, value);
    }
}

/// <summary>One selectable way to expose a Raw Input mouse-style device's native axis: a
/// continuous axis under a given AxisType slot, or two buttons (ReportAs is unused for that case -
/// see AxisReportMode).</summary>
public sealed record AxisModeOption(string Label, AxisReportMode Mode, AxisType ReportAs)
{
    public override string ToString() => Label;
}

/// <summary>One native axis (X/Y movement or wheel) on a Raw Input mouse-style device, with a
/// dropdown of ways to expose it (see AxisModeOption) - e.g. relabeling a dial's wheel delta to a
/// different axis slot, or converting it to two buttons better suited to step actions than a
/// continuous scroll-style axis.</summary>
public sealed class AxisModeRowViewModel(Guid deviceGuid, AxisType nativeAxis, string nativeLabel) : ObservableBase
{
    public Guid DeviceGuid { get; } = deviceGuid;
    public AxisType NativeAxis { get; } = nativeAxis;
    public string NativeLabel { get; } = nativeLabel;

    public static IReadOnlyList<AxisModeOption> Options { get; } =
    [
        new("Axis: X", AxisReportMode.Axis, AxisType.X),
        new("Axis: Y", AxisReportMode.Axis, AxisType.Y),
        new("Axis: Z", AxisReportMode.Axis, AxisType.Z),
        new("Axis: Rotation X", AxisReportMode.Axis, AxisType.RotationX),
        new("Axis: Rotation Y", AxisReportMode.Axis, AxisType.RotationY),
        new("Axis: Rotation Z", AxisReportMode.Axis, AxisType.RotationZ),
        new("Axis: Slider 1", AxisReportMode.Axis, AxisType.Slider0),
        new("Axis: Slider 2", AxisReportMode.Axis, AxisType.Slider1),
        new("Two buttons (+/-)", AxisReportMode.Buttons, AxisType.X),
    ];

    private AxisModeOption _selectedOption = Options[0];
    public AxisModeOption SelectedOption
    {
        get => _selectedOption;
        set => SetField(ref _selectedOption, value);
    }
}

public sealed class PovReadingViewModel(Guid deviceGuid, int index) : ObservableBase, INamedControl
{
    public Guid DeviceGuid { get; } = deviceGuid;
    public int Index { get; } = index;
    public ControlKind Kind => ControlKind.Pov;
    public int ControlIndex => Index;

    /// <summary>1-based number - matches what Mapping's "Source #"/"Target #" fields expect.</summary>
    public int DisplayNumber => Index + 1;

    private string _label = $"POV {index + 1}";
    /// <summary>User-editable friendly name for this hat switch.</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    private int _rawHundredthDegrees = -1;
    public int RawHundredthDegrees
    {
        get => _rawHundredthDegrees;
        set
        {
            if (SetField(ref _rawHundredthDegrees, value)) Raise(nameof(DirectionText));
        }
    }

    public string DirectionText => RawHundredthDegrees < 0 ? "center" : $"{RawHundredthDegrees / 100.0:0}°";
}
