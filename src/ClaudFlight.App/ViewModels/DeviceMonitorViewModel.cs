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

        for (var i = 0; i < Buttons.Count && i < snapshot.Buttons.Count; i++)
        {
            Buttons[i].IsPressed = snapshot.Buttons[i];
        }

        for (var i = 0; i < Povs.Count && i < snapshot.PointOfViews.Count; i++)
        {
            Povs[i].RawHundredthDegrees = snapshot.PointOfViews[i];
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
