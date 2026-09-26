using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

/// <summary>Editable row backing one KeyBinding (physical control -&gt; simulated key press).
/// SourceIndexText is free text for the same reason as BindingRowViewModel's: its valid values
/// depend on Kind (an axis name, or a 1-based button/POV number).</summary>
public sealed class KeyBindingRowViewModel : ObservableBase
{
    private Guid _sourceDeviceGuid;
    public Guid SourceDeviceGuid
    {
        get => _sourceDeviceGuid;
        set => SetField(ref _sourceDeviceGuid, value);
    }

    /// <summary>Live reference to the source device's view model, kept in sync with renames.</summary>
    private DeviceMonitorViewModel? _sourceDevice;
    public DeviceMonitorViewModel? SourceDevice
    {
        get => _sourceDevice;
        set => SetField(ref _sourceDevice, value);
    }

    /// <summary>Live reference to the source control's view model, for display binding
    /// ("SourceControl.Label") kept in sync with Live Monitor/Calibration/Mapping renames.</summary>
    private INamedControl? _sourceControl;
    public INamedControl? SourceControl
    {
        get => _sourceControl;
        set => SetField(ref _sourceControl, value);
    }

    private ControlKind _sourceKind = ControlKind.Button;
    public ControlKind SourceKind
    {
        get => _sourceKind;
        set => SetField(ref _sourceKind, value);
    }

    private string _sourceIndexText = "1";
    public string SourceIndexText
    {
        get => _sourceIndexText;
        set => SetField(ref _sourceIndexText, value);
    }

    private string _keyName = "F13";
    public string KeyName
    {
        get => _keyName;
        set => SetField(ref _keyName, value);
    }

    /// <summary>Button/Pov only: hold the key while the control is held, vs. a single tap per press.</summary>
    private bool _holdWhilePressed = true;
    public bool HoldWhilePressed
    {
        get => _holdWhilePressed;
        set => SetField(ref _holdWhilePressed, value);
    }

    /// <summary>Pov only: 0=N, 1=E, 2=S, 3=W.</summary>
    private int _povDirection;
    public int PovDirection
    {
        get => _povDirection;
        set => SetField(ref _povDirection, value);
    }

    /// <summary>Axis only: normalized magnitude (0..1) that must be crossed to trigger the key.</summary>
    private double _axisThreshold = 0.5;
    public double AxisThreshold
    {
        get => _axisThreshold;
        set => SetField(ref _axisThreshold, value);
    }

    /// <summary>Axis only: true = trigger above +threshold, false = trigger below -threshold.</summary>
    private bool _axisAboveThreshold = true;
    public bool AxisAboveThreshold
    {
        get => _axisAboveThreshold;
        set => SetField(ref _axisAboveThreshold, value);
    }

    public static KeyBindingRowViewModel FromKeyBinding(KeyBinding binding, DeviceMonitorViewModel? sourceDevice)
    {
        var keyName = KeyCatalog.Keys.FirstOrDefault(k => k.VKey == binding.VirtualKeyCode)?.Name ?? "F13";
        return new KeyBindingRowViewModel
        {
            SourceDeviceGuid = binding.Source.DeviceGuid,
            SourceDevice = sourceDevice,
            SourceControl = sourceDevice?.FindControl(binding.Source.Kind, binding.Source.Index),
            SourceKind = binding.Source.Kind,
            SourceIndexText = FormatSourceIndex(binding.Source),
            KeyName = keyName,
            HoldWhilePressed = binding.HoldWhilePressed,
            PovDirection = binding.PovDirection,
            AxisThreshold = binding.AxisThreshold,
            AxisAboveThreshold = binding.AxisAboveThreshold,
        };
    }

    private static string FormatSourceIndex(InputRef source) =>
        source.Kind == ControlKind.Axis ? ((AxisType)source.Index).ToString() : (source.Index + 1).ToString();

    /// <summary>Parses this row into a KeyBinding, or returns null with an error message.</summary>
    public KeyBinding? TryBuildKeyBinding(out string? error)
    {
        error = null;

        if (!TryParseSourceIndex(out var sourceIndex))
        {
            error = SourceKind == ControlKind.Axis
                ? $"Source index '{SourceIndexText}' is not a valid axis name (X, Y, Z, RotationX, RotationY, RotationZ, Slider0, Slider1)."
                : $"Source index '{SourceIndexText}' is not a valid 1-based number.";
            return null;
        }

        var key = KeyCatalog.Keys.FirstOrDefault(k => k.Name == KeyName);
        if (key is null)
        {
            error = $"'{KeyName}' is not a recognized key.";
            return null;
        }

        return new KeyBinding
        {
            Source = new InputRef(SourceDeviceGuid, SourceKind, sourceIndex),
            VirtualKeyCode = key.VKey,
            HoldWhilePressed = HoldWhilePressed,
            PovDirection = PovDirection,
            AxisThreshold = AxisThreshold,
            AxisAboveThreshold = AxisAboveThreshold,
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
}
