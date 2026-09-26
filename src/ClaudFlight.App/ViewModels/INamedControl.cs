using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

/// <summary>Implemented by every per-control live-monitor view model (axis/button/POV) so other
/// tabs can bind directly to the same instance's Label - a rename anywhere updates everywhere,
/// with no separate sync step needed - and so hide/restore handlers can work generically across
/// all three control kinds without a type switch.</summary>
public interface INamedControl
{
    string Label { get; set; }
    Guid DeviceGuid { get; }
    ControlKind Kind { get; }
    int ControlIndex { get; }
}
