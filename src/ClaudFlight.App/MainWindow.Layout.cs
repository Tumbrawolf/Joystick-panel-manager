using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudFlight.App.ViewModels;
using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;
using Microsoft.Win32;

namespace ClaudFlight.App;

/// <summary>The Layout tab: pages with a background picture (a controls diagram, a photo of the
/// cockpit, a hand-drawn schematic) with markers placed on top of it - one per control, or per
/// part of a control (each hat direction, each half of an axis), plus free text labels. Markers
/// light up live as their control is used and show what it's mapped to, and clicking one opens a
/// remap editor for it, so the page works as both a reference card and a mapping editor.
/// Positions are stored as fractions of the picture's size, so they stay put however the window is
/// sized.</summary>
public partial class MainWindow
{
    private const double LayoutPadding = 16; // matches LayoutBackgroundImage's Margin

    public ObservableCollection<LayoutPageViewModel> LayoutPages { get; } = [];
    public ObservableCollection<LayoutItemViewModel> LayoutItems { get; } = [];
    public ObservableCollection<LayoutPaletteGroup> LayoutPalette { get; } = [];

    private LayoutPage? _layoutPage;
    private ImageSource? _layoutBackground;
    private bool _isLayoutEditing;
    private LayoutItemViewModel? _draggingItem;
    private Vector _itemDragOffset;
    private Point _paletteDragStart;

    /// <summary>Reloads the page list from the current profile (startup, profile load/new),
    /// keeping the same page selected if it's still there.</summary>
    private void RebuildLayoutPages()
    {
        var previous = _layoutPage;
        LayoutPages.Clear();
        foreach (var page in _currentProfile.LayoutPages) LayoutPages.Add(new LayoutPageViewModel(page));
        LayoutPageCombo.SelectedItem = LayoutPages.FirstOrDefault(p => p.Page == previous) ?? LayoutPages.FirstOrDefault();
        ShowLayoutPage();
    }

    private void LayoutPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowLayoutPage();

    private void ShowLayoutPage()
    {
        _layoutPage = (LayoutPageCombo.SelectedItem as LayoutPageViewModel)?.Page;
        _layoutBackground = _layoutPage?.Background is { } bytes ? KeyImages.Decode(bytes) : null;
        LayoutBackgroundImage.Source = _layoutBackground;
        RebuildLayoutMarkers();
    }

    /// <summary>Rebuilds the current page's markers/text labels and the palette of controls not on
    /// it yet. Runs after every device refresh and remap too, since markers mirror Live Monitor's
    /// readings and show each control's mappings.</summary>
    private void RebuildLayoutMarkers()
    {
        _draggingItem = null;
        LayoutItems.Clear();
        LayoutPalette.Clear();

        var hasPage = _layoutPage is not null;
        LayoutPageNameBox.IsEnabled = hasPage;
        LayoutDeletePageButton.IsEnabled = hasPage;
        LayoutAddTextButton.IsEnabled = hasPage;
        LayoutExportImageButton.IsEnabled = hasPage;
        LayoutPrintButton.IsEnabled = hasPage;
        LayoutClearBackgroundButton.IsEnabled = _layoutPage?.Background is not null;

        if (_layoutPage is { } page)
        {
            foreach (var marker in page.Markers)
            {
                var reading = Devices.FirstOrDefault(d => d.DeviceGuid == marker.DeviceGuid)?.FindControl(marker.Kind, marker.Index);
                var deviceLabel = DeviceName(marker.DeviceGuid);
                var savedLabel = SavedControlName(marker.DeviceGuid, marker.Kind, marker.Index);
                var mapping = DescribeMarkerMappings(marker);
                LayoutItems.Add(new LayoutMarkerViewModel(marker, reading, savedLabel, deviceLabel, mapping)
                {
                    IsEditing = _isLayoutEditing,
                    IsZoneActive = marker.Part == InputPart.Zone ? ZoneCheck(marker.DeviceGuid, marker.Index) : null,
                });
            }

            foreach (var text in page.Texts)
            {
                LayoutItems.Add(new LayoutTextViewModel(text) { IsEditing = _isLayoutEditing });
            }

            foreach (var device in Devices)
            {
                var group = new LayoutPaletteGroup(device.DeviceGuid, device.Label);
                foreach (var (control, part) in device.ControlParts())
                {
                    if (page.Markers.Any(m => m.DeviceGuid == device.DeviceGuid && m.Kind == control.Kind && m.Index == control.ControlIndex && m.Part == part)) continue;
                    group.Items.Add(new LayoutPaletteItem(device.DeviceGuid, control.Kind, control.ControlIndex, part, control.Label + InputParts.Suffix(part)));
                }
                if (group.Items.Count > 0) LayoutPalette.Add(group);
            }
        }

        LayoutHintText.Text =
            !hasPage ? "Click \"New page\" to start a layout, then \"Set background...\" to load a picture of your controls."
            : _layoutPage!.Background is null && LayoutItems.Count == 0 ? "Click \"Set background...\" (or drop a picture file here) to load a picture of your controls, then tick \"Edit layout\" and drag controls onto it."
            : LayoutItems.Count == 0 && !_isLayoutEditing ? "Tick \"Edit layout\" to place controls on the picture."
            : "";

        PositionLayoutItems();
    }

