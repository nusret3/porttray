# PortTray

A tiny Windows tray app that shows which serial (COM) ports exist right now — with
device name, VID:PID and USB serial number — and copies a port name to the clipboard
with one click. Handy when you juggle several USB-serial adapters and keep wondering
*"which COM did that board get this time?"*

Two independent implementations are included:

| File | Stack | Notes |
| --- | --- | --- |
| [PortTray.cs](PortTray.cs) | C# / .NET Framework 4.x, WinForms | No dependencies, no SDK needed, ~16 KB exe. The recommended one. |
| [porttray.py](porttray.py) | Python + `pystray` + `pyserial` + `pillow` | Same idea, for anyone who prefers to hack on Python. |

## Features

- Tray menu listing every COM port, sorted numerically, e.g.
  `COM7    USB Serial Port  [0403:6001]  SN A50285BI`
- Click a port to copy `COMx` to the clipboard.
- Hover tooltip on each entry shows the manufacturer and full PNP device ID.
- Balloon notification when a port appears or disappears (`COM7 connected`,
  `COM7 removed`, or a combined list). Toggleable from the menu.
- Tray tooltip shows the current ports at a glance.
- **Start with Windows** toggle (C# version; writes `HKCU\...\CurrentVersion\Run`).
- **Open Device Manager** shortcut.
- Opens on left *and* right click; single instance only (guarded by a named mutex).
- Picks up ports that WMI does not list under *Ports (COM & LPT)* — com0com,
  some Bluetooth and virtual drivers — by cross-checking `SerialPort.GetPortNames()`.
- Icon is a DB9 connector drawn in code, so there is no `.ico` file to ship.

## Build & run (C# version)

No Visual Studio, no .NET SDK — it compiles with the `csc.exe` that ships inside
Windows itself:

```bat
build.bat
```

That calls `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, produces
`PortTray.exe` next to the source and launches it.

Don't want to build it? Grab `PortTray.exe` from the
[latest release](../../releases/latest). It is unsigned, so SmartScreen will likely
want a *More info → Run anyway* — or just run `build.bat` and compile it yourself in
a second.

The source is deliberately written in C# 5 style (no string interpolation, no `?.`)
because the in-box compiler is a C# 5 compiler. Keep it that way if you edit it.

Referenced assemblies: `System.Management`, `System.Windows.Forms`, `System.Drawing`.

## Run (Python version)

```sh
pip install pystray pyserial pillow
pythonw porttray.py        # pythonw = no console window
```

## How port detection works

The C# version queries WMI for `Win32_PnPEntity` rows in the *Ports (COM & LPT)*
device class, pulls `COMx` out of the friendly name, and parses `VID_xxxx`/`PID_xxxx`
plus the serial number out of the PNP device ID. FTDI devices get special treatment:
`FTDIBUS\VID_0403+PID_6001+A50285BIA\0000` carries the chip serial with a trailing
channel letter, which is stripped. For plain `USB\...` IDs, an instance ID containing
`&` is one Windows made up rather than a real serial, so it is ignored.

Changes are driven by `WM_DEVICECHANGE` broadcasts to a hidden window, debounced by
800 ms so the driver has finished enumerating before the rescan. The Python version
polls every 1.5 s instead.

## Requirements

- Windows (7 or newer in practice; tested on Windows 11)
- .NET Framework 4.x for the C# version — present on every modern Windows install
- Python 3 for the Python version

## License

[MIT](LICENSE) — © 2026 Nusret Süzme
