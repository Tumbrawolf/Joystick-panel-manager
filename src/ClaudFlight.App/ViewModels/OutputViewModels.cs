using System.Collections.ObjectModel;
using ClaudFlight.Core.Models;

namespace ClaudFlight.App.ViewModels;

/// <summary>One vJoy device on the vJoy Output tab: every axis, button and hat the profile writes
/// to, showing the value games currently see.</summary>
public sealed class VJoyOutputDeviceViewModel(uint id)
{
    public uint Id { get; } = id;
    public string Title => $"vJoy device {Id}";
    public ObservableCollection<VJoyAxisOutputViewModel> Axes { get; } = [];
    public ObservableCollection<VJoyButtonOutputViewModel> Buttons { get; } = [];
    public ObservableCollection<VJoyHatOutputViewModel> Hats { get; } = [];
    public override string ToString() => Title;
}

public sealed class VJoyAxisOutputViewModel(VJoyAxis axis, string drivenBy) : ObservableBase
{
    private double _value;
    private bool _written;

    public VJoyAxis Axis { get; } = axis;
    public string Name => Axis.ToString();

    /// <summary>What writes to it, e.g. "X (Stick)".</summary>
    public string DrivenBy { get; } = drivenBy;

    /// <summary>-1..1.</summary>
    public double Value => _value;

    /// <summary>0..1 for a progress bar.</summary>
    public double Fill => (_value + 1) / 2;

    public string Text => _written ? $"{_value * 100:+0;-0;0}%" : "-";

    public void Set(double? value)
    {
        var written = value.HasValue;
        var v = value ?? 0;
        if (written == _written && Math.Abs(v - _value) < 0.0005) return;
        _written = written;
        _value = v;
        Raise(nameof(Value));
        Raise(nameof(Fill));
        Raise(nameof(Text));
    }

    public override string ToString() => $"{Name} {Text}";
}

public sealed class VJoyButtonOutputViewModel(int number, string drivenBy) : ObservableBase
{
    private bool _isPressed;

    /// <summary>1-based, as games number them.</summary>
    public int Number { get; } = number;
    public string DrivenBy { get; } = drivenBy;

    public bool IsPressed
    {
        get => _isPressed;
        set => SetField(ref _isPressed, value);
    }

    public override string ToString() => $"Button {Number}{(IsPressed ? " pressed" : "")}";
}

public sealed class VJoyHatOutputViewModel(int number, string drivenBy) : ObservableBase
{
    private int _direction = -1;

    public int Number { get; } = number;
    public string Name => $"Hat {Number}";
    public string DrivenBy { get; } = drivenBy;

    public string DirectionText => _direction switch
    {
        0 => "↑ Up",
        1 => "→ Right",
        2 => "↓ Down",
        3 => "← Left",
        _ => "centered",
    };

    public int Direction
    {
        get => _direction;
        set
        {
            if (SetField(ref _direction, value)) Raise(nameof(DirectionText));
        }
    }

    public override string ToString() => $"{Name}: {DirectionText}";
}