    private static string DefaultControlLabel(ControlKind kind, int index) => kind switch
    {
        ControlKind.Axis => ((AxisType)index).ToString(),
        ControlKind.Button => $"Button {index + 1}",
        _ => $"POV {index + 1}",
    };

    // ---- Names for controls and devices, connected or not (one place for every list and window) ----

    /// <summary>A device's name: its live card's label, else the name saved in the profile.</summary>
    private string DeviceName(Guid deviceGuid, string whenUnknown = "Device not connected") =>
        Devices.FirstOrDefault(d => d.DeviceGuid == deviceGuid)?.Label
        ?? _currentProfile.DeviceLabels.GetValueOrDefault(deviceGuid, whenUnknown);

    /// <summary>A control's name as saved in the profile, else its default ("Button 3", "X"...).</summary>
    private string SavedControlName(Guid deviceGuid, ControlKind kind, int index) =>
        GetSavedLabel(deviceGuid, kind, index, DefaultControlLabel(kind, index));

    /// <summary>A control's name (live if its device is connected, else saved), with the part's
    /// suffix for a hat direction or axis half.</summary>
    private string ControlName(Guid deviceGuid, ControlKind kind, int index, InputPart part = InputPart.Whole) =>
        (Devices.FirstOrDefault(d => d.DeviceGuid == deviceGuid)?.FindControl(kind, index)?.Label
         ?? SavedControlName(deviceGuid, kind, index))
        + InputParts.Suffix(part);

    // ---- Remaps (used by the markers and RemapWindow) ----

    /// <summary>Everything one control (or one part of it) currently does, in both layers: vJoy
    /// outputs, axis nudges and keys.</summary>
    internal IReadOnlyList<RemapOutput> GetRemaps(Guid deviceGuid, ControlKind kind, int index, InputPart part)
    {
        var outputs = new List<RemapOutput>();

        foreach (var binding in _currentProfile.Bindings)
        {
            if (binding.Source.DeviceGuid != deviceGuid || binding.Source.Kind != kind || binding.Source.Index != index || binding.Part != part) continue;
            outputs.Add(new RemapOutput(DescribeBinding(binding), binding, binding.Shifted, binding.ModeName, ConflictNote(binding)));
        }

        foreach (var keyBinding in _currentProfile.KeyBindings)
        {
            if (keyBinding.Source.DeviceGuid != deviceGuid || keyBinding.Source.Kind != kind || keyBinding.Source.Index != index) continue;
            if (InputParts.Of(keyBinding) != part) continue;
            outputs.Add(new RemapOutput(DescribeKeyBinding(keyBinding), keyBinding, keyBinding.Shifted, keyBinding.ModeName, ConflictNote(keyBinding)));
        }

        return outputs;
    }

