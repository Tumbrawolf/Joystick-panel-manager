# ClaudFlight

A Windows desktop app for monitoring, calibrating, renaming, and remapping flight sim hardware
(joysticks, throttles, switch panels, the mouse) — built as a replacement for clunky vendor
software like Thrustmaster's T.A.R.G.E.T.

It reads your physical devices via DirectInput and can combine several of them into one virtual
controller (via [vJoy](https://sourceforge.net/projects/vjoystick/)) that games see as a single
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
- **Mapping** — combine multiple physical devices into one or more virtual [vJoy](https://sourceforge.net/projects/vjoystick/)
  controllers that games bind to, with an "auto-populate" option that maps every control on a
  device in one click.
- **Keyboard Mapping** — simulate a keyboard key press from any axis/button/POV, for
  games/functions that take keyboard input but not joystick input. Off by default every launch;
  you turn it on explicitly per session.
- **Profiles** — save/load named setups; the last-used profile reloads automatically on startup.
  Export/Import a whole profile, or just a single device's renames/calibration/mappings, to/from
  a `.json` file anywhere - for backup or sharing.
- **Runs in the background** — closing or minimizing the window sends it to the system tray
  instead of exiting, so mapping keeps running while you're in a game.

## Requirements

- Windows 10/11 (uses DirectInput and Windows-only APIs).
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or build from source
  with the .NET 9 SDK, which bundles everything needed).
- [vJoy](https://sourceforge.net/projects/vjoystick/) — **only required if you want to use the
  Mapping tab** to combine devices into a virtual controller. Live Monitor, Calibration, renaming,
  and Keyboard Mapping all work without it.

## Quick start (new to this kind of tool)

1. **Install vJoy** from [sourceforge.net/projects/vjoystick](https://sourceforge.net/projects/vjoystick/)
   if you plan to combine multiple devices into one virtual controller. Skip this if you only want
   to monitor/calibrate/rename your existing hardware, or only want the Keyboard Mapping tab.
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
7. Open **Mapping**, expand the "What is vJoy?" panel if you want the full explanation, then either
   add mappings one at a time or use **Auto-populate all controls** to map an entire device onto a
   vJoy device in one click.
8. Open Windows' **"Set up USB game controllers"** (search the Start Menu) to confirm "vJoy Device"
   shows up and responds correctly before jumping into a game.
9. In your flight sim, bind controls to **vJoy Device** instead of your physical hardware.
10. Save your profile (bottom of the window) so it reloads automatically next time.

Closing the window doesn't quit the app — it minimizes to the system tray so your mappings keep
running. Right-click the tray icon to reopen the window or fully exit.

## Building

```
dotnet build ClaudFlight.sln
dotnet run --project src/ClaudFlight.App/ClaudFlight.App.csproj
```

Requires the .NET 9 SDK. The solution has two projects:

- `src/ClaudFlight.Core` — device polling (DirectInput/mouse), calibration math, the vJoy and
  keyboard-injection interop, and the mapping engine. No UI dependencies.
- `src/ClaudFlight.App` — the WPF UI.

## How mapping works (for anyone unfamiliar with vJoy)

Games can't natively combine several separate physical devices into one controller. **vJoy** is a
driver that creates a *virtual* joystick — as far as Windows and any game is concerned, it's a real
USB controller, except ClaudFlight is the one feeding it values based on the mappings you set up.

A mapping is just: read this control from this physical device (the **source**), write it to this
slot on this vJoy device (the **target**). Map your stick's axes, your throttle lever, and your
switch panel's buttons all onto the same vJoy device number, and your game only needs to see one
combined controller instead of four separate ones.

vJoy supports up to 16 virtual devices (numbered 1-16); almost everyone only ever needs device 1.

## Keyboard Mapping safety note

The Keyboard Mapping tab simulates real keystrokes via Windows' `SendInput` API — the same
mechanism legitimate macro tools use. This is why it's **off by default every time the app
starts** and has to be explicitly re-enabled each session: some anti-cheat software flags
background keyboard injection, so only turn it on for games/situations where that's acceptable.
Only keys from a curated safe list (letters, numbers, F1-F24, navigation, common punctuation,
modifiers) can ever be simulated, even if a profile file were edited by hand.

## Where things are stored

- Profiles: `%AppData%\ClaudFlight\profiles\*.json`
- Last-used profile / app settings: `%AppData%\ClaudFlight\settings.json`

Profile files are plain JSON and safe to inspect, back up, or hand-edit if you know what you're
doing — device identity is stored by DirectInput instance GUID, which is generally stable for the
same physical device on the same USB port.

## Known limitations

- **Keyboard-emulating devices** (macro keypads/streaming panels that send keystrokes rather than
  acting as a HID game controller) aren't supported — Windows only exposes one combined keyboard
  stream to DirectInput, so per-device keyboard *capture* isn't possible without a much larger
  separate input pipeline (Raw Input). Keyboard *output* (Keyboard Mapping tab) is unaffected by
  this; it's only *reading* a keyboard-type device that's unsupported.
- A gaming mouse's extra/macro buttons are often routed through the vendor's own proprietary
  software rather than standard mouse or keyboard input, and won't be seen unless the vendor
  software is configured to send them as one of those.

## License

MIT — see [LICENSE](LICENSE). Use it, modify it, share it, no permission needed.
