# ClaudFlight

A Windows desktop app for monitoring, calibrating, renaming, and remapping flight sim hardware
(joysticks, throttles, switch panels, the mouse) — built as a replacement for clunky vendor
software like Thrustmaster's T.A.R.G.E.T.

It reads your physical devices via DirectInput and can combine several of them into one virtual
controller (via [vJoy](https://github.com/BrunnerInnovation/vJoy/releases)) that games see as a single
joystick, plus optionally simulate keyboard key presses from any axis/button/POV.

## Features

- **Live Monitor** — see every connected device's axes, buttons, and POV hats update in real time.
- **Calibration** — set each axis's min/center/max, deadzone, response curve, and inversion, with
  the effect visible live.
- **Rename anything** — devices, axes, buttons, and POVs can all be given friendly names, kept in
  sync across every tab.
- **Hide/restore** — hide devices or individual controls you don't care about from the views
  without affecting anything already mapped.
- **Drag-to-reorder** device cards in Live Monitor.
- **Layout** — load a picture of your controls (a manual's diagram, a photo, your own schematic)
  and drag a marker for each axis/button/POV onto the matching spot. A hat's four directions and
  an axis's two ends (+/−) get markers of their own, so each can sit on its own spot and be
  remapped separately. Markers light up live as the control is used and show what it's mapped
  to; click one to change what it does (vJoy buttons/axes/hats and keyboard keys, as many at once
  as you like). Add free text labels for titles and notes. Make as many pages as you like (one per
  device, or one for a whole HOTAS); the pictures are saved inside the profile. **Export image...**
  saves a page, markers and all, as a PNG at the background picture's own resolution - a cheat
  sheet to keep on a second screen or use as a kneeboard page - and **Print...** prints it, turned
  and sized to fit the paper (pick *Microsoft Print to PDF* to get a PDF).
- **Split controls** — any single hat direction can press a vJoy button or key (diagonals press
  both neighbouring directions), and either end of an axis can act as a button once it's pushed
  past a set point - handy for things like a throttle detent or a two-way rocker axis.
- **Switch behaviours** — a button output can follow the control (hold), give a short press each
  time it's switched on (pulse), give a short press on every flip on *and* off (for toggle
  switches when the game only has a single toggle binding), or latch (press once for on, again for
  off). The same choices apply to hat directions and axis ends.
- **Tap, hold or double-tap** — each mapping on a button (or hat direction, axis end or zone) can
  fire on a normal press, a short press (tap and let go), a long press (held half a second) or a
  double tap, so one button can do several things.
- **Axis zones** — right-click an axis and choose **Remap zones** to make stretches of its travel
  act as buttons, e.g. 0-5% for idle cut-off and 95-100% for afterburner. Move the axis and click
  **Start here** / **End here** to set a zone where it physically is.
- **Key sequences** — a press can play a short macro, e.g. `G, wait 200, Ctrl+L, Enter` (keys
  separated by commas, held together with `+`, pauses with `wait` and milliseconds).
- **Shift layer** — pick any button (or hat direction, or axis end) as the shift control; while
  it's held, every control with a shifted mapping does that instead. Controls without a shifted
  mapping keep doing their normal thing, so you only set up the extras.
- **Modes** — whole sets of mappings picked by a switch, e.g. Nav, Combat and Landing on a 3-way
  switch (**Modes...** on the Mappings tab). While a mode's switch is on, any control with mappings
  for that mode uses them instead of its usual ones; the rest keep their usual mappings. Each
  mapping's mode is set in its remap window, the top bar shows the active mode, and it can be
  spoken aloud when it changes. The shift layer works inside every mode.
- **Axis trim from buttons** — a button can nudge a vJoy axis up or down (smoothly while held, or
  a step per press), snap it back to center, or put it at a set position (e.g. a flaps detent at
  25%) - for trim wheels, zoom, flaps, or a throttle on a stick with no lever.
- **Spoken latches** — a latching button can say something when it turns on and off ("Gear down" /
  "Gear up"), using Windows' built-in voice.
- **Test** — the remap window's **Test** button sends what you're setting up once, without the
  hardware: presses the vJoy button, sweeps the axis, turns the hat, moves the axis to its preset,
  or (with keyboard keys on) types the key or sequence after a 3 second pause so you can switch to
  the game.
- **Mapping** — combine multiple physical devices into one or more virtual [vJoy](https://github.com/BrunnerInnovation/vJoy/releases)
  controllers that games bind to, and/or have any control press keyboard keys (for games or
  functions that take keyboard input but not joystick input). Every change goes through one remap
  window, which you can open from wherever you are: right-click a control on **Live Monitor**,
  click its marker on the **Layout** tab, or use **Add...** / double-click a line on the
  **Mappings** tab. The Mappings tab lists everything mapped in plain words, filterable by device,
  layer and type, searchable, and sortable by any column. **Map all to vJoy...** on a device's card
  maps every control on that device in one click.
- **Conflict warnings** — a ⚠ marks mappings that fight over one output: a vJoy button, hat or key
  pressed from two different controls, or a vJoy axis driven by two axes (or an axis and a button).
  Pick **Conflicts** in the Mappings tab's type filter to list them; the remap window warns as you
  pick an output that's already in use. Mappings in different modes never count, nor do several
  buttons moving one axis (trim up/down/center).
- **vJoy Output** tab — shows what your mappings are sending right now: every vJoy axis, button
  and hat a game would see, and the last keyboard keys pressed. It works without vJoy installed,
  so you can check mappings before installing it.
- **Keyboard keys** stay off every launch until you tick **Keyboard keys on** at the top of the
  window (or in the tray icon's menu) - see the safety note below.
- **Profiles** — named setups that save automatically as you make changes; the last-used profile
  reloads on startup. The **Profiles** tab lists them all: switch, create, duplicate, rename,
  delete, and Export/Import to a `.json` file anywhere (for backup or sharing). A single device's
  renames/calibration/mappings can also be exported/imported from its card in Live Monitor. The
  dropdown at the bottom of the window switches profile from any tab.
- **Undo and backups** — **Undo**/**Redo** at the bottom of the window (or Ctrl+Z / Ctrl+Y) step
  back through this session's changes. Each profile also keeps up to 30 automatic backups (one at
  most every 10 minutes while it changes); **Backups...** on the Profiles tab puts one back.
- **Per-game profiles** — on the Profiles tab, tell ClaudFlight which profile goes with which
  game; it switches to that profile while the game is running and back to your previous one when
  the game closes. Tick **Keys on** for a game to have keyboard keys turned on while it runs and
  off again when it closes.
- **Runs in the background** — closing or minimizing the window sends it to the system tray
  instead of exiting, so mapping keeps running while you're in a game. Tick **Start with Windows**
  (top of the window or the tray menu) to have it start in the tray when you sign in.

## Requirements

- Windows 10/11 (uses DirectInput and Windows-only APIs).
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or build from source
  with the .NET 9 SDK, which bundles everything needed).
- [vJoy](https://github.com/BrunnerInnovation/vJoy/releases) — **only required if you want to use the
  vJoy mappings** that combine devices into a virtual controller. Live Monitor, Calibration,
  renaming, the Layout tab and keyboard-key mappings all work without it.

## Quick start (new to this kind of tool)

1. **Install vJoy** if you plan to combine multiple devices into one virtual controller. On
   Windows 10/11 use the signed 2.2.2.0 build from
   [BrunnerInnovation/vJoy](https://github.com/BrunnerInnovation/vJoy/releases), or 2.1.9.1 from
   [jshafer817/vJoy](https://github.com/jshafer817/vJoy/releases) if that one won't install; the
   older SourceForge release often fails to install on Windows 11. Skip this if you only want to
   monitor/calibrate/rename your existing hardware, or only want to map controls to keyboard keys.
2. **Run "Configure vJoy"** (installed alongside the driver, from the Start Menu) once, and set how
   many axes/buttons/POVs vJoy device 1 should have. If unsure, enable all 8 axes and a generous
   button count (e.g. 32) — you can change this later.
3. **Build and run ClaudFlight** (see [Building](#building) below), or run the published `.exe` if
   you have one.
4. Open the **Live Monitor** tab, plug in your hardware, and click **Refresh Devices**. Wiggle
   sticks and press buttons to confirm everything shows up.
5. Rename devices/controls to something meaningful (click the label to edit it, or right-click a
   control to hide it if you don't need it).
6. Open **Calibration** and set each axis's center/min/max if the defaults don't feel right.
7. On **Live Monitor**, click **Map all to vJoy...** on a device's card to map every control on it
   in one go, or right-click any single control and choose **Remap...**. The **Mappings** tab then
   lists everything (expand its "What is vJoy?" panel for the full explanation).
8. Open Windows' **"Set up USB game controllers"** (search the Start Menu) to confirm "vJoy Device"
   shows up and responds correctly before jumping into a game.
9. In your flight sim, bind controls to **vJoy Device** instead of your physical hardware.
10. Changes save into the current profile automatically. Use **Duplicate** on the **Profiles** tab
    to keep a copy under another name.

Closing the window doesn't quit the app — it minimizes to the system tray so your mappings keep
running. Right-click the tray icon to reopen the window or fully exit.

## Building

```
dotnet build ClaudFlight.sln
dotnet run --project src/ClaudFlight.App/ClaudFlight.App.csproj
```

Requires the .NET 9 SDK. The solution has three projects:

- `src/ClaudFlight.Core` — device polling (DirectInput/mouse), calibration math, the vJoy and
  keyboard-injection interop, and the mapping engine. No UI dependencies.
- `src/ClaudFlight.App` — the WPF UI.
- `tests/ClaudFlight.Tests` — tests for the mapping engine, run against fake devices and a fake
  vJoy (`dotnet test`), so they need no hardware or driver.

## How mapping works (for anyone unfamiliar with vJoy)

Games can't natively combine several separate physical devices into one controller. **vJoy** is a
driver that creates a *virtual* joystick — as far as Windows and any game is concerned, it's a real
USB controller, except ClaudFlight is the one feeding it values based on the mappings you set up.

A mapping is just: read this control from this physical device (the **source**), write it to this
slot on this vJoy device (the **target**). Map your stick's axes, your throttle lever, and your
switch panel's buttons all onto the same vJoy device number, and your game only needs to see one
combined controller instead of four separate ones.

vJoy supports up to 16 virtual devices (numbered 1-16); almost everyone only ever needs device 1.

## Keyboard keys safety note

Key mappings simulate real keystrokes via Windows' `SendInput` API — the same
mechanism legitimate macro tools use. This is why it's **off by default every time the app
starts** and has to be explicitly re-enabled each session: some anti-cheat software flags
background keyboard injection, so only turn it on for games/situations where that's acceptable.
The one exception is a game you've ticked **Keys on** for on the Profiles tab: keys come on while
that game runs and go off again when it closes - so only tick it for games where that's fine.
Only keys from a curated safe list (letters, numbers, F1-F24, navigation, common punctuation,
modifiers) can ever be simulated, even if a profile file were edited by hand.

## Where things are stored

- Profiles: `%AppData%\ClaudFlight\profiles\*.json`
- Automatic profile backups: `%AppData%\ClaudFlight\profiles\Backups\<profile name>\`
- Last-used profile / app settings: `%AppData%\ClaudFlight\settings.json`

Profile files are plain JSON and safe to inspect, back up, or hand-edit if you know what you're
doing — joysticks/game controllers are identified by their DirectInput instance GUID, and
keyboard-/mouse-type devices (including the system mouse) by a GUID derived from their stable Raw
Input device path. Both are generally stable for the same physical device on the same USB port.
StreamDock panels are identified by a GUID derived from their USB serial number, so they keep
their settings even if moved to a different port.

## Known limitations

- **Macro keypads and streaming panels** that emulate a keyboard or mouse (rather than acting as a
  HID game controller) are supported via a separate Raw Input capture path, which tells each
  physical device apart instead of lumping everything into Windows' single combined
  keyboard/mouse stream. New keyboard-/mouse-type devices start hidden the first time they're
  seen (restore them from the Hidden Devices list in Live Monitor), and a keypad's buttons are
  *learned* as they're pressed rather than assumed from a fixed catalog, so press each button once
  before mapping it.
- **StreamDock panels** (Mirabox/HotSpot devices configured with "VSD Craft", currently the
  **VSD M18**) use a proprietary USB protocol instead of keyboard/joystick reports. ClaudFlight
  reads that protocol directly, so the M18's 15 screen keys and 3 side buttons show up as regular
  buttons ("Key 1"-"Key 15", "Side 1"-"Side 3") that can be renamed, mapped to vJoy, or bound
  to a keystroke. **VSD Craft isn't needed**: ClaudFlight sends the panel the same start-up
  sequence VSD Craft does, which switches its key reporting on. To put a picture on a key's
  screen, right-click the key in Live Monitor and choose **Set image...** (or drop a picture file
  onto it); any common image format works and is fitted onto the key's 64x64 screen. Pictures are
  saved in the profile, travel with a device Export/Import, and are redrawn whenever ClaudFlight
  connects to the panel. **Set pressed image...** gives a key a second picture shown while it's
  held. Each panel's card also has three switches: **Show names on keys without a picture** (the
  key's name and what it's mapped to, updated as you rename or remap), **Light up keys while
  pressed** (a bright frame on keys that have no pressed picture of their own) and **Show latched
  keys and the shift layer** (keys stay lit while a latching mapping on them is on, and named keys
  show their shifted mappings while the shift control is held). If VSD Craft is running anyway, ClaudFlight stays quiet and just listens
  alongside it (VSD Craft's own pictures stay on the keys), and takes over again - redrawing
  ClaudFlight's pictures - when VSD Craft quits. Which key number sits at which physical position isn't documented,
  so press each key in Live Monitor to see its number and rename it. Other StreamDock models can
  be added to the `SupportedDevices` list in `StreamDockManager` once their USB IDs are known.
- Other programmable panels with their own proprietary protocol can't be captured directly. If
  the panel's own software offers a "Hotkey" action, assigning one to each button (with the
  panel's software running in the background) lets ClaudFlight pick up the resulting keystroke
  instead.
- A gaming mouse's extra/macro buttons are often routed through the vendor's own proprietary
  software rather than standard mouse or keyboard input, and won't be seen unless the vendor
  software is configured to send them as one of those.

## License

MIT — see [LICENSE](LICENSE). Use it, modify it, share it, no permission needed.
