using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

/// <summary>Editable calibration for one physical device's axis. Holds direct references to the
/// same Device/AxisVm instances the Live Monitor tab uses (rather than copies), so a rename on
/// either tab shows up on both immediately, and live Raw/Normalized values need no separate sync
/// loop - they're read straight off the instance Live Monitor is already updating every tick.
/// Calibration edits are pushed straight into the running MappingEngine so the user sees the
/// effect live while dragging.</summary>
public sealed class CalibrationRowViewModel : ObservableBase
{
    private readonly MappingEngine _engine;

    public DeviceMonitorViewModel Device { get; }
    public AxisReadingViewModel AxisVm { get; }

    public Guid DeviceGuid => Device.DeviceGuid;
    public AxisType Axis => AxisVm.Axis;

    public int RawLive => AxisVm.Raw;
    public double NormalizedLive => Calibration.Normalize(AxisVm.Raw);

    public AxisCalibration Calibration { get; private set; }

    public int RawMin
    {
        get => Calibration.RawMin;
        set => Update(c => c.RawMin = value);
    }

    public int RawCenter
    {
        get => Calibration.RawCenter;
        set => Update(c => c.RawCenter = value);
    }

    public int RawMax
    {
        get => Calibration.RawMax;
        set => Update(c => c.RawMax = value);
    }

    public double DeadZone
    {
        get => Calibration.DeadZone;
        set => Update(c => c.DeadZone = value);
    }

    public double Curve
    {
        get => Calibration.Curve;
        set => Update(c => c.Curve = value);
    }

    public bool Invert
    {
        get => Calibration.Invert;
        set => Update(c => c.Invert = value);
    }

    public CalibrationRowViewModel(MappingEngine engine, DeviceMonitorViewModel device, AxisReadingViewModel axisVm)
    {
        _engine = engine;
        Device = device;
        AxisVm = axisVm;
        Calibration = engine.GetCalibration(device.DeviceGuid, axisVm.Axis);

        AxisVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AxisReadingViewModel.Raw))
            {
                Raise(nameof(RawLive));
                Raise(nameof(NormalizedLive));
            }
        };
    }

    public void SetMinFromLive() => Update(c => c.RawMin = RawLive);
    public void SetCenterFromLive() => Update(c => c.RawCenter = RawLive);
    public void SetMaxFromLive() => Update(c => c.RawMax = RawLive);
    public void SetCenterFromMinMax() => Update(c => c.RawCenter = (Calibration.RawMin + Calibration.RawMax) / 2);

    private void Update(Action<AxisCalibration> mutate)
    {
        var updated = new AxisCalibration
        {
            RawMin = Calibration.RawMin,
            RawMax = Calibration.RawMax,
            RawCenter = Calibration.RawCenter,
            DeadZone = Calibration.DeadZone,
            Curve = Calibration.Curve,
            Invert = Calibration.Invert,
        };
        mutate(updated);
        Calibration = updated;
        _engine.SetCalibration(DeviceGuid, Axis, updated);

        Raise(nameof(RawMin));
        Raise(nameof(RawCenter));
        Raise(nameof(RawMax));
        Raise(nameof(DeadZone));
        Raise(nameof(Curve));
        Raise(nameof(Invert));
        Raise(nameof(NormalizedLive));
    }
}
