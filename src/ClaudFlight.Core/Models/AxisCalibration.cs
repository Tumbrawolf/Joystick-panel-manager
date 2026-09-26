namespace ClaudFlight.Core.Models;

/// <summary>Per-axis calibration: raw DirectInput range -&gt; normalized -1..1 -&gt; shaped output.</summary>
public sealed class AxisCalibration
{
    public int RawMin { get; set; } = PhysicalAxisRange.Min;
    public int RawMax { get; set; } = PhysicalAxisRange.Max;
    public int RawCenter { get; set; } = 0;

    /// <summary>Fraction (0..1) of the range around center that is ignored.</summary>
    public double DeadZone { get; set; } = 0.02;

    /// <summary>Response curve exponent. 1 = linear, &gt;1 = more precision near center (expo).</summary>
    public double Curve { get; set; } = 1.0;

    public bool Invert { get; set; }

    /// <summary>Maps a raw DirectInput value through calibration to a normalized value in [-1, 1].</summary>
    public double Normalize(int raw)
    {
        double normalized;
        if (raw >= RawCenter)
        {
            var span = Math.Max(1, RawMax - RawCenter);
            normalized = (raw - RawCenter) / (double)span;
        }
        else
        {
            var span = Math.Max(1, RawCenter - RawMin);
            normalized = (raw - RawCenter) / (double)span;
        }
        normalized = Math.Clamp(normalized, -1.0, 1.0);

        var magnitude = Math.Abs(normalized);
        if (magnitude < DeadZone)
        {
            normalized = 0.0;
        }
        else
        {
            var sign = Math.Sign(normalized);
            var rescaled = (magnitude - DeadZone) / (1.0 - DeadZone);
            normalized = sign * Math.Pow(rescaled, Curve);
        }

        if (Invert) normalized = -normalized;
        return normalized;
    }
}

public static class PhysicalAxisRange
{
    public const int Min = -1000;
    public const int Max = 1000;
}
