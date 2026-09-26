namespace ClaudFlight.Core.Models;

/// <summary>Immutable snapshot of a physical device's raw DirectInput state for one poll cycle.</summary>
public sealed class JoystickSnapshot
{
    public required IReadOnlyDictionary<AxisType, int> Axes { get; init; }
    public required IReadOnlyList<bool> Buttons { get; init; }
    public required IReadOnlyList<int> PointOfViews { get; init; }

    public static readonly JoystickSnapshot Empty = new()
    {
        Axes = new Dictionary<AxisType, int>(),
        Buttons = Array.Empty<bool>(),
        PointOfViews = Array.Empty<int>(),
    };
}
