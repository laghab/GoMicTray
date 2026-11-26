# GoMic Tray

A tiny Windows system-tray app for the **Samson Go Mic** (USB `17A0:0305`), which has hardware direct monitoring but no Windows control app.

| Action | What it does |
|---|---|
| **Click** the tray icon | Mute / unmute the mic at the hardware level (the PC stops hearing it) |
| **Double-click** the tray icon | Turn direct monitoring on / off |
| **Right-click** | Menu: monitoring, mic mute, monitor volume slider, reset volume, start with Windows, exit |

- The monitor volume slider is shown relative to a default level (`-30 dB`): go quieter or louder from there. **Reset volume** returns to the default.
- The app **never reads or changes the mic's gain**; use the switch on the mic.
- Remembers monitoring state and volume and re-applies them when the mic is reconnected.
- Follows the Windows light/dark theme. No driver, no admin rights.

## Download

Grab `GoMicTray.exe` from the [Releases](../../releases) page and run it. Requires Windows 10/11 (uses the built-in .NET Framework 4.8).
Enable **Start with Windows** from the right-click menu to launch at login.

If your PC blocks unsigned executables, run the script form instead: `powershell -STA -ExecutionPolicy Bypass -File GoMicTray.ps1` (it compiles the same C# sources in memory; `-Install` enables autostart).

## Build

```
dotnet build GoMicTray.csproj -c Release -o out
```

Releases are built by GitHub Actions when a `v*` tag is pushed.

## How it works

The mic's USB audio topology exposes a capture path (`Mic -> ADC -> Mute -> Volume`) and a monitor path
(`Mic -> ADC -> Mute -> SuperMix -> Sum -> Volume -> DAC`). The monitor-path `Mute` node is the direct-monitoring switch,
the monitor-path `Volume` is the headphone level, and the capture-path `Mute` is the mic mute.
