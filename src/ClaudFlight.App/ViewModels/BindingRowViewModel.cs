using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

/// <summary>Editable row backing one Binding. SourceIndexText/TargetIndexText are free text
/// because their valid values depend on Kind (an axis name like "X", or a 1-based number for
/// buttons/POVs) - a fully dropdown-driven grid would need per-row templates for little benefit here.</summary>
public sealed class BindingRowViewModel : ObservableBase
{
    private Guid _sourceDeviceGuid;
    public Guid SourceDeviceGuid
    {
        get => _sourceDeviceGuid;
        set => SetField(ref _sourceDeviceGuid, value);
    }

    /// <summary>Live reference to the source device's view model, for display binding
    /// ("SourceDevice.Label") that stays in sync with renames made on any tab. Null if the
    /// device isn't currently connected (e.g. right after loading a profile).</summary>
    private DeviceMonitorViewModel? _sourceDevice;
    public DeviceMonitorViewModel? SourceDevice
    {
        get => _sourceDevice;
        set => SetField(ref _sourceDevice, value);
    }

    /// <summary>Live reference to the source control's view model, for display binding
    /// ("SourceControl.Label"). Also settable from here - renaming in the Mapping grid
    /// updates the same instance Live Monitor and Calibration show.</summary>
    private INamedControl? _sourceControl;
    public INamedControl? SourceControl
    {
        get => _sourceControl;
        set => SetField(ref _sourceControl, value);
    }

    private ControlKind _sourceKind = ControlKind.Axis;
    public ControlKind SourceKind
    {
        get => _sourceKind;
        set => SetField(ref _sourceKind, value);
    }

    private string _sourceIndexText = "X";
    public string SourceIndexText
    {
        get => _sourceIndexText;
        set => SetField(ref _sourceIndexText, value);
    }

    private uint _targetVJoyDeviceId = 1;
    public uint TargetVJoyDeviceId
    {
        get => _targetVJoyDeviceId;
        set => SetField(ref _targetVJoyDeviceId, value);
    }

    private string _targetIndexText = "X";
    public string TargetIndexText
    {
        get => _targetIndexText;
        set => SetField(ref _targetIndexText, value);
    }

    public static BindingRowViewModel FromBinding(Binding binding, DeviceMonitorViewModel? sourceDevice)
    {
        return new BindingRowViewModel
        {
            SourceDeviceGuid = binding.Source.DeviceGuid,
            SourceDevice = sourceDevice,
            SourceControl = sourceDevice?.FindControl(binding.Source.Kind, binding.Source.Index),
            SourceKind = binding.Source.Kind,
            SourceIndexText = FormatSourceIndex(binding.Source),
            TargetVJoyDeviceId = binding.Target.VJoyDeviceId,
            TargetIndexText = FormatTargetIndex(binding.Target),
        };
    }

    private static string FormatSourceIndex(InputRef source) =>
        source.Kind == ControlKind.Axis ? ((AxisType)source.Index).ToString() : (source.Index + 1).ToString();

    private static string FormatTargetIndex(OutputRef target) =>
        target.Kind == ControlKind.Axis ? ((VJoyAxis)target.Index).ToString() : (target.Index + 1).ToString();

    /// <summary>Parses this row into a Binding, or returns null with an error message if the
    /// index text doesn't match the row's Kind (e.g. "X" for a Button row).</summary>
    public Binding? TryBuildBinding(out string? error)
    {
        error = null;

        if (!TryParseSourceIndex(out var sourceIndex))
        {
            error = SourceKind == ControlKind.Axis
                ? $"Source index '{SourceIndexText}' is not a valid axis name (X, Y, Z, RotationX, RotationY, RotationZ, Slider0, Slider1)."
                : $"Source index '{SourceIndexText}' is not a valid 1-based number.";
            return null;
        }

        if (!TryParseTargetIndex(out var targetIndex))
        {
            error = SourceKind == ControlKind.Axis
                ? $"Target index '{TargetIndexText}' is not a valid vJoy axis name (X, Y, Z, RotationX, RotationY, RotationZ, Slider0, Slider1)."
                : $"Target index '{TargetIndexText}' is not a valid 1-based number from 1 to {(SourceKind == ControlKind.Button ? 128 : 4)}.";
            return null;
        }

        return new Binding
        {
            Source = new InputRef(SourceDeviceGuid, SourceKind, sourceIndex),
            Target = new OutputRef(TargetVJoyDeviceId, SourceKind, targetIndex),
        };
    }

    private bool TryParseSourceIndex(out int index)
    {
        if (SourceKind == ControlKind.Axis)
        {
            if (Enum.TryParse<AxisType>(SourceIndexText.Trim(), ignoreCase: true, out var axis))
            {
                index = (int)axis;
                return true;
            }
            index = 0;
            return false;
        }

        if (int.TryParse(SourceIndexText.Trim(), out var oneBased) && oneBased >= 1)
        {
            index = oneBased - 1;
            return true;
        }
        index = 0;
        return false;
    }

    private bool TryParseTargetIndex(out int index)
    {
        if (SourceKind == ControlKind.Axis)
        {
            if (Enum.TryParse<VJoyAxis>(TargetIndexText.Trim(), ignoreCase: true, out var axis))
            {
                index = (int)axis;
                return true;
            }
            index = 0;
            return false;
        }

        // Upper-bounded (vJoy's real max is 128 buttons / 4 POVs) so a stray typo in this
        // directly-editable grid cell fails loudly here rather than silently wrapping around
        // when VJoyManager later narrows it to a byte for the native SetButton/SetDiscPov call.
        var max = SourceKind == ControlKind.Button ? 128 : 4;
        if (int.TryParse(TargetIndexText.Trim(), out var oneBased) && oneBased >= 1 && oneBased <= max)
        {
            index = oneBased - 1;
            return true;
        }
        index = 0;
        return false;
    }
}