    /// <summary>Short description of a vJoy mapping, e.g. "vJoy 1 B5 (latch)" or "vJoy 1 Slider0 ▲ 50%/s".
    /// Button/hat numbers use the same 1-based numbering Live Monitor shows.</summary>
    internal static string DescribeBinding(Binding binding) =>
        WithTriggerAndZone(DescribeBindingOutput(binding), binding.Trigger, binding.Part == InputPart.Zone, binding.ZoneFrom, binding.ZoneTo);

    /// <summary>"90-100%: " in front for a zone, ", long press" after for a special trigger.</summary>
    private static string WithTriggerAndZone(string text, PressTrigger trigger, bool isZone, double zoneFrom, double zoneTo)
    {
        if (TriggerName(trigger) is { Length: > 0 } name) text += $", {name}";
        return isZone ? $"{Math.Min(zoneFrom, zoneTo) * 100:0}-{Math.Max(zoneFrom, zoneTo) * 100:0}%: {text}" : text;
    }

    private static string DescribeBindingOutput(Binding binding)
    {
        var target = binding.Target;
        if (binding.Nudge != AxisNudge.None)
        {
            var axis = $"vJoy {target.VJoyDeviceId} {(VJoyAxis)target.Index}";
            var percent = $"{binding.NudgeAmount * 100:0.#}%";
            return binding.Nudge switch
            {
                AxisNudge.Center => $"{axis} to center",
                AxisNudge.SetTo => $"{axis} to {percent}",
                _ => $"{axis} {(binding.Nudge == AxisNudge.Increase ? "▲" : "▼")} {percent}{(binding.Mode == SwitchMode.Hold ? "/s" : " per press")}",
            };
        }

        var text = target.Kind switch
        {
            ControlKind.Axis => $"vJoy {target.VJoyDeviceId} {(VJoyAxis)target.Index}",
            ControlKind.Button => $"vJoy {target.VJoyDeviceId} B{target.Index + 1}",
            _ => $"vJoy {target.VJoyDeviceId} Hat {target.Index + 1}",
        };
        if (target.Kind == ControlKind.Button)
        {
            text += binding.Mode switch
            {
                SwitchMode.PulseOnPress => " (pulse)",
                SwitchMode.PulseOnBoth => " (pulse on/off)",
                SwitchMode.Latch => binding.SayOn is not null || binding.SayOff is not null ? " (latch, spoken)" : " (latch)",
                _ => "",
            };
        }
        return text;
    }

    /// <summary>"" for a normal press, else "long press" etc.</summary>
    internal static string TriggerName(PressTrigger trigger) => trigger switch
    {
        PressTrigger.ShortPress => "short press",
        PressTrigger.LongPress => "long press",
        PressTrigger.DoubleTap => "double tap",
        _ => "",
    };

    /// <summary>"zone 90-100%".</summary>
    internal static string ZoneText(double from, double to) =>
        $"zone {Math.Min(from, to) * 100:0}-{Math.Max(from, to) * 100:0}%";

    internal static string DescribeKeyBinding(ClaudFlight.Core.Models.KeyBinding keyBinding)
    {
        string text;
        if (keyBinding.Sequence is { Count: > 0 } sequence)
        {
            text = $"Keys {KeySequenceText.Format(sequence)}";
        }
        else
        {
            var name = KeyCatalog.Keys.FirstOrDefault(k => k.VKey == keyBinding.VirtualKeyCode)?.Name ?? $"0x{keyBinding.VirtualKeyCode:X2}";
            text = $"Key {name}";
            if (keyBinding.Source.Kind != ControlKind.Axis && !keyBinding.HoldWhilePressed) text += keyBinding.TapOnRelease ? " (tap on/off)" : " (tap)";
        }
        return WithTriggerAndZone(text, keyBinding.Trigger, keyBinding.IsZone, keyBinding.ZoneFrom, keyBinding.ZoneTo);
    }

