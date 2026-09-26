using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Navigation;
using System.Windows.Threading;
using ClaudFlight.App.ViewModels;
using ClaudFlight.Core.Devices;
using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Mapping;
using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;
using ClaudFlight.Core.VJoy;

namespace ClaudFlight.App;

public partial class MainWindow : Window
{
    private readonly DeviceManager _deviceManager = new();
    private readonly VJoyManager _vjoyManager = new();
    private readonly ProfileStore _profileStore = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly MappingEngine _engine;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private Profile _currentProfile = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;
    private bool _trayBalloonShown;

    public ObservableCollection<DeviceMonitorViewModel> Devices { get; } = [];
    public ObservableCollection<HiddenDeviceViewModel> HiddenDevices { get; } = [];
    public ObservableCollection<CalibrationRowViewModel> CalibrationRows { get; } = [];
    public ObservableCollection<BindingRowViewModel> BindingRows { get; } = [];
    public ControlKind[] ControlKindValues { get; } = Enum.GetValues<ControlKind>();

    /// <summary>One pickable option in the Mapping tab's Source/Target dropdowns: what to show
    /// the user (Display) and the technical text that actually goes into the Binding (IndexText -
    /// an axis name like "X", or a 1-based number).</summary>
    public sealed record ControlOption(string Display, string IndexText);

    public ObservableCollection<ControlOption> SourceControlOptions { get; } = [];
    public ObservableCollection<ControlOption> TargetControlOptions { get; } = [];

    public ObservableCollection<KeyBindingRowViewModel> KeyBindingRows { get; } = [];
    public ObservableCollection<ControlOption> KeySourceControlOptions { get; } = [];
    public IReadOnlyList<KeyOption> KeyOptions { get; } = KeyCatalog.Keys;

    public MainWindow()
    {
        _engine = new MappingEngine(_deviceManager, _vjoyManager);

        InitializeComponent();
        DataContext = this;

        _timer.Tick += Timer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        // If Windows itself is shutting down or logging off, let the app actually exit (and run
        // its normal cleanup/autosave) instead of cancelling the close to hide in the tray - doing
        // that here would fight the OS shutdown instead of just getting out of its way.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _isExiting = true;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();

        VJoyStatusText.Text = _vjoyManager.IsAvailable
            ? "vJoy: available"
            : $"vJoy: unavailable ({_vjoyManager.UnavailableReason})";

        // Auto-load whatever profile was active last time the app was closed, so renames,
        // calibration, and mappings persist across restarts without an explicit Load click.
        var settings = _settingsStore.Load();
        if (settings.LastProfileName is { } lastName)
        {
            try { _currentProfile = _profileStore.Load(lastName); }
            catch (IOException) { /* profile file missing/moved; fall back to a blank profile */ }
        }

        _engine.LoadProfile(_currentProfile);
        RefreshDevices();
        RebuildBindingRowsFromProfile();
        RebuildKeyBindingRowsFromProfile();
        RefreshProfileList();
        _timer.Start();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            // Clicking the window's X sends it to the tray instead of exiting, so polling/vJoy
            // mapping/keyboard injection keep running in the background (e.g. while a game is in
            // focus). Only the tray icon's own "Exit" actually shuts the app down.
            e.Cancel = true;
            Hide();

            if (!_trayBalloonShown)
            {
                _trayIcon?.ShowBalloonTip(3000, "ClaudFlight",
                    "Still running in the background. Right-click the tray icon to reopen or exit.",
                    System.Windows.Forms.ToolTipIcon.Info);
                _trayBalloonShown = true;
            }
            return;
        }

        _timer.Stop();
        AutoSaveCurrentProfile();

        _engine.LoadProfile(new Profile());
        _deviceManager.Dispose();
        _vjoyManager.Dispose();
        _trayIcon?.Dispose();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Minimizing also drops to the tray rather than sitting in the taskbar, matching Close's
        // behavior - both just mean "get this out of the way, keep running".
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void InitializeTrayIcon()
    {
        var icon = Environment.ProcessPath is { } exePath
            ? System.Drawing.Icon.ExtractAssociatedIcon(exePath)
            : null;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show ClaudFlight", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "ClaudFlight",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _isExiting = true;
        Close();
    }

