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
using ClaudFlight.Core.RawInput;
using ClaudFlight.Core.StreamDock;
using ClaudFlight.Core.VJoy;

namespace ClaudFlight.App;

public partial class MainWindow : Window
{
    private readonly DeviceManager _deviceManager = new();
    private readonly RawInputKeyboardManager _rawInputManager = new();
    private readonly StreamDockManager _streamDockManager = new();
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

    public MainWindow()
    {
        // The recorders pass everything on unchanged and remember it for the vJoy Output tab.
        _vjoyRecorder = new VJoyOutputRecorder(_vjoyManager);
        _keyRecorder = new KeyOutputRecorder(new SendInputKeyOutput());
        _engine = new MappingEngine(AllDevices, _vjoyRecorder, _keyRecorder);
        HookEngineEvents();

        InitializeComponent();
        DataContext = this;

        _timer.Tick += Timer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        KeyDown += MainWindow_KeyDown;

        // Started by Windows at sign-in: come up minimized, then drop straight to the tray once loaded.
        if (StartedInTray)
        {
            WindowState = WindowState.Minimized;
            ShowActivated = false;
        }

        // If Windows itself is shutting down or logging off, let the app actually exit (and run
        // its normal cleanup/autosave) instead of cancelling the close to hide in the tray - doing
        // that here would fight the OS shutdown instead of just getting out of its way.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _isExiting = true;

        // A Raw Input keyboard-type device's button set is learned as keys are actually pressed;
        // persist each newly-learned one immediately (so it survives even if the app closes before
        // an explicit Save) and refresh so the new button shows up right away instead of only
        // after the next manual "Refresh Devices" click.
        _rawInputManager.KeyLearned += (deviceGuid, vkey) =>
        {
            if (!_currentProfile.RawInputLearnedKeys.Any(k => k.DeviceGuid == deviceGuid && k.VKey == vkey))
            {
                _currentProfile.RawInputLearnedKeys.Add(new RawInputLearnedKey { DeviceGuid = deviceGuid, VKey = vkey });
            }
            // A hidden device (the regular keyboard, by default) learns every new key typed; a
            // full refresh then would rebuild every view - and steal focus from whatever text box
            // is being typed in - for a device nobody's looking at.
            if (!_currentProfile.ExcludedDeviceGuids.Contains(deviceGuid)) RefreshDevices();
        };

        // A StreamDock panel (VSD Craft device) reports this from its background read thread when
        // a key with a new code first appears or the panel is unplugged.
        _streamDockManager.DevicesChanged += () => Dispatcher.BeginInvoke(RefreshDevices);
    }

    private IEnumerable<IPhysicalDevice> AllDevices() =>
        _deviceManager.Devices.Concat(_rawInputManager.Devices).Concat(_streamDockManager.Devices);

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        SyncStartupUi();
        if (StartedInTray) Hide();

        VJoyStatusText.Text = _vjoyManager.IsAvailable
            ? "vJoy: available"
            : $"vJoy: unavailable ({_vjoyManager.UnavailableReason})";
        VJoyInstallLink.Visibility = _vjoyManager.IsAvailable ? Visibility.Collapsed : Visibility.Visible;

        // Auto-load whatever profile was active last time the app was closed, so renames,
        // calibration, and mappings persist across restarts without an explicit Load click.
        _settings = _settingsStore.Load();
        if (_settings.LastProfileName is { } lastName)
        {
            try { _currentProfile = _profileStore.Load(lastName); }
            catch (IOException) { /* profile file missing/moved; fall back to a blank profile */ }
        }

        // Restore each Raw Input device's learned button set before the first refresh, so its
        // button count/numbering is already correct instead of starting at zero known buttons
        // and waiting for every key to be pressed again this session.
        SyncRawInputWithProfile();

        _engine.LoadProfile(_currentProfile);
        RefreshDevices();
        RebuildLayoutPages();
        RefreshProfileList();
        _timer.Start();

        _autosaveTimer.Tick += (_, _) => AutoSaveCurrentProfile();
        _autosaveTimer.Start();
        _gameWatchTimer.Tick += (_, _) => CheckGameProfiles();
        _gameWatchTimer.Start();
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
        _autosaveTimer.Stop();
        _gameWatchTimer.Stop();
        AutoSaveCurrentProfile();
        RememberCurrentProfile();