    /// <summary>Whether an axis is inside any of its zone mappings (either layer) right now. The
    /// zones are read once; markers are rebuilt whenever mappings change.</summary>
    private Func<bool> ZoneCheck(Guid deviceGuid, int axisIndex)
    {
        var zones = _currentProfile.Bindings
            .Where(b => b.Part == InputPart.Zone && b.Source.DeviceGuid == deviceGuid && b.Source.Kind == ControlKind.Axis && b.Source.Index == axisIndex)
            .Select(b => (b.ZoneFrom, b.ZoneTo))
            .Concat(_currentProfile.KeyBindings
                .Where(k => k.IsZone && k.Source.DeviceGuid == deviceGuid && k.Source.Kind == ControlKind.Axis && k.Source.Index == axisIndex)
                .Select(k => (k.ZoneFrom, k.ZoneTo)))
            .ToList();
        return () => AxisTravel(deviceGuid, axisIndex) is { } position && zones.Any(z => InputParts.InZone(position, z.ZoneFrom, z.ZoneTo));
    }

    /// <summary>Where an axis is along its travel right now (0..1, calibrated), or null if it isn't connected.</summary>
    internal double? AxisTravel(Guid deviceGuid, int axisIndex) => _engine.AxisTravel(deviceGuid, (AxisType)axisIndex);

    internal bool IsVJoyTargetUsed(uint vjoyDeviceId, ControlKind kind, int index) =>
        _currentProfile.Bindings.Any(b => b.Target.VJoyDeviceId == vjoyDeviceId && b.Target.Kind == kind && b.Target.Index == index);

    internal VJoyLimits GetVJoyLimits(uint vjoyDeviceId) => _vjoyManager.IsAvailable
        ? new VJoyLimits(true, _vjoyManager.GetButtonCount(vjoyDeviceId), _vjoyManager.GetDiscretePovCount(vjoyDeviceId),
            axis => _vjoyManager.AxisExists(vjoyDeviceId, axis))
        : new VJoyLimits(false, 128, 4, _ => true);

    internal bool IsShiftControl(RemapSlot slot) =>
        _currentProfile.ShiftControl is { } shift && shift.DeviceGuid == slot.DeviceGuid && shift.Kind == slot.Kind &&
        shift.Index == slot.Index && shift.Part == slot.Part;

    internal void SetShiftControl(RemapSlot slot, bool isShift)
    {
        if (isShift) _currentProfile.ShiftControl = new ShiftControl(slot.DeviceGuid, slot.Kind, slot.Index, slot.Part);
        else if (IsShiftControl(slot)) _currentProfile.ShiftControl = null;
        AfterRemapsChanged();
    }

    /// <summary>"Button 8 (Left Stick)"-style name of the shift control, or null if none is set.</summary>
    internal string? ShiftControlName()
    {
        if (_currentProfile.ShiftControl is not { } shift) return null;
        return $"{ControlName(shift.DeviceGuid, shift.Kind, shift.Index, shift.Part)} ({DeviceName(shift.DeviceGuid, "not connected")})";
    }

    /// <summary>The text under a marker: its normal mappings, then its shifted ones marked ⇧.</summary>
    private string DescribeMarkerMappings(LayoutMarker marker) =>
        DescribeMappings(marker.DeviceGuid, marker.Kind, marker.Index, marker.Part);

    /// <summary>Everything one control (or part) does, normal mappings first, shifted ones marked ⇧.</summary>
    private string DescribeMappings(Guid deviceGuid, ControlKind kind, int index, InputPart part)
    {
        var remaps = GetRemaps(deviceGuid, kind, index, part);
        // Any-mode mappings first, then each mode's (named in front).
        var parts = remaps.OrderBy(r => r.ModeName is not null).ThenBy(r => r.ModeName, StringComparer.OrdinalIgnoreCase)
            .Where(r => !r.Shifted).Select(r => ModePrefix(r) + r.Description)
            .Concat(remaps.Where(r => r.Shifted).Select(r => "⇧ " + ModePrefix(r) + r.Description))
            .ToList();
        var slot = new RemapSlot(deviceGuid, kind, index, part, "", "");
        if (IsShiftControl(slot)) parts.Insert(0, "SHIFT");
        return string.Join(", ", parts);
    }

    private static string ModePrefix(RemapOutput remap) => remap.ModeName is null ? "" : $"{remap.ModeName}: ";

