using System.Collections.ObjectModel;
using System.ComponentModel;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

/// <summary>One page in the Layout tab's page picker, editing its LayoutPage in place.</summary>
public sealed class LayoutPageViewModel(LayoutPage page) : ObservableBase
{
    public LayoutPage Page { get; } = page;

    public string Name
    {
        get => Page.Name;
        set
        {
            if (Page.Name == value) return;
            Page.Name = value;
            Raise();
        }
    }

    public override string ToString() => Name;
}

/// <summary>Anything placed on a layout page (a control marker or a text label): its spot on the
/// picture (X/Y as fractions of the picture's size) and where that lands on screen.</summary>
public abstract class LayoutItemViewModel : ObservableBase
{
    public abstract double X { get; set; }
    public abstract double Y { get; set; }

    // The anchor is the item's center in canvas pixels; Left/Top shift it by half the item's own
    // size so the item is centered on its spot of the picture.
    private double _anchorX, _anchorY, _width, _height;

    public void SetAnchor(double x, double y)
    {
        _anchorX = x;
        _anchorY = y;
        Raise(nameof(Left));
        Raise(nameof(Top));
    }

    public void SetSize(double width, double height)
    {
        _width = width;
        _height = height;
        Raise(nameof(Left));
        Raise(nameof(Top));
    }

    public double Left => _anchorX - _width / 2;
    public double Top => _anchorY - _height / 2;

    private bool _isEditing;
    /// <summary>True while the page's "Edit layout" is ticked (text labels become editable then).</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }
}

/// <summary>A marker for one control - or one part of it (a hat direction, an axis half) - on the
/// layout page. Mirrors the Live Monitor's own reading of that control, so it lights up as the
/// control is used; a marker for a device that isn't connected (or is hidden) just shows its saved
/// name, dimmed.</summary>
public sealed class LayoutMarkerViewModel : LayoutItemViewModel
{
    /// <summary>What a screen reader says for this item in a list.</summary>
    public override string ToString() => $"{Label} ({DeviceLabel})";

    private readonly string _savedLabel;

    public LayoutMarkerViewModel(LayoutMarker marker, INamedControl? reading, string savedLabel, string deviceLabel, string mappingText)
    {
        Marker = marker;
        Reading = reading;
        _savedLabel = savedLabel;
        DeviceLabel = deviceLabel;
        MappingText = mappingText;

        if (reading is INotifyPropertyChanged notifying)
        {
            notifying.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(INamedControl.Label):
                        Raise(nameof(Label));
                        Raise(nameof(ToolTipText));
                        break;
                    case nameof(ButtonReadingViewModel.IsPressed):
                    case nameof(PovReadingViewModel.RawHundredthDegrees):
                        Raise(nameof(IsActive));
                        break;
                    case nameof(AxisReadingViewModel.Raw):
                        Raise(nameof(AxisValue));
                        if (Marker.Part != InputPart.Whole) Raise(nameof(IsActive));
                        break;
                }
            };
        }
    }

    public LayoutMarker Marker { get; }
    public INamedControl? Reading { get; }
    public string DeviceLabel { get; }
    public string MappingText { get; }
    public bool HasMapping => MappingText.Length > 0;

    public override double X { get => Marker.X; set => Marker.X = value; }
    public override double Y { get => Marker.Y; set => Marker.Y = value; }

    public string Label => (Reading?.Label ?? _savedLabel) + InputParts.Suffix(Marker.Part);
    public bool IsConnected => Reading is not null;

    /// <summary>Show the live position bar: only for a whole axis (its halves light up instead).</summary>
    public bool IsAxis => Marker.Kind == ControlKind.Axis && Marker.Part == InputPart.Whole;

    /// <summary>Button held down, hat pushed (toward this marker's direction, if it has one), or
    /// axis pushed well into this marker's half.</summary>
    public bool IsActive => Reading switch
    {
        ButtonReadingViewModel button => button.IsPressed,
        PovReadingViewModel pov => Marker.Part == InputPart.Whole
            ? pov.RawHundredthDegrees >= 0
            : InputParts.IsPovDirectionActive(pov.RawHundredthDegrees, Marker.Part),
        AxisReadingViewModel axis => Marker.Part switch
        {
            InputPart.Positive => axis.Normalized >= 0.75,
            InputPart.Negative => axis.Normalized <= 0.25,
            _ => false,
        },
        _ => false,
    };

    public double AxisValue => Reading is AxisReadingViewModel axis ? axis.Normalized : 0.5;

    public string ToolTipText
    {
        get
        {
            var text = $"{Label}\n{DeviceLabel}";
            text += HasMapping ? $"\nMapped to: {MappingText}" : "\nNot mapped";
            if (!IsConnected) text += "\n(device not connected or hidden)";
            return text + "\n\nClick to change what it does (double-click while editing the layout).";
        }
    }
}

/// <summary>A free text label on the layout page.</summary>
public sealed class LayoutTextViewModel(LayoutText model) : LayoutItemViewModel
{
    public override string ToString() => Text;

    public LayoutText Model { get; } = model;

    public override double X { get => Model.X; set => Model.X = value; }
    public override double Y { get => Model.Y; set => Model.Y = value; }

    public string Text
    {
        get => Model.Text;
        set
        {
            if (Model.Text == value) return;
            Model.Text = value;
            Raise();
        }
    }

    public double FontSize
    {
        get => Model.FontSize;
        set
        {
            if (Model.FontSize == value) return;
            Model.FontSize = value;
            Raise();
        }
    }
}

/// <summary>A control (or part of one) that isn't on the current layout page yet, listed in the
/// editing palette.</summary>
public sealed record LayoutPaletteItem(Guid DeviceGuid, ControlKind Kind, int Index, InputPart Part, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One device's not-yet-placed controls in the editing palette.</summary>
public sealed class LayoutPaletteGroup(Guid deviceGuid, string deviceLabel)
{
    public override string ToString() => DeviceLabel;

    public Guid DeviceGuid { get; } = deviceGuid;
    public string DeviceLabel { get; } = deviceLabel;
    public ObservableCollection<LayoutPaletteItem> Items { get; } = [];
}