        _engine.LoadProfile(new Profile());
        _deviceManager.Dispose();
        _rawInputManager.Dispose();
        _streamDockManager.Dispose();
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
        AddTrayKeyboardItem(menu);
        AddTrayStartupItem(menu);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
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

    private AppSettings _settings = new();
    private string? _lastSavedJson;
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Silently persists the current profile (including renames typed into Live
    /// Monitor) whenever anything in it has changed - runs every couple of seconds and on exit,
    /// so nothing is lost if the app is closed any other way. Writes only when the saved JSON
    /// would actually differ.</summary>
    private void AutoSaveCurrentProfile()
    {
        HarvestInputLabels();
        if (string.IsNullOrWhiteSpace(_currentProfile.Name)) _currentProfile.Name = "Default";

        var json = _profileStore.Serialize(_currentProfile);
        if (json == _lastSavedJson) return;

        try
        {
            var isNew = !_profileStore.Exists(_currentProfile.Name);
            BeforeProfileSaved(isNew);
            _profileStore.SaveSerialized(_currentProfile.Name, json);
            _lastSavedJson = json;
            if (_currentProfile.KeyScreenSettings.Any(k => k.ShowNames)) ApplyButtonImages(); // names/mappings may have changed
            if (isNew) RefreshProfileList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort; try again on the next tick.
        }
    }

    /// <summary>Records which profile to load next launch: the current one, or - while a game's
    /// profile was switched in automatically - the one the user had before.</summary>
    private void RememberCurrentProfile()
    {
        _settings.LastProfileName = _profileBeforeGame ?? _currentProfile.Name;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var snapshots = _engine.Tick();

        foreach (var device in Devices)
        {
            if (snapshots.TryGetValue(device.DeviceGuid, out var snapshot)) device.ApplySnapshot(snapshot);
        }

        RefreshKeyStateIfChanged();
        RefreshModeStatus();

        if (IsOutputTabShown)
        {
            // Rebuilt when another profile is switched in (its own Bindings list) or remaps change.
            if (!ReferenceEquals(_outputBuiltFor, _currentProfile.Bindings)) RebuildOutputView();
            else UpdateOutputView();
        }
    }

    private void RefreshDevices()
    {
        // Preserve any unsaved rename edits before we rebuild the view models from scratch.
        HarvestInputLabels();

        var handle = new WindowInteropHelper(this).EnsureHandle();
        _deviceManager.Refresh(handle);
        _rawInputManager.Refresh();
        _streamDockManager.Refresh();

        // Raw Input keyboard-type devices (the real keyboard, any macro pad/streaming panel)
        // start hidden the first time they're ever seen, so Live Monitor isn't flooded with every
        // keyboard-type device on the system - restore just the ones actually wanted via the
        // existing Hidden devices dropdown. Devices already known are left as the user has them.
        foreach (var rawDevice in _rawInputManager.Devices)
        {
            if (_currentProfile.KnownRawInputDeviceGuids.Contains(rawDevice.InstanceGuid)) continue;
            _currentProfile.KnownRawInputDeviceGuids.Add(rawDevice.InstanceGuid);
            _currentProfile.ExcludedDeviceGuids.Add(rawDevice.InstanceGuid);
        }

        HiddenDevices.Clear();
        var visible = new List<DeviceMonitorViewModel>();
        foreach (var device in AllDevices())
        {
            var label = _currentProfile.DeviceLabels.GetValueOrDefault(device.InstanceGuid, device.Name);

            if (_currentProfile.ExcludedDeviceGuids.Contains(device.InstanceGuid))
            {
                HiddenDevices.Add(new HiddenDeviceViewModel(device.InstanceGuid, label));
                continue;
            }

            var screenSettings = _currentProfile.KeyScreenSettings.FirstOrDefault(k => k.DeviceGuid == device.InstanceGuid);
            var vm = new DeviceMonitorViewModel(device.InstanceGuid, device.Name)
            {
                Label = label,
                PresentAxes = device.PresentAxes,
                HasKeyScreens = device is StreamDockDevice { ButtonCount: > 0 } panel && Enumerable.Range(0, panel.ButtonCount).Any(panel.HasKeyScreen),
                ShowKeyNames = screenSettings?.ShowNames ?? false,
                HighlightPressedKeys = screenSettings?.HighlightPressed ?? false,
                ShowKeyState = screenSettings?.ShowState ?? false,
            };
            // After the initial values, so only the user's changes (by mouse, keyboard or screen reader) count.
            vm.PropertyChanged += KeyScreenOption_Changed;

            if (device is RawInputMouseDevice or RawInputCombinedDevice)
            {
                AddAxisModeRow(vm, device.InstanceGuid, AxisType.X, "X Movement");
                AddAxisModeRow(vm, device.InstanceGuid, AxisType.Y, "Y Movement");
                AddAxisModeRow(vm, device.InstanceGuid, AxisType.Z, "Wheel / Dial");
            }

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
                var defaultButtonLabel = device.ButtonDeviceNames.GetValueOrDefault(i, $"{i + 1}");
                if (IsControlExcluded(device.InstanceGuid, ControlKind.Button, i))
                {
                    vm.HiddenControls.Add(new HiddenControlViewModel(ControlKind.Button, i, GetSavedLabel(device.InstanceGuid, ControlKind.Button, i, defaultButtonLabel)));
                    continue;
                }
                vm.Buttons.Add(new ButtonReadingViewModel(device.InstanceGuid, i)
                {
                    Label = defaultButtonLabel,
                    HasScreen = device is StreamDockDevice streamDock && streamDock.HasKeyScreen(i),
                });
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

        ApplyButtonImages();
        RebuildCalibrationRows();
        RebuildLayoutMarkers();
        RebuildMappingList();

        // The list was rebuilt, so the "Add back" picker lost its selection; show the first again.
        if (HiddenDevices.Count > 0) HiddenDevicesCombo.SelectedIndex = 0;
    }

    /// <summary>Builds one axis-mode row (see AxisModeRowViewModel) for a Raw Input mouse-style
    /// device's native axis, pre-selecting whatever option matches its currently configured mode.</summary>
    private void AddAxisModeRow(DeviceMonitorViewModel vm, Guid deviceGuid, AxisType nativeAxis, string label)
    {
        var row = new AxisModeRowViewModel(deviceGuid, nativeAxis, label);
        var current = _rawInputManager.GetAxisMode(deviceGuid, nativeAxis);
        row.SelectedOption = AxisModeRowViewModel.Options.FirstOrDefault(o =>
            o.Mode == current.Mode && (o.Mode == AxisReportMode.Buttons || o.ReportAs == current.ReportAs))
            ?? AxisModeRowViewModel.Options[0];
        vm.AxisModeRows.Add(row);
    }

    /// <summary>Persists a user-driven change to how a Raw Input mouse-style device's native axis
    /// is exposed. Guarded by e.RemovedItems: the ComboBox also raises this when a row's
    /// SelectedOption is first set from code (building the row in AddAxisModeRow above), which has
    /// no previous selection to report as removed - only react to an actual change away from some
    /// prior selection, or setting the initial value here would recurse into RefreshDevices()
    /// forever as each rebuild re-triggers the "initial" selection on its brand new ComboBoxes.</summary>
    private void AxisMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0) return;
        if (sender is not FrameworkElement { DataContext: AxisModeRowViewModel row }) return;
        if (row.SelectedOption is not { } option) return;

        _rawInputManager.SetAxisMode(row.DeviceGuid, row.NativeAxis, option.Mode, option.ReportAs);

        _currentProfile.AxisModes.RemoveAll(m => m.DeviceGuid == row.DeviceGuid && m.NativeAxis == row.NativeAxis);
        _currentProfile.AxisModes.Add(new AxisModeEntry { DeviceGuid = row.DeviceGuid, NativeAxis = row.NativeAxis, Mode = option.Mode, ReportAs = option.ReportAs });

        RefreshDevices();
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

    private const string ImageFileFilter =
        "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico|All files (*.*)|*.*";

    /// <summary>Right-click "Set image..." on a button that has its own screen (a StreamDock key).</summary>
    private void SetButtonImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ButtonReadingViewModel { HasScreen: true } button }) return;

        var dialog = new OpenFileDialog { Title = $"Picture for '{button.Label}'", Filter = ImageFileFilter };
        if (dialog.ShowDialog(this) != true) return;

        SetButtonImage(button, dialog.FileName);
    }

    private void ClearButtonImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ButtonReadingViewModel button }) return;

        UpdateButtonImageEntry(button, entry => entry.Jpeg = null);
    }

    /// <summary>Right-click "Set pressed image..." - shown instead while the key is held.</summary>
    private void SetPressedButtonImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ButtonReadingViewModel { HasScreen: true } button }) return;

        var dialog = new OpenFileDialog { Title = $"Picture for '{button.Label}' while pressed", Filter = ImageFileFilter };
        if (dialog.ShowDialog(this) != true) return;

        SetButtonImage(button, dialog.FileName, pressed: true);
    }

    private void ClearPressedButtonImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ButtonReadingViewModel button }) return;

        UpdateButtonImageEntry(button, entry => entry.PressedJpeg = null);
    }

    /// <summary>The key-screen switches on a StreamDock panel's card ("Show names", "Light up while
    /// pressed", "Show latched keys and the shift layer"), via their two-way bindings.</summary>
    private void KeyScreenOption_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not DeviceMonitorViewModel device) return;
        if (e.PropertyName is not (nameof(DeviceMonitorViewModel.ShowKeyNames) or nameof(DeviceMonitorViewModel.HighlightPressedKeys)
            or nameof(DeviceMonitorViewModel.ShowKeyState))) return;

        var settings = _currentProfile.KeyScreenSettings.FirstOrDefault(k => k.DeviceGuid == device.DeviceGuid);
        if (settings is null)
        {
            settings = new KeyScreenSettings { DeviceGuid = device.DeviceGuid };
            _currentProfile.KeyScreenSettings.Add(settings);
        }
        settings.ShowNames = device.ShowKeyNames;
        settings.HighlightPressed = device.HighlightPressedKeys;
        settings.ShowState = device.ShowKeyState;
        if (!settings.ShowNames && !settings.HighlightPressed && !settings.ShowState) _currentProfile.KeyScreenSettings.Remove(settings);
        ApplyButtonImages();
    }

    /// <summary>Changes one key's picture entry, dropping it once it holds no pictures at all.</summary>
    private void UpdateButtonImageEntry(ButtonReadingViewModel button, Action<ButtonImageEntry> change)
    {
        var entry = _currentProfile.ButtonImages.LastOrDefault(i => i.DeviceGuid == button.DeviceGuid && i.ButtonIndex == button.Index);
        if (entry is null)
        {
            entry = new ButtonImageEntry { DeviceGuid = button.DeviceGuid, ButtonIndex = button.Index };
            _currentProfile.ButtonImages.Add(entry);
        }
        _currentProfile.ButtonImages.RemoveAll(i => i.DeviceGuid == button.DeviceGuid && i.ButtonIndex == button.Index && i != entry);
        change(entry);
        if (entry.Jpeg is null && entry.PressedJpeg is null) _currentProfile.ButtonImages.Remove(entry);
        ApplyButtonImages();
    }

    private void SetButtonImage(ButtonReadingViewModel button, string path, bool pressed = false)
    {
        byte[] jpeg;
        try
        {
            jpeg = KeyImages.FromFile(path, StreamDockDevice.KeyImageSize);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't use that file as a picture:\n{ex.Message}", "Set image", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdateButtonImageEntry(button, entry =>
        {
            if (pressed) entry.PressedJpeg = jpeg;
            else entry.Jpeg = jpeg;
        });
    }

    // Generated key pictures, reused while their text/source doesn't change (so a redraw only
    // sends keys whose picture really changed).
    private readonly Dictionary<string, byte[]> _namePictures = [];
    private readonly Dictionary<byte[], byte[]> _highlightPictures = new(ReferenceEqualityComparer.Instance);
    private byte[]? _blankHighlight;

    /// <summary>Sends the current profile's button pictures to every connected StreamDock panel
    /// (each only redraws the keys that changed) and shows them on the Live Monitor tiles. Keys
    /// with no picture of their own get a generated name picture / pressed highlight if the
    /// panel's options ask for one.</summary>
    private void ApplyButtonImages()
    {
        if (_namePictures.Count > 500) _namePictures.Clear();
        if (_highlightPictures.Count > 500) _highlightPictures.Clear();

        // What the keys showing state reflect - remembered so Timer_Tick redraws only when it changes.
        var shiftHeld = _engine.ShiftHeld;
        var latched = _engine.LatchedSlots();
        _shownShiftHeld = shiftHeld;
        _shownLatchVersion = _engine.LatchVersion;

        var shown = new Dictionary<(Guid, int), (byte[]? Normal, byte[]? Pressed)>();
        foreach (var device in _streamDockManager.Devices.OfType<StreamDockDevice>())
        {
            var settings = _currentProfile.KeyScreenSettings.FirstOrDefault(k => k.DeviceGuid == device.InstanceGuid);
            var showState = settings?.ShowState == true;
            var deviceVm = Devices.FirstOrDefault(d => d.DeviceGuid == device.InstanceGuid);
            var images = new Dictionary<int, byte[]>();
            var pressedImages = new Dictionary<int, byte[]>();
            for (var index = 0; index < device.ButtonCount; index++)
            {
                if (!device.HasKeyScreen(index)) continue;
                var entry = _currentProfile.ButtonImages.LastOrDefault(i => i.DeviceGuid == device.InstanceGuid && i.ButtonIndex == index);

                var normal = entry?.Jpeg;
                if (normal is null && settings?.ShowNames == true)
                {
                    var name = deviceVm?.FindControl(ControlKind.Button, index)?.Label
                               ?? GetSavedLabel(device.InstanceGuid, ControlKind.Button, index, device.ButtonDeviceNames.GetValueOrDefault(index, $"{index + 1}"));
                    var mapping = showState && shiftHeld
                        ? DescribeShiftLayer(device.InstanceGuid, ControlKind.Button, index, InputPart.Whole)
                        : DescribeMappings(device.InstanceGuid, ControlKind.Button, index, InputPart.Whole);
                    var key = name + "\n" + mapping;
                    if (!_namePictures.TryGetValue(key, out normal))
                    {
                        normal = KeyImages.FromText(name.Trim(), mapping, StreamDockDevice.KeyImageSize);
                        _namePictures[key] = normal;
                    }
                }

                var pressed = entry?.PressedJpeg;
                if (pressed is null && settings?.HighlightPressed == true)
                {
                    if (normal is null) pressed = _blankHighlight ??= KeyImages.Highlight(null, StreamDockDevice.KeyImageSize);
                    else if (!_highlightPictures.TryGetValue(normal, out pressed))
                    {
                        pressed = KeyImages.Highlight(normal, StreamDockDevice.KeyImageSize);
                        _highlightPictures[normal] = pressed;
                    }
                }

                // A latched key stays lit (its picture with the pressed frame) until it's unlatched.
                if (showState && latched.Contains((device.InstanceGuid, ControlKind.Button, index, InputPart.Whole)))
                {
                    normal = LitPicture(normal);
                }

                if (normal is not null) images[index] = normal;
                if (pressed is not null) pressedImages[index] = pressed;
                shown[(device.InstanceGuid, index)] = (normal, pressed);
            }
            device.SetButtonImages(images, pressedImages);
        }

        foreach (var device in Devices)
        {
            foreach (var button in device.Buttons.Where(b => b.HasScreen))
            {
                var entry = _currentProfile.ButtonImages.LastOrDefault(i => i.DeviceGuid == device.DeviceGuid && i.ButtonIndex == button.Index);
                button.HasOwnImage = entry?.Jpeg is not null;
                button.HasOwnPressedImage = entry?.PressedJpeg is not null;
                var (normal, pressed) = shown.GetValueOrDefault((device.DeviceGuid, button.Index));
                button.Image = normal is null ? null : KeyImages.Decode(normal);
                button.PressedImage = pressed is null ? null : KeyImages.Decode(pressed);
            }
        }
    }

    private bool _shownShiftHeld;
    private int _shownLatchVersion = -1;

    private byte[] LitPicture(byte[]? normal)
    {
        if (normal is null) return _blankHighlight ??= KeyImages.Highlight(null, StreamDockDevice.KeyImageSize);
        if (!_highlightPictures.TryGetValue(normal, out var lit))
        {
            lit = KeyImages.Highlight(normal, StreamDockDevice.KeyImageSize);
            _highlightPictures[normal] = lit;
        }
        return lit;
    }

    /// <summary>What a control does while shift is held: its shifted mappings (marked ⇧), or its
    /// normal ones if it has none - those keep working in the shift layer.</summary>
    private string DescribeShiftLayer(Guid deviceGuid, ControlKind kind, int index, InputPart part)
    {
        var remaps = GetRemaps(deviceGuid, kind, index, part);
        var shifted = remaps.Where(r => r.Shifted).Select(r => r.Description).ToList();
        if (shifted.Count > 0) return "⇧ " + string.Join(", ", shifted);
        return DescribeMappings(deviceGuid, kind, index, part);
    }

    /// <summary>Redraws StreamDock keys when the shift control or a latch changes, for panels
    /// set to show that.</summary>
    private void RefreshKeyStateIfChanged()
    {
        if (!_currentProfile.KeyScreenSettings.Any(k => k.ShowState)) return;
        if (_engine.ShiftHeld == _shownShiftHeld && _engine.LatchVersion == _shownLatchVersion) return;
        ApplyButtonImages();
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

        _currentProfile.Bindings.RemoveAll(b => b.Source.DeviceGuid == device.DeviceGuid);
        _currentProfile.KeyBindings.RemoveAll(k => k.Source.DeviceGuid == device.DeviceGuid);
        if (_currentProfile.ShiftControl?.DeviceGuid == device.DeviceGuid) _currentProfile.ShiftControl = null;
        _engine.LoadProfile(_currentProfile);

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
            ButtonImages = _currentProfile.ButtonImages.Where(i => i.DeviceGuid == guid).ToList(),
            KeyScreenSettings = _currentProfile.KeyScreenSettings.Where(k => k.DeviceGuid == guid).ToList(),
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

        // Full copies, so every setting (part, mode, layer, trigger, zone, sequence...) comes along.
        foreach (var binding in imported.Bindings.Where(b => b.Source.DeviceGuid == sourceGuid))
        {
            _currentProfile.Bindings.Add(ModelCopy.WithSourceDevice(binding, targetGuid));
        }

        foreach (var keyBinding in imported.KeyBindings.Where(k => k.Source.DeviceGuid == sourceGuid))
        {
            _currentProfile.KeyBindings.Add(ModelCopy.WithSourceDevice(keyBinding, targetGuid));
        }

        foreach (var image in imported.ButtonImages.Where(i => i.DeviceGuid == sourceGuid))
        {
            _currentProfile.ButtonImages.RemoveAll(i => i.DeviceGuid == targetGuid && i.ButtonIndex == image.ButtonIndex);
            _currentProfile.ButtonImages.Add(new ButtonImageEntry { DeviceGuid = targetGuid, ButtonIndex = image.ButtonIndex, Jpeg = image.Jpeg, PressedJpeg = image.PressedJpeg });
        }

        foreach (var screen in imported.KeyScreenSettings.Where(k => k.DeviceGuid == sourceGuid))
        {
            _currentProfile.KeyScreenSettings.RemoveAll(k => k.DeviceGuid == targetGuid);
            _currentProfile.KeyScreenSettings.Add(new KeyScreenSettings { DeviceGuid = targetGuid, ShowNames = screen.ShowNames, HighlightPressed = screen.HighlightPressed });
        }

        _engine.LoadProfile(_currentProfile);
        RefreshDevices();
    }

    private static Guid? FindAnyDeviceGuid(Profile profile)
    {
        if (profile.DeviceLabels.Count > 0) return profile.DeviceLabels.Keys.First();
        if (profile.InputLabels.Count > 0) return profile.InputLabels[0].DeviceGuid;
        if (profile.AxisCalibrations.Count > 0) return profile.AxisCalibrations[0].DeviceGuid;
        if (profile.ExcludedControls.Count > 0) return profile.ExcludedControls[0].DeviceGuid;
        if (profile.Bindings.Count > 0) return profile.Bindings[0].Source.DeviceGuid;
        if (profile.KeyBindings.Count > 0) return profile.KeyBindings[0].Source.DeviceGuid;
        if (profile.ButtonImages.Count > 0) return profile.ButtonImages[0].DeviceGuid;
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

    private Point _buttonDragStartPoint;

    /// <summary>Only meaningful for Raw Input keyboard-type devices (where button order is just
    /// discovery order and can be fixed up); harmless no-op if dragged elsewhere or dropped on a
    /// device whose button order isn't reorderable (ButtonLamp_Drop checks that).</summary>
    private void ButtonLamp_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _buttonDragStartPoint = e.GetPosition(null);
    }

    private void ButtonLamp_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { DataContext: ButtonReadingViewModel button } element) return;
        if (_rawInputManager.GetKnownVKeys(button.DeviceGuid).Count == 0) return; // not reorderable

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _buttonDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _buttonDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(element, button, DragDropEffects.Move);
    }

    private void ButtonLamp_Drop(object sender, DragEventArgs e)
    {
        // A picture file dragged in from Explorer onto a key that has its own screen.
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (sender is FrameworkElement { DataContext: ButtonReadingViewModel { HasScreen: true } screenButton } &&
                e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                SetButtonImage(screenButton, files[0]);
            }
            return;
        }

        if (!e.Data.GetDataPresent(typeof(ButtonReadingViewModel))) return;
        if (sender is not FrameworkElement { DataContext: ButtonReadingViewModel targetButton }) return;
        if (e.Data.GetData(typeof(ButtonReadingViewModel)) is not ButtonReadingViewModel draggedButton) return;
        if (ReferenceEquals(draggedButton, targetButton)) return;
        if (draggedButton.DeviceGuid != targetButton.DeviceGuid) return;

        var deviceGuid = draggedButton.DeviceGuid;
        var currentOrder = _rawInputManager.GetKnownVKeys(deviceGuid);
        if (currentOrder.Count == 0) return; // not a reorderable Raw Input keyboard device

        var oldIndex = draggedButton.Index;
        var newIndex = targetButton.Index;
        if (oldIndex < 0 || oldIndex >= currentOrder.Count || newIndex < 0 || newIndex >= currentOrder.Count) return;

        var reordered = currentOrder.ToList();
        var moved = reordered[oldIndex];
        reordered.RemoveAt(oldIndex);
        reordered.Insert(newIndex, moved);

        _rawInputManager.ReorderKnownKeys(deviceGuid, reordered);

        _currentProfile.RawInputLearnedKeys.RemoveAll(k => k.DeviceGuid == deviceGuid);
        foreach (var vkey in reordered) _currentProfile.RawInputLearnedKeys.Add(new RawInputLearnedKey { DeviceGuid = deviceGuid, VKey = vkey });

        RefreshDevices();
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

    /// <summary>Common tail end of switching to a newly-loaded-or-imported profile: wire it into
    /// the engine, apply its device/control renames to whatever's currently connected, and rebuild
    /// every dependent view.</summary>
    private void ApplyLoadedProfile(Profile profile)
    {
        _currentProfile = profile;
        SyncRawInputWithProfile();
        _engine.LoadProfile(_currentProfile);

        // A full rebuild: the profile decides which devices/controls are hidden, their order,
        // names, StreamDock options and pictures - not just the labels. RefreshDevices also
        // rebuilds calibration rows, key pictures, layout markers and the mappings list.
        RefreshDevices();
        RebuildLayoutPages();
        RefreshProfileList();
        UpdateUndoButtons();
    }

    /// <summary>Makes Raw Input keypads number their buttons the way the current profile does
    /// (keys it doesn't know yet keep their place after its own), records the full numbering
    /// back into the profile so it's saved with it, and applies its axis modes.</summary>
    private void SyncRawInputWithProfile()
    {
        foreach (var group in _currentProfile.RawInputLearnedKeys.GroupBy(k => k.DeviceGuid))
        {
            var order = group.Select(k => k.VKey).Distinct().ToList();
            order.AddRange(_rawInputManager.GetKnownVKeys(group.Key).Where(v => !order.Contains(v)));
            _rawInputManager.SeedKnownKeys(group.Key, order);
            _rawInputManager.ReorderKnownKeys(group.Key, order);
        }

        foreach (var deviceGuid in _rawInputManager.DevicesWithKnownKeys)
        {
            foreach (var vkey in _rawInputManager.GetKnownVKeys(deviceGuid))
            {
                if (!_currentProfile.RawInputLearnedKeys.Any(k => k.DeviceGuid == deviceGuid && k.VKey == vkey))
                {
                    _currentProfile.RawInputLearnedKeys.Add(new RawInputLearnedKey { DeviceGuid = deviceGuid, VKey = vkey });
                }
            }
        }

        _rawInputManager.ReplaceAxisModes(_currentProfile.AxisModes);
    }
}