    internal void AddRemap(object output)
    {
        if (output is Binding binding) _currentProfile.Bindings.Add(binding);
        else if (output is ClaudFlight.Core.Models.KeyBinding keyBinding) _currentProfile.KeyBindings.Add(keyBinding);
        AfterRemapsChanged();
    }

    /// <summary>Swaps an output for its changed version, keeping its place in the list.</summary>
    internal void ReplaceRemap(object oldOutput, object newOutput)
    {
        if (oldOutput is Binding oldBinding && newOutput is Binding newBinding && _currentProfile.Bindings.IndexOf(oldBinding) is var b and >= 0)
        {
            _currentProfile.Bindings[b] = newBinding;
        }
        else if (oldOutput is ClaudFlight.Core.Models.KeyBinding oldKey && newOutput is ClaudFlight.Core.Models.KeyBinding newKey &&
                 _currentProfile.KeyBindings.IndexOf(oldKey) is var k and >= 0)
        {
            _currentProfile.KeyBindings[k] = newKey;
        }
        else
        {
            RemoveRemap(oldOutput);
            AddRemap(newOutput);
            return;
        }
        AfterRemapsChanged();
    }

    internal void RemoveRemap(object output)
    {
        if (output is Binding binding) _currentProfile.Bindings.Remove(binding);
        else if (output is ClaudFlight.Core.Models.KeyBinding keyBinding) _currentProfile.KeyBindings.Remove(keyBinding);
        AfterRemapsChanged();
    }

    /// <summary>Pushes a remap made from the Layout tab everywhere else: the engine, the two
    /// Mappings list, and the markers' mapping text.</summary>
    private void AfterRemapsChanged()
    {
        RefreshConflicts();
        _engine.LoadProfile(_currentProfile);
        RebuildLayoutMarkers();
        RebuildMappingList();
        _outputBuiltFor = null; // the vJoy Output tab rebuilds next time it's shown
    }

    private void OpenRemapEditor(LayoutMarkerViewModel marker)
    {
        var m = marker.Marker;
        OpenRemap(m.DeviceGuid, m.Kind, m.Index, m.Part);
    }

    /// <summary>Opens the remap window for any control (or part of one), from wherever: a Layout
    /// marker, Live Monitor's right-click menu, the Mappings list. With editOutput, that output is
    /// loaded ready to change.</summary>
    internal void OpenRemap(Guid deviceGuid, ControlKind kind, int index, InputPart part, object? editOutput = null)
    {
        var slot = new RemapSlot(deviceGuid, kind, index, part, ControlName(deviceGuid, kind, index, part), DeviceName(deviceGuid));
        new RemapWindow(this, slot, editOutput).ShowDialog();
    }

    // ---- Positioning ----

    /// <summary>Where the background picture is drawn inside LayoutArea (the whole padded area
    /// when there's no picture), matching the Image's Stretch="Uniform" placement.</summary>
    private Rect LayoutPictureRect()
    {
        var width = LayoutArea.ActualWidth - 2 * LayoutPadding;
        var height = LayoutArea.ActualHeight - 2 * LayoutPadding;
        if (width <= 0 || height <= 0) return Rect.Empty;
        if (_layoutBackground is not { Width: > 0, Height: > 0 } picture) return new Rect(LayoutPadding, LayoutPadding, width, height);

        var scale = Math.Min(width / picture.Width, height / picture.Height);
        var drawnWidth = picture.Width * scale;
        var drawnHeight = picture.Height * scale;
        return new Rect(LayoutPadding + (width - drawnWidth) / 2, LayoutPadding + (height - drawnHeight) / 2, drawnWidth, drawnHeight);
    }

    private void PositionLayoutItems()
    {
        var rect = LayoutPictureRect();
        if (rect.IsEmpty) return;
        foreach (var item in LayoutItems) item.SetAnchor(rect.X + item.X * rect.Width, rect.Y + item.Y * rect.Height);
    }

    private void LayoutArea_SizeChanged(object sender, SizeChangedEventArgs e) => PositionLayoutItems();