    /// <summary>Silently persists whatever the current profile's state is (including any renames
    /// made this session) so it comes back automatically on the next launch, without requiring
    /// an explicit Save click.</summary>
    private void AutoSaveCurrentProfile()
    {
        HarvestInputLabels();
        if (string.IsNullOrWhiteSpace(_currentProfile.Name)) _currentProfile.Name = "Default";

        try
        {
            _profileStore.Save(_currentProfile);
            _settingsStore.Save(new AppSettings { LastProfileName = _currentProfile.Name });
        }
        catch (IOException)
        {
            // Best-effort autosave; don't block shutdown over a save failure.
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var snapshots = _engine.Tick();

        foreach (var device in Devices)
        {
            if (snapshots.TryGetValue(device.DeviceGuid, out var snapshot)) device.ApplySnapshot(snapshot);
        }
    }

    private void RefreshDevices()
    {
        // Preserve any unsaved rename edits before we rebuild the view models from scratch.
        HarvestInputLabels();

        var handle = new WindowInteropHelper(this).EnsureHandle();
        _deviceManager.Refresh(handle);

        HiddenDevices.Clear();
        var visible = new List<DeviceMonitorViewModel>();
        foreach (var device in _deviceManager.Devices)
        {
            var label = _currentProfile.DeviceLabels.GetValueOrDefault(device.InstanceGuid, device.Name);

            if (_currentProfile.ExcludedDeviceGuids.Contains(device.InstanceGuid))
            {
                HiddenDevices.Add(new HiddenDeviceViewModel(device.InstanceGuid, label));
                continue;
            }

            var vm = new DeviceMonitorViewModel(device.InstanceGuid, device.Name) { Label = label, PresentAxes = device.PresentAxes };

            foreach (var axis in device.PresentAxes)
            {
                var defaultLabel = device.AxisDeviceNames.GetValueOrDefault(axis, axis.ToString());
                if (IsControlExcluded(device.InstanceGuid, ControlKind.Axis, (int)axis))
                {
                    vm.HiddenControls.Add(new HiddenControlViewModel(ControlKind.Axis, (int)axis, GetSavedLabel(device.InstanceGuid, ControlKind.Axis, (int)axis, defaultLabel)));
                    continue;
                }
                var axisVm = new AxisReadingViewModel(device.InstanceGuid, axis) { Label = defaultLabel };
                vm.Axes.Add(axisVm);
            }
            for (var i = 0; i < device.ButtonCount; i++)
            {
                if (IsControlExcluded(device.InstanceGuid, ControlKind.Button, i))
                {
                    vm.HiddenControls.Add(new HiddenControlViewModel(ControlKind.Button, i, GetSavedLabel(device.InstanceGuid, ControlKind.Button, i, $"{i + 1}")));
                    continue;
                }
                vm.Buttons.Add(new ButtonReadingViewModel(device.InstanceGuid, i));
            }
            for (var i = 0; i < device.PovCount; i++)
            {
                if (IsControlExcluded(device.InstanceGuid, ControlKind.Pov, i))
                {
                    vm.HiddenControls.Add(new HiddenControlViewModel(ControlKind.Pov, i, GetSavedLabel(device.InstanceGuid, ControlKind.Pov, i, $"POV {i + 1}")));
                    continue;
                }
                vm.Povs.Add(new PovReadingViewModel(device.InstanceGuid, i));
            }

            ApplyInputLabels(vm);
            visible.Add(vm);
        }

        // Apply the user's drag/drop ordering: devices named in DeviceOrder come first in that
        // order, anything new (never reordered) is appended afterward in detection order.
        var orderIndex = _currentProfile.DeviceOrder;
        var ordered = visible
            .OrderBy(d => orderIndex.IndexOf(d.DeviceGuid) is var i && i < 0 ? int.MaxValue : i)
            .ToList();

        Devices.Clear();
        foreach (var vm in ordered) Devices.Add(vm);

        RebuildCalibrationRows();
        RefreshMappingFormOptions();
        RefreshKeySourceControlOptions();
    }

    /// <summary>Captures the current on-screen device card order into the profile so drag/drop
    /// reordering survives a refresh or restart.</summary>
    private void PersistDeviceOrder() => _currentProfile.DeviceOrder = Devices.Select(d => d.DeviceGuid).ToList();

    private bool IsControlExcluded(Guid deviceGuid, ControlKind kind, int index) =>
        _currentProfile.ExcludedControls.Any(c => c.DeviceGuid == deviceGuid && c.Kind == kind && c.Index == index);

    private string GetSavedLabel(Guid deviceGuid, ControlKind kind, int index, string defaultLabel) =>
        _currentProfile.InputLabels.FirstOrDefault(l => l.DeviceGuid == deviceGuid && l.Kind == kind && l.Index == index)?.Label ?? defaultLabel;

    /// <summary>Hides one axis/button/POV from Live Monitor and Calibration on an otherwise-visible
    /// device. Purely cosmetic - any existing mapping using this control keeps working.</summary>
    private void HideControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: INamedControl control }) return;

        _currentProfile.ExcludedControls.Add(new ExcludedControlEntry { DeviceGuid = control.DeviceGuid, Kind = control.Kind, Index = control.ControlIndex });
        RefreshDevices();
    }

    private void RestoreControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceMonitorViewModel device }) return;
        if (device.SelectedHiddenControl is not { } hidden) return;

        _currentProfile.ExcludedControls.RemoveAll(c => c.DeviceGuid == device.DeviceGuid && c.Kind == hidden.Kind && c.Index == hidden.Index);
        RefreshDevices();
    }

    /// <summary>Copies current rename edits (device + axis/button/POV labels) from the live view
    /// models into _currentProfile, so they survive a device refresh or a profile save. Upserts
    /// rather than clearing-and-rebuilding: a hidden control (or an entirely hidden device) isn't
    /// represented in Devices/Axes/Buttons/Povs, so a clear-and-rebuild would silently discard its
    /// saved rename every time this runs while it stays hidden.</summary>
    private void HarvestInputLabels()
    {
        if (Devices.Count == 0) return;

        foreach (var device in Devices) _currentProfile.DeviceLabels[device.DeviceGuid] = device.Label;

        foreach (var device in Devices)
        {
            foreach (var axis in device.Axes) UpsertInputLabel(device.DeviceGuid, ControlKind.Axis, (int)axis.Axis, axis.Label);
            foreach (var button in device.Buttons) UpsertInputLabel(device.DeviceGuid, ControlKind.Button, button.Index, button.Label);
            foreach (var pov in device.Povs) UpsertInputLabel(device.DeviceGuid, ControlKind.Pov, pov.Index, pov.Label);
            foreach (var hidden in device.HiddenControls) UpsertInputLabel(device.DeviceGuid, hidden.Kind, hidden.Index, hidden.Label);
        }
    }

    private void UpsertInputLabel(Guid deviceGuid, ControlKind kind, int index, string label)
    {
        var existing = _currentProfile.InputLabels.FirstOrDefault(l => l.DeviceGuid == deviceGuid && l.Kind == kind && l.Index == index);
        if (existing is not null) _currentProfile.InputLabels.Remove(existing);
        _currentProfile.InputLabels.Add(new InputLabelEntry { DeviceGuid = deviceGuid, Kind = kind, Index = index, Label = label });
    }

    /// <summary>Applies any saved renames from _currentProfile onto a freshly-built device view model.</summary>
    private void ApplyInputLabels(DeviceMonitorViewModel vm)
    {
        foreach (var axis in vm.Axes)
        {
            var entry = _currentProfile.InputLabels.FirstOrDefault(l => l.DeviceGuid == vm.DeviceGuid && l.Kind == ControlKind.Axis && l.Index == (int)axis.Axis);
            if (entry is not null) axis.Label = entry.Label;
        }
        foreach (var button in vm.Buttons)
        {
            var entry = _currentProfile.InputLabels.FirstOrDefault(l => l.DeviceGuid == vm.DeviceGuid && l.Kind == ControlKind.Button && l.Index == button.Index);
            if (entry is not null) button.Label = entry.Label;
        }
        foreach (var pov in vm.Povs)
        {
            var entry = _currentProfile.InputLabels.FirstOrDefault(l => l.DeviceGuid == vm.DeviceGuid && l.Kind == ControlKind.Pov && l.Index == pov.Index);
            if (entry is not null) pov.Label = entry.Label;
        }
    }

    private void RebuildCalibrationRows()
    {
        CalibrationRows.Clear();
        foreach (var device in Devices)
        {
            foreach (var axisVm in device.Axes)
            {
                CalibrationRows.Add(new CalibrationRowViewModel(_engine, device, axisVm));
            }
        }
    }

    /// <summary>Rebuilds BindingRows from _currentProfile.Bindings, resolving each row's live
    /// device/control references against whatever's currently connected (Devices must already
    /// be populated). Used both on startup and when loading a profile mid-session.</summary>
    private void RebuildBindingRowsFromProfile()
    {
        BindingRows.Clear();
        foreach (var binding in _currentProfile.Bindings)
        {
            var device = Devices.FirstOrDefault(d => d.DeviceGuid == binding.Source.DeviceGuid);
            BindingRows.Add(BindingRowViewModel.FromBinding(binding, device));
        }
    }

    private void RebuildProfileBindings()
    {
        _currentProfile.Bindings.Clear();
        foreach (var row in BindingRows)
        {
            var binding = row.TryBuildBinding(out _);
            if (binding is not null) _currentProfile.Bindings.Add(binding);
        }
        _engine.LoadProfile(_currentProfile);
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceMonitorViewModel device }) return;

        if (MessageBox.Show(this, $"Hide '{device.Label}' from Live Monitor, Calibration, and Mapping?\n\nAny existing mappings using it will be removed.",
                "Remove device", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _currentProfile.ExcludedDeviceGuids.Add(device.DeviceGuid);

        var removed = BindingRows.Where(r => r.SourceDeviceGuid == device.DeviceGuid).ToList();
        foreach (var row in removed) BindingRows.Remove(row);
        RebuildProfileBindings();

        RefreshDevices();
    }

    private void RestoreDevice_Click(object sender, RoutedEventArgs e)
    {
        if (HiddenDevicesCombo.SelectedItem is not HiddenDeviceViewModel hidden) return;

        _currentProfile.ExcludedDeviceGuids.Remove(hidden.DeviceGuid);
        RefreshDevices();
    }

    private void ExportDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceMonitorViewModel device }) return;

        var dialog = new SaveFileDialog
        {
            Title = $"Export '{device.Label}'",
            Filter = "ClaudFlight device export (*.json)|*.json",
            FileName = device.Label,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _profileStore.SaveToFile(BuildDeviceProfile(device), dialog.FileName);
            MessageBox.Show(this, $"Exported '{device.Label}' to:\n{dialog.FileName}", "Export device", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Couldn't export the device:\n{ex.Message}", "Export device", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Extracts just one device's slice of the current profile (its label, its axes'
    /// calibration/renames, hidden controls, and any vJoy/keyboard mappings sourced from it) into
    /// a standalone Profile-shaped file, so it can be exported/shared/re-imported independently of
    /// everything else in the profile.</summary>
    private Profile BuildDeviceProfile(DeviceMonitorViewModel device)
    {
        HarvestInputLabels();
        var guid = device.DeviceGuid;

        return new Profile
        {
            Name = device.Label,
            DeviceLabels = _currentProfile.DeviceLabels.TryGetValue(guid, out var label)
                ? new Dictionary<Guid, string> { [guid] = label }
                : [],
            InputLabels = _currentProfile.InputLabels.Where(l => l.DeviceGuid == guid).ToList(),
            AxisCalibrations = _currentProfile.AxisCalibrations.Where(c => c.DeviceGuid == guid).ToList(),
            ExcludedControls = _currentProfile.ExcludedControls.Where(c => c.DeviceGuid == guid).ToList(),
            Bindings = _currentProfile.Bindings.Where(b => b.Source.DeviceGuid == guid).ToList(),
            KeyBindings = _currentProfile.KeyBindings.Where(k => k.Source.DeviceGuid == guid).ToList(),
        };
    }

    private void ImportDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceMonitorViewModel device }) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Import into '{device.Label}'",
            Filter = "ClaudFlight device export (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        Profile imported;
        try
        {
            imported = _profileStore.LoadFromFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't import that file:\n{ex.Message}", "Import device", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ImportIntoDevice(device, imported);
        MessageBox.Show(this, $"Imported '{imported.Name}' into '{device.Label}'.\n\nClick Save if you want to keep this in your saved profile.",
            "Import device", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Merges a device-scoped export (or, harmlessly, just the matching slice of a full
    /// profile export) into the current profile, re-targeting every entry from whatever device
    /// guid it was originally exported with onto targetDevice's actual guid - the exported file's
    /// device may have a different DirectInput instance guid (different machine, different USB
    /// port) than the one it's being imported into.</summary>
    private void ImportIntoDevice(DeviceMonitorViewModel targetDevice, Profile imported)
    {
        var sourceGuid = FindAnyDeviceGuid(imported) ?? targetDevice.DeviceGuid;
        var targetGuid = targetDevice.DeviceGuid;

        if (imported.DeviceLabels.TryGetValue(sourceGuid, out var importedLabel))
        {
            _currentProfile.DeviceLabels[targetGuid] = importedLabel;
            targetDevice.Label = importedLabel;
        }

        foreach (var entry in imported.InputLabels.Where(l => l.DeviceGuid == sourceGuid))
            UpsertInputLabel(targetGuid, entry.Kind, entry.Index, entry.Label);

        foreach (var cal in imported.AxisCalibrations.Where(c => c.DeviceGuid == sourceGuid))
            _engine.SetCalibration(targetGuid, cal.Axis, cal.Calibration);

        foreach (var excluded in imported.ExcludedControls.Where(c => c.DeviceGuid == sourceGuid))
        {
            if (!_currentProfile.ExcludedControls.Any(c => c.DeviceGuid == targetGuid && c.Kind == excluded.Kind && c.Index == excluded.Index))
            {
                _currentProfile.ExcludedControls.Add(new ExcludedControlEntry { DeviceGuid = targetGuid, Kind = excluded.Kind, Index = excluded.Index });
            }
        }

        foreach (var binding in imported.Bindings.Where(b => b.Source.DeviceGuid == sourceGuid))
        {
            _currentProfile.Bindings.Add(new Binding { Source = binding.Source with { DeviceGuid = targetGuid }, Target = binding.Target });
        }

        foreach (var keyBinding in imported.KeyBindings.Where(k => k.Source.DeviceGuid == sourceGuid))
        {
            _currentProfile.KeyBindings.Add(new ClaudFlight.Core.Models.KeyBinding
            {
                Source = keyBinding.Source with { DeviceGuid = targetGuid },
                VirtualKeyCode = keyBinding.VirtualKeyCode,
                HoldWhilePressed = keyBinding.HoldWhilePressed,
                PovDirection = keyBinding.PovDirection,
                AxisThreshold = keyBinding.AxisThreshold,
                AxisAboveThreshold = keyBinding.AxisAboveThreshold,
            });
        }

        _engine.LoadProfile(_currentProfile);
        RefreshDevices();
        RebuildBindingRowsFromProfile();
        RebuildKeyBindingRowsFromProfile();
    }

    private static Guid? FindAnyDeviceGuid(Profile profile)
    {
        if (profile.DeviceLabels.Count > 0) return profile.DeviceLabels.Keys.First();
        if (profile.InputLabels.Count > 0) return profile.InputLabels[0].DeviceGuid;
        if (profile.AxisCalibrations.Count > 0) return profile.AxisCalibrations[0].DeviceGuid;
        if (profile.ExcludedControls.Count > 0) return profile.ExcludedControls[0].DeviceGuid;
        if (profile.Bindings.Count > 0) return profile.Bindings[0].Source.DeviceGuid;
        if (profile.KeyBindings.Count > 0) return profile.KeyBindings[0].Source.DeviceGuid;
        return null;
    }

    private Point _deviceDragStartPoint;

    /// <summary>Drag handle is just the card's header row (device name + Remove button), not the
    /// whole card, so a click into the rename TextBox below never gets mistaken for a drag.</summary>
    private void DeviceCardHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _deviceDragStartPoint = e.GetPosition(null);
    }

    private void DeviceCardHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { DataContext: DeviceMonitorViewModel device } header) return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _deviceDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _deviceDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(header, device, DragDropEffects.Move);
    }

    private void DeviceCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(DeviceMonitorViewModel))) return;
        if (sender is not FrameworkElement { DataContext: DeviceMonitorViewModel targetDevice }) return;
        if (e.Data.GetData(typeof(DeviceMonitorViewModel)) is not DeviceMonitorViewModel draggedDevice) return;
        if (ReferenceEquals(draggedDevice, targetDevice)) return;

        var oldIndex = Devices.IndexOf(draggedDevice);
        var newIndex = Devices.IndexOf(targetDevice);
        if (oldIndex < 0 || newIndex < 0) return;

        Devices.Move(oldIndex, newIndex);
        PersistDeviceOrder();
    }

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (SourceDeviceCombo.SelectedItem is not DeviceMonitorViewModel sourceDevice)
        {
            MessageBox.Show(this, "Select a source device first.", "Add mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SourceControlCombo.SelectedItem is not ControlOption sourceOption)
        {
            MessageBox.Show(this, "Select a source control first - this list is empty if the device has none of the selected Kind.", "Add mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (TargetControlCombo.SelectedItem is not ControlOption targetOption)
        {
            MessageBox.Show(this, "Select a target control first.", "Add mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var row = new BindingRowViewModel
        {
            SourceDeviceGuid = sourceDevice.DeviceGuid,
            SourceDevice = sourceDevice,
            SourceKind = (ControlKind)SourceKindCombo.SelectedItem,
            SourceIndexText = sourceOption.IndexText,
            TargetVJoyDeviceId = uint.TryParse(TargetVJoyIdBox.Text, out var id) ? id : 1,
            TargetIndexText = targetOption.IndexText,
        };

        var binding = row.TryBuildBinding(out var error);
        if (binding is null)
        {
            MessageBox.Show(this, error, "Add mapping", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        row.SourceControl = sourceDevice.FindControl(binding.Source.Kind, binding.Source.Index);
        BindingRows.Add(row);
        RebuildProfileBindings();
    }

    /// <summary>Adds one mapping for every axis/button/POV on the selected source device, onto
    /// the selected vJoy device - an alternative to adding mappings one at a time above. Axes
    /// prefer the same-named vJoy axis (X-&gt;X, RotationZ-&gt;RotationZ, ...); if that's already
    /// taken, or for buttons/POVs, it fills the next free slot, skipping anything that would
    /// collide with an existing mapping on that vJoy device or exceed vJoy's configured capacity.
    /// Existing mappings (however they were created) are left untouched, and every generated row
    /// can be edited or removed afterward just like a manually-added one.</summary>
    private void AutoPopulateMappings_Click(object sender, RoutedEventArgs e)
    {
        if (SourceDeviceCombo.SelectedItem is not DeviceMonitorViewModel device)
        {
            MessageBox.Show(this, "Select a source device first.", "Auto-populate mappings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var vjoyId = uint.TryParse(TargetVJoyIdBox.Text, out var id) ? id : 1u;

        var usedAxes = new HashSet<VJoyAxis>(BindingRows
            .Where(r => r.TargetVJoyDeviceId == vjoyId && r.SourceKind == ControlKind.Axis)
            .Select(r => Enum.TryParse<VJoyAxis>(r.TargetIndexText, true, out var a) ? a : (VJoyAxis?)null)
            .Where(a => a.HasValue).Select(a => a!.Value));
        var usedButtons = new HashSet<int>(BindingRows
            .Where(r => r.TargetVJoyDeviceId == vjoyId && r.SourceKind == ControlKind.Button)
            .Select(r => int.TryParse(r.TargetIndexText, out var n) ? n : (int?)null)
            .Where(n => n.HasValue).Select(n => n!.Value));
        var usedPovs = new HashSet<int>(BindingRows
            .Where(r => r.TargetVJoyDeviceId == vjoyId && r.SourceKind == ControlKind.Pov)
            .Select(r => int.TryParse(r.TargetIndexText, out var n) ? n : (int?)null)
            .Where(n => n.HasValue).Select(n => n!.Value));

        var added = 0;
        var skipped = 0;

        foreach (var axis in device.Axes)
        {
            VJoyAxis? target = null;
            if (Enum.TryParse<VJoyAxis>(axis.Axis.ToString(), out var sameNamed) && !usedAxes.Contains(sameNamed) &&
                (!_vjoyManager.IsAvailable || _vjoyManager.AxisExists(vjoyId, sameNamed)))
            {
                target = sameNamed;
            }
            else
            {
                foreach (var candidate in Enum.GetValues<VJoyAxis>())
                {
                    if (usedAxes.Contains(candidate)) continue;
                    if (_vjoyManager.IsAvailable && !_vjoyManager.AxisExists(vjoyId, candidate)) continue;
                    target = candidate;
                    break;
                }
            }

            if (target is null) { skipped++; continue; }
            usedAxes.Add(target.Value);
            AddAutoMapping(device, ControlKind.Axis, axis.Axis.ToString(), vjoyId, target.Value.ToString());
            added++;
        }

        var buttonCap = _vjoyManager.IsAvailable ? _vjoyManager.GetButtonCount(vjoyId) : int.MaxValue;
        var nextButton = 1;
        foreach (var button in device.Buttons)
        {
            while (usedButtons.Contains(nextButton)) nextButton++;
            if (nextButton > buttonCap) { skipped++; continue; }
            usedButtons.Add(nextButton);
            AddAutoMapping(device, ControlKind.Button, button.DisplayNumber.ToString(), vjoyId, nextButton.ToString());
            added++;
            nextButton++;
        }

        var povCap = _vjoyManager.IsAvailable ? _vjoyManager.GetDiscretePovCount(vjoyId) : int.MaxValue;
        var nextPov = 1;
        foreach (var pov in device.Povs)
        {
            while (usedPovs.Contains(nextPov)) nextPov++;
            if (nextPov > povCap) { skipped++; continue; }
            usedPovs.Add(nextPov);
            AddAutoMapping(device, ControlKind.Pov, pov.DisplayNumber.ToString(), vjoyId, nextPov.ToString());
            added++;
            nextPov++;
        }

        RebuildProfileBindings();

        var message = $"Added {added} mapping(s) for '{device.Label}' onto vJoy device {vjoyId}.";
        if (skipped > 0)
        {
            message += $"\n\n{skipped} control(s) were skipped - either every matching vJoy slot on that device was already used by another mapping, or vJoy device {vjoyId} isn't configured with enough axes/buttons/POVs (adjust that via vJoy's own \"Configure vJoy\" tool).";
        }
        message += "\n\nYou can edit vJoy Device #/Target # directly in the grid below, or remove any row.";
        MessageBox.Show(this, message, "Auto-populate mappings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddAutoMapping(DeviceMonitorViewModel device, ControlKind kind, string sourceIndexText, uint vjoyId, string targetIndexText)
    {
        var row = new BindingRowViewModel
        {
            SourceDeviceGuid = device.DeviceGuid,
            SourceDevice = device,
            SourceKind = kind,
            SourceIndexText = sourceIndexText,
            TargetVJoyDeviceId = vjoyId,
            TargetIndexText = targetIndexText,
        };

        var binding = row.TryBuildBinding(out _);
        if (binding is null) return;

        row.SourceControl = device.FindControl(binding.Source.Kind, binding.Source.Index);
        BindingRows.Add(row);
    }

    /// <summary>Grid edits to vJoy Device #/Target # only land in the bound BindingRowViewModel
    /// after this event returns, so the rebuild is deferred to the next dispatcher pass rather
    /// than reading the (still stale) value synchronously here.</summary>
    private void BindingsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(RebuildProfileBindings, DispatcherPriority.Background);
    }

    private void SourceDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshMappingFormOptions();
    private void SourceKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshMappingFormOptions();
    private void TargetVJoyIdBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshMappingFormOptions();

    /// <summary>Repopulates the Mapping tab's Source/Target dropdowns to match whatever's actually
    /// selected/available right now, so the user picks from real options instead of typing
    /// technical identifiers by hand.</summary>
    private void RefreshMappingFormOptions()
    {
        RefreshSourceControlOptions();
        RefreshTargetControlOptions();
    }

    private void RefreshSourceControlOptions()
    {
        SourceControlOptions.Clear();
        if (SourceDeviceCombo?.SelectedItem is not DeviceMonitorViewModel device) return;
        if (SourceKindCombo?.SelectedItem is not ControlKind kind) return;

        switch (kind)
        {
            case ControlKind.Axis:
                foreach (var axis in device.Axes)
                    SourceControlOptions.Add(new ControlOption($"{axis.Label} ({axis.Axis})", axis.Axis.ToString()));
                break;
            case ControlKind.Button:
                foreach (var button in device.Buttons)
                    SourceControlOptions.Add(new ControlOption($"{button.DisplayNumber} - {button.Label}", button.DisplayNumber.ToString()));
                break;
            case ControlKind.Pov:
                foreach (var pov in device.Povs)
                    SourceControlOptions.Add(new ControlOption($"{pov.DisplayNumber} - {pov.Label}", pov.DisplayNumber.ToString()));
                break;
        }

        if (SourceControlCombo is { } combo && SourceControlOptions.Count > 0) combo.SelectedIndex = 0;
    }

    private void RefreshTargetControlOptions()
    {
        TargetControlOptions.Clear();
        if (SourceKindCombo?.SelectedItem is not ControlKind kind) return;
        var vjoyId = uint.TryParse(TargetVJoyIdBox?.Text, out var id) ? id : 1u;

        switch (kind)
        {
            case ControlKind.Axis:
                foreach (var axis in Enum.GetValues<VJoyAxis>())
                {
                    if (_vjoyManager.IsAvailable && !_vjoyManager.AxisExists(vjoyId, axis)) continue;
                    TargetControlOptions.Add(new ControlOption(axis.ToString(), axis.ToString()));
                }
                break;
            case ControlKind.Button:
            {
                var count = _vjoyManager.IsAvailable ? _vjoyManager.GetButtonCount(vjoyId) : 32;
                for (var i = 1; i <= Math.Max(count, 1); i++) TargetControlOptions.Add(new ControlOption(i.ToString(), i.ToString()));
                break;
            }
            case ControlKind.Pov:
            {
                var count = _vjoyManager.IsAvailable ? _vjoyManager.GetDiscretePovCount(vjoyId) : 4;
                for (var i = 1; i <= Math.Max(count, 1); i++) TargetControlOptions.Add(new ControlOption(i.ToString(), i.ToString()));
                break;
            }
        }

        if (TargetControlCombo is { } targetCombo && TargetControlOptions.Count > 0) targetCombo.SelectedIndex = 0;
    }

    private void RemoveBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BindingRowViewModel row })
        {
            BindingRows.Remove(row);
            RebuildProfileBindings();
        }
    }

    private void SetMinFromLive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalibrationRowViewModel row }) row.SetMinFromLive();
    }

    private void SetCenterFromLive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalibrationRowViewModel row }) row.SetCenterFromLive();
    }

    private void SetCenterFromMinMax_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalibrationRowViewModel row }) row.SetCenterFromMinMax();
    }

    private void SetMaxFromLive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalibrationRowViewModel row }) row.SetMaxFromLive();
    }

    private void RefreshProfileList()
    {
        var names = _profileStore.ListProfileNames().OrderBy(n => n).ToList();
        ProfileListCombo.ItemsSource = names;
        ProfileNameBox.Text = _currentProfile.Name;
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        _currentProfile = new Profile();
        BindingRows.Clear();
        KeyBindingRows.Clear();
        _engine.LoadProfile(_currentProfile);
        RebuildCalibrationRows();
        ProfileNameBox.Text = _currentProfile.Name;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        _currentProfile.Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Default" : ProfileNameBox.Text.Trim();
        HarvestInputLabels();

        _profileStore.Save(_currentProfile);
        _settingsStore.Save(new AppSettings { LastProfileName = _currentProfile.Name });
        RefreshProfileList();
        MessageBox.Show(this, $"Saved profile '{_currentProfile.Name}'.", "Save profile", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileListCombo.SelectedItem is not string name) return;

        ApplyLoadedProfile(_profileStore.Load(name));
        _settingsStore.Save(new AppSettings { LastProfileName = _currentProfile.Name });
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        _currentProfile.Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Default" : ProfileNameBox.Text.Trim();
        HarvestInputLabels();

        var dialog = new SaveFileDialog
        {
            Title = "Export profile",
            Filter = "ClaudFlight profile (*.json)|*.json",
            FileName = _currentProfile.Name,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _profileStore.SaveToFile(_currentProfile, dialog.FileName);
            MessageBox.Show(this, $"Exported profile '{_currentProfile.Name}' to:\n{dialog.FileName}", "Export profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Couldn't export the profile:\n{ex.Message}", "Export profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import profile",
            Filter = "ClaudFlight profile (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        Profile imported;
        try
        {
            imported = _profileStore.LoadFromFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"Couldn't import that file as a profile:\n{ex.Message}", "Import profile", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ApplyLoadedProfile(imported);
        MessageBox.Show(this, $"Imported profile '{_currentProfile.Name}'.\n\nIt's now the active profile - click Save if you want it to also appear in the profile list above.",
            "Import profile", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Common tail end of switching to a newly-loaded-or-imported profile: wire it into
    /// the engine, apply its device/control renames to whatever's currently connected, and rebuild
    /// every dependent view.</summary>
    private void ApplyLoadedProfile(Profile profile)
    {
        _currentProfile = profile;
        _engine.LoadProfile(_currentProfile);

        foreach (var device in Devices)
        {
            device.Label = _currentProfile.DeviceLabels.GetValueOrDefault(device.DeviceGuid, device.DeviceName);
            ApplyInputLabels(device);
        }

        RebuildBindingRowsFromProfile();
        RebuildKeyBindingRowsFromProfile();
        RebuildCalibrationRows();
        ProfileNameBox.Text = _currentProfile.Name;
    }

    private void KeyboardInjectionToggle_Changed(object sender, RoutedEventArgs e)
    {
        _engine.KeyboardInjectionEnabled = KeyboardInjectionToggle.IsChecked == true;
    }

    /// <summary>Rebuilds KeyBindingRows from _currentProfile.KeyBindings, resolving each row's
    /// live device/control references against whatever's currently connected.</summary>
    private void RebuildKeyBindingRowsFromProfile()
    {
        KeyBindingRows.Clear();
        foreach (var keyBinding in _currentProfile.KeyBindings)
        {
            var device = Devices.FirstOrDefault(d => d.DeviceGuid == keyBinding.Source.DeviceGuid);
            KeyBindingRows.Add(KeyBindingRowViewModel.FromKeyBinding(keyBinding, device));
        }
    }

    private void RebuildProfileKeyBindings()
    {
        _currentProfile.KeyBindings.Clear();
        foreach (var row in KeyBindingRows)
        {
            var keyBinding = row.TryBuildKeyBinding(out _);
            if (keyBinding is not null) _currentProfile.KeyBindings.Add(keyBinding);
        }
        _engine.LoadProfile(_currentProfile);
    }

    private void AddKeyBinding_Click(object sender, RoutedEventArgs e)
    {
        if (KeySourceDeviceCombo.SelectedItem is not DeviceMonitorViewModel device)
        {
            MessageBox.Show(this, "Select a source device first.", "Add key mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (KeySourceControlCombo.SelectedItem is not ControlOption sourceOption)
        {
            MessageBox.Show(this, "Select a source control first - this list is empty if the device has none of the selected Kind.", "Add key mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (KeyNameCombo.SelectedItem is not KeyOption keyOption)
        {
            MessageBox.Show(this, "Select a key first.", "Add key mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var row = new KeyBindingRowViewModel
        {
            SourceDeviceGuid = device.DeviceGuid,
            SourceDevice = device,
            SourceKind = (ControlKind)KeySourceKindCombo.SelectedItem,
            SourceIndexText = sourceOption.IndexText,
            KeyName = keyOption.Name,
            HoldWhilePressed = HoldWhilePressedCheck.IsChecked == true,
            PovDirection = int.TryParse(PovDirectionBox.Text, out var povDir) ? povDir : 0,
            AxisThreshold = double.TryParse(AxisThresholdBox.Text, out var threshold) ? threshold : 0.5,
            AxisAboveThreshold = AxisAboveCheck.IsChecked == true,
        };

        var keyBinding = row.TryBuildKeyBinding(out var error);
        if (keyBinding is null)
        {
            MessageBox.Show(this, error, "Add key mapping", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        row.SourceControl = device.FindControl(keyBinding.Source.Kind, keyBinding.Source.Index);
        KeyBindingRows.Add(row);
        RebuildProfileKeyBindings();
    }

    private void RemoveKeyBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KeyBindingRowViewModel row })
        {
            KeyBindingRows.Remove(row);
            RebuildProfileKeyBindings();
        }
    }

    private void KeySourceDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshKeySourceControlOptions();
    private void KeySourceKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshKeySourceControlOptions();

    private void RefreshKeySourceControlOptions()
    {
        KeySourceControlOptions.Clear();
        if (KeySourceDeviceCombo?.SelectedItem is not DeviceMonitorViewModel device) return;
        if (KeySourceKindCombo?.SelectedItem is not ControlKind kind) return;

        switch (kind)
        {
            case ControlKind.Axis:
                foreach (var axis in device.Axes)
                    KeySourceControlOptions.Add(new ControlOption($"{axis.Label} ({axis.Axis})", axis.Axis.ToString()));
                break;
            case ControlKind.Button:
                foreach (var button in device.Buttons)
                    KeySourceControlOptions.Add(new ControlOption($"{button.DisplayNumber} - {button.Label}", button.DisplayNumber.ToString()));
                break;
            case ControlKind.Pov:
                foreach (var pov in device.Povs)
                    KeySourceControlOptions.Add(new ControlOption($"{pov.DisplayNumber} - {pov.Label}", pov.DisplayNumber.ToString()));
                break;
        }

        if (KeySourceControlCombo is { } combo && KeySourceControlOptions.Count > 0) combo.SelectedIndex = 0;
    }

    /// <summary>Grid edits only land in the bound KeyBindingRowViewModel after this event returns,
    /// so the rebuild is deferred rather than reading the (still stale) value synchronously here.</summary>
    private void KeyBindingsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(RebuildProfileKeyBindings, DispatcherPriority.Background);
    }
}
