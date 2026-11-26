namespace ClaudFlight.Core.Models;

/// <summary>One page of the Layout tab: a background picture (e.g. a manufacturer's controls
/// diagram or a hand-drawn panel schematic) with markers for physical controls placed on top of
/// it, so the user can see at a glance which control is which and what it's mapped to.</summary>
public sealed class LayoutPage
{
    public string Name { get; set; } = "Layout";

    /// <summary>The picture file's original bytes (PNG, JPEG...), or null for a blank page.
    /// Stored inside the profile so a shared/exported profile carries it along.</summary>
    public byte[]? Background { get; set; }

    public List<LayoutMarker> Markers { get; set; } = [];

    /// <summary>Free text labels placed on the page (titles, notes, names for parts of the picture).</summary>
    public List<LayoutText> Texts { get; set; } = [];
}

/// <summary>A free text label on a layout page, not tied to any control.</summary>
public sealed class LayoutText
{
    public string Text { get; set; } = "Text";

    /// <summary>Center, as a fraction (0..1) of the background picture's size - see LayoutMarker.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double FontSize { get; set; } = 16;
}

/// <summary>Where one physical control's marker sits on a layout page.</summary>
public sealed class LayoutMarker
{
    public required Guid DeviceGuid { get; init; }
    public required ControlKind Kind { get; init; }
    public required int Index { get; init; }

    /// <summary>Which part of the control this marker stands for - the whole control, or one
    /// hat direction / axis half, so each can sit on its own spot of the picture.</summary>
    public InputPart Part { get; init; } = InputPart.Whole;

    /// <summary>Center of the marker as a fraction (0..1) of the background picture's width and
    /// height, so markers stay on the same spot of the picture however big the window is.</summary>
    public double X { get; set; }

    public double Y { get; set; }
}
