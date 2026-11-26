# GoMic Tray

A tiny Windows system-tray app for the **Samson Go Mic** (USB `17A0:0305`), which has hardware direct monitoring but no Windows control app.

- **Left-click** the tray icon: toggle direct monitoring (green dot = on, red dot = off)
- **Right-click**: mic gain slider (hardware range, e.g. -14..+22 dB), monitor/headphone volume, mute mic, start with Windows, exit
- Remembers your settings and re-applies them when the mic is reconnected
- No driver, no admin rights; talks to the mic's own controls through the Windows Core Audio device topology

## Download

Grab `GoMicTray.exe` from the [Releases](../../releases) page and run it. Requires Windows 10/11 (uses the built-in .NET Framework 4.8).
Enable **Start with Windows** from the right-click menu to have it launch at login.

## Build

```
dotnet build GoMicTray.csproj -c Release -o out
```

Releases are built by GitHub Actions when a `v*` tag is pushed.

## How it works

The mic's USB audio topology exposes `Mic -> ADC -> Mute -> Volume` (capture gain) and `Mic -> ADC -> Mute -> SuperMix -> Sum -> Volume -> DAC` (monitor path). The monitor `Mute` node is the direct-monitoring switch.