    private void LayoutItem_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutItemViewModel item }) item.SetSize(e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>Refreshes the Layout markers / Mappings list when their tab is opened, so renames
    /// typed on Live Monitor meanwhile show up.</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (ReferenceEquals(MainTabs.SelectedItem, LayoutTab)) RebuildLayoutMarkers();
        else if (ReferenceEquals(MainTabs.SelectedItem, MappingsTab)) RebuildMappingList();
        else if (ReferenceEquals(MainTabs.SelectedItem, OutputTab)) RebuildOutputView();
        else if (ReferenceEquals(MainTabs.SelectedItem, ProfilesTab))
        {
            RefreshProfileList();
            RefreshRunningPrograms();
        }
    }

    // ---- Pages and background ----

    private LayoutPageViewModel AddLayoutPage(string name)
    {
        var page = new LayoutPage { Name = name };
        _currentProfile.LayoutPages.Add(page);
        var vm = new LayoutPageViewModel(page);
        LayoutPages.Add(vm);
        LayoutPageCombo.SelectedItem = vm; // fires SelectionChanged -> ShowLayoutPage
        return vm;
    }

    private void LayoutNewPage_Click(object sender, RoutedEventArgs e)
    {
        var number = 1;
        while (_currentProfile.LayoutPages.Any(p => p.Name == $"Layout {number}")) number++;
        AddLayoutPage($"Layout {number}");
    }

    private void LayoutDeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutPageCombo.SelectedItem is not LayoutPageViewModel vm) return;
        if (MessageBox.Show(this, $"Delete the layout page '{vm.Name}' and its picture? (Your mappings aren't affected.)", "Delete page",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _currentProfile.LayoutPages.Remove(vm.Page);
        LayoutPages.Remove(vm);
        LayoutPageCombo.SelectedItem = LayoutPages.FirstOrDefault();
        ShowLayoutPage();
    }

    private void LayoutSetBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Background picture", Filter = ImageFileFilter };
        if (dialog.ShowDialog(this) != true) return;
        SetLayoutBackground(dialog.FileName);
    }

    private void SetLayoutBackground(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't read that file:\n{ex.Message}", "Set background", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (KeyImages.Decode(bytes) is null)
        {
            MessageBox.Show(this, "That file isn't a picture ClaudFlight can open (try PNG, JPEG, BMP, GIF or TIFF).",
                "Set background", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // No page yet: start one named after the picture.
        if (_layoutPage is null) AddLayoutPage(Path.GetFileNameWithoutExtension(path));

        _layoutPage!.Background = bytes;
        ShowLayoutPage();
    }

    private void LayoutClearBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPage is null) return;
        _layoutPage.Background = null;
        ShowLayoutPage();
    }

    /// <summary>Saves the page as you see it (minus the editing handles) to a PNG, scaled up so
    /// the background keeps roughly its own resolution.</summary>
    private void LayoutExportImage_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPage is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export layout page as a picture",
            Filter = "PNG image (*.png)|*.png",
            FileName = _layoutPage.Name,
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (RenderLayoutPage() is not { } picture) return;
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using var file = File.Create(dialog.FileName);
            encoder.Save(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't save the picture:\n{ex.Message}", "Export image", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Prints the page as a reference card, turned and scaled to fit the paper. Picking
    /// "Microsoft Print to PDF" saves it as a PDF instead.</summary>
    private void LayoutPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPage is null) return;
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (RenderLayoutPage() is not { } picture) return;
            const double margin = 36; // half an inch
            var pageWidth = dialog.PrintableAreaWidth;
            var pageHeight = dialog.PrintableAreaHeight;
            var (width, height) = (picture.Width, picture.Height);
            var turn = width > height != pageWidth > pageHeight; // a wide picture on upright paper goes sideways
            var (fitWidth, fitHeight) = turn ? (height, width) : (width, height);
            var scale = Math.Min((pageWidth - 2 * margin) / fitWidth, (pageHeight - 2 * margin) / fitHeight);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(pageWidth / 2, pageHeight / 2));
                if (turn) context.PushTransform(new RotateTransform(90));
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawImage(picture, new Rect(-width / 2, -height / 2, width, height));
            }
            dialog.PrintVisual(visual, $"ClaudFlight - {_layoutPage.Name}");
        }
        catch (Exception ex) when (ex is System.Printing.PrintSystemException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Couldn't print the page:\n{ex.Message}", "Print", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>The current page as it looks locked (picture, markers, mappings and text, no hint),
    /// cropped to the picture plus the padding around it, or null if there's nothing to draw.</summary>
    private System.Windows.Media.Imaging.BitmapSource? RenderLayoutPage()
    {
        // Show the locked look (plain text labels, no hint) while rendering.
        foreach (var item in LayoutItems) item.IsEditing = false;
        var hintVisibility = LayoutHintText.Visibility;
        LayoutHintText.Visibility = Visibility.Collapsed;
        LayoutArea.UpdateLayout();
        LayoutArea.UpdateLayout(); // second pass: markers re-center after their size settles

        try
        {
            var picture = LayoutPictureRect();
            if (picture.IsEmpty) return null;
            var area = new Rect(0, 0, LayoutArea.ActualWidth, LayoutArea.ActualHeight);
            // Keep the padding around the picture so markers sitting on its edge aren't cut in half.
            var crop = Rect.Intersect(new Rect(picture.X - LayoutPadding, picture.Y - LayoutPadding,
                picture.Width + 2 * LayoutPadding, picture.Height + 2 * LayoutPadding), area);

            var scale = _layoutBackground is System.Windows.Media.Imaging.BitmapSource bitmap
                ? Math.Clamp(bitmap.PixelWidth / picture.Width, 1, 4)
                : 2;

            // Drawn through a VisualBrush so the element's own position in the window doesn't
            // offset the result.
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new VisualBrush(LayoutArea) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, area);
            }
            var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(area.Width * scale), (int)Math.Ceiling(area.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            full.Render(visual);

            var pixels = new Int32Rect((int)(crop.X * scale), (int)(crop.Y * scale),
                Math.Min((int)(crop.Width * scale), full.PixelWidth - (int)(crop.X * scale)),
                Math.Min((int)(crop.Height * scale), full.PixelHeight - (int)(crop.Y * scale)));
            var cropped = new System.Windows.Media.Imaging.CroppedBitmap(full, pixels);
            cropped.Freeze();
            return cropped;
        }
        finally
        {
            foreach (var item in LayoutItems) item.IsEditing = _isLayoutEditing;
            LayoutHintText.Visibility = hintVisibility;
        }
    }

    private void LayoutEditToggle_Changed(object sender, RoutedEventArgs e)
    {
        _isLayoutEditing = LayoutEditToggle.IsChecked == true;
        LayoutPalettePanel.Visibility = _isLayoutEditing ? Visibility.Visible : Visibility.Collapsed;
        // Locked: a click on a marker opens its remap editor. Editing: markers drag around.
        LayoutMarkersControl.Cursor = _isLayoutEditing ? Cursors.SizeAll : Cursors.Hand;
        RebuildLayoutMarkers();
    }

    // ---- Text labels ----

    private void LayoutAddText_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPage is null) return;

        // As near the middle as possible without covering a control's marker or another label.
        var (x, y) = _layoutPage.FreeSpotForText();
        var text = new LayoutText { Text = "Text", X = x, Y = y };
        _layoutPage.Texts.Add(text);

        if (LayoutEditToggle.IsChecked == true) RebuildLayoutMarkers();
        else LayoutEditToggle.IsChecked = true; // rebuilds via LayoutEditToggle_Changed

        // Put the cursor in it with "Text" selected, so it's obvious where it went and typing
        // replaces it straight away.
        Dispatcher.BeginInvoke(() =>
        {
            var vm = LayoutItems.OfType<LayoutTextViewModel>().FirstOrDefault(t => t.Model == text);
            if (vm is null || LayoutMarkersControl.ItemContainerGenerator.ContainerFromItem(vm) is not DependencyObject container) return;
            if (FindDescendant<TextBox>(container) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void LayoutTextBigger_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutTextViewModel text }) text.FontSize = Math.Min(text.FontSize + 4, 72);
    }

    private void LayoutTextSmaller_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutTextViewModel text }) text.FontSize = Math.Max(text.FontSize - 4, 8);
    }

    private void LayoutTextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPage is null || sender is not FrameworkElement { DataContext: LayoutTextViewModel text }) return;
        _layoutPage.Texts.Remove(text.Model);
        RebuildLayoutMarkers();
    }

    // ---- Placing controls ----

    private void PlaceOnLayout(LayoutPaletteItem item, double x, double y)
    {
        if (_layoutPage is null) return;
        if (_layoutPage.Markers.Any(m => m.DeviceGuid == item.DeviceGuid && m.Kind == item.Kind && m.Index == item.Index && m.Part == item.Part)) return;

        _layoutPage.Markers.Add(new LayoutMarker
        {
            DeviceGuid = item.DeviceGuid,
            Kind = item.Kind,
            Index = item.Index,
            Part = item.Part,
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
        });
    }

    private void LayoutPaletteItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _paletteDragStart = e.GetPosition(null);
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: LayoutPaletteItem item })
        {
            PlaceOnLayout(item, 0.5, 0.5);
            RebuildLayoutMarkers();
            e.Handled = true;
        }
    }

    private void LayoutPaletteItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { DataContext: LayoutPaletteItem item } element) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _paletteDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _paletteDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(element, item, DragDropEffects.Copy);
    }

    /// <summary>Lays a device's remaining controls out in a grid from the top-left corner, ready
    /// to be dragged onto the right spots.</summary>
    private void LayoutAddAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayoutPaletteGroup group }) return;

        const int columns = 8;
        var i = 0;
        foreach (var item in group.Items)
        {
            PlaceOnLayout(item, 0.06 + i % columns * 0.12, Math.Min(0.06 + i / columns * 0.08, 0.95));
            i++;
        }
        RebuildLayoutMarkers();
    }

    private void LayoutArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(LayoutPaletteItem)) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void LayoutArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LayoutPaletteItem)) is LayoutPaletteItem item)
        {
            var rect = LayoutPictureRect();
            if (rect.IsEmpty) return;
            var point = e.GetPosition(LayoutArea);
            PlaceOnLayout(item, (point.X - rect.X) / rect.Width, (point.Y - rect.Y) / rect.Height);
            RebuildLayoutMarkers();
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            SetLayoutBackground(files[0]);
        }
    }

    // ---- Clicking, moving and removing items ----

    private void LayoutItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayoutItemViewModel item } element) return;

        // A marker opens its remap editor: on a single click while the layout is locked, or a
        // double-click while editing (a single click/drag moves it then).
        if (item is LayoutMarkerViewModel marker && (!_isLayoutEditing || e.ClickCount == 2))
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(() => OpenRemapEditor(marker), DispatcherPriority.Input);
            return;
        }

        if (!_isLayoutEditing) return;
        var rect = LayoutPictureRect();
        if (rect.IsEmpty) return;

        var anchor = new Point(rect.X + item.X * rect.Width, rect.Y + item.Y * rect.Height);
        _itemDragOffset = e.GetPosition(LayoutArea) - anchor;
        _draggingItem = item;
        element.CaptureMouse();
        e.Handled = true;
    }

    private void LayoutItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingItem is null || sender is not FrameworkElement { DataContext: LayoutItemViewModel item } || item != _draggingItem) return;
        var rect = LayoutPictureRect();
        if (rect.IsEmpty) return;

        var point = e.GetPosition(LayoutArea) - _itemDragOffset;
        item.X = Math.Clamp((point.X - rect.X) / rect.Width, 0, 1);
        item.Y = Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1);
        item.SetAnchor(rect.X + item.X * rect.Width, rect.Y + item.Y * rect.Height);
    }

    private void LayoutItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingItem is null) return;
        _draggingItem = null;
        (sender as FrameworkElement)?.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void LayoutRemapMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutMarkerViewModel marker }) OpenRemapEditor(marker);
    }

    private void LayoutRemoveMarker_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPage is null || sender is not FrameworkElement { DataContext: LayoutMarkerViewModel marker }) return;
        _layoutPage.Markers.Remove(marker.Marker);
        RebuildLayoutMarkers();
    }
}
