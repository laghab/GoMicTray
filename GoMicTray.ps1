<#
  GoMic Tray - system tray control for the Samson Go Mic (USB 17A0:0305)
    * Left-click tray icon  : toggle direct monitoring on/off
    * Right-click tray icon : menu with mic gain slider, monitor volume, mute, autostart, exit
  Talks to the mic's own hardware controls through the Windows Core Audio device topology.
#>
param([switch]$Test, [switch]$Install, [switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$AppName   = 'GoMic Tray'
$ScriptPath = $MyInvocation.MyCommand.Path
$StartupLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\GoMic Tray.lnk'
$SettingsDir  = Join-Path $env:APPDATA 'GoMicTray'
$SettingsFile = Join-Path $SettingsDir 'settings.json'

# ---------------------------------------------------------------- COM interop
$cs = @'
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace GoMicNative {
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDevEnumComObj {}
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
  void EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { void GetCount(out int n); void Item(int i, out IMMDevice d); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
  void Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  void OpenPropertyStore(int a, out IntPtr ps);
  void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}
[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology {
  void GetConnectorCount(out int n);
  void GetConnector(int i, out IConnector c);
  void GetSubunitCount(out int n);
  void GetSubunit(int i, out IPart p);
  void GetPartById(int id, out IPart p);
  void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  void GetSignalPath(IPart a, IPart b, bool rejectMixed, out IPartsList l);
}
[ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector {
  void GetType(out int t);
  void GetDataFlow(out int f);
  void ConnectTo(IConnector c);
  void Disconnect();
  void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool b);
  void GetConnectedTo(out IConnector c);
  void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
  void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}
[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPart {
  void GetName([MarshalAs(UnmanagedType.LPWStr)] out string n);
  void GetLocalId(out int id);
  void GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  void GetPartType(out int t);
  void GetSubType(out Guid g);
  void GetControlInterfaceCount(out int n);
  void GetControlInterface(int i, out IControlInterface c);
  void EnumPartsIncoming(out IPartsList l);
  void EnumPartsOutgoing(out IPartsList l);
  void GetTopologyObject(out IDeviceTopology t);
  void Activate(int ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  void RegisterControlChangeCallback(ref Guid iid, IntPtr cb);
  void UnregisterControlChangeCallback(IntPtr cb);
}
[ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPartsList { void GetCount(out int n); void GetPart(int i, out IPart p); }
[ComImport, Guid("45D37C3F-5140-444A-AE24-400789F3CBF3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IControlInterface { void GetName([MarshalAs(UnmanagedType.LPWStr)] out string n); void GetIID(out Guid g); }

[ComImport, Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMute {
  void SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
  void GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
}
[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioVolumeLevel {
  void GetChannelCount(out int n);
  void GetLevelRange(int ch, out float min, out float max, out float step);
  void GetLevel(int ch, out float db);
  void SetLevel(int ch, float db, ref Guid ctx);
  void SetLevelUniform(float db, ref Guid ctx);
  void SetLevelAllChannels([MarshalAs(UnmanagedType.LPArray)] float[] l, int n, ref Guid ctx);
}

public class GoMic {
  static readonly Guid IID_Topo = new Guid("2A07407E-6497-4A18-9787-32F79BD0D98F");
  static readonly Guid IID_Mute = new Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E");
  static readonly Guid IID_Vol  = new Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC");
  const int CLSCTX_ALL = 23;
  Guid ctx = Guid.Empty;
  IAudioMute monMute, capMute;
  IAudioVolumeLevel micVol, hpVol;
  public string VidMatch = "vid_17a0";   // Samson
  public bool Connected { get { return monMute != null && micVol != null; } }
  public bool HasMonitorVolume { get { return hpVol != null; } }
  public bool HasMicMute { get { return capMute != null; } }

  static List<IPart> Children(IPart p) {
    var r = new List<IPart>();
    try { IPartsList l; p.EnumPartsOutgoing(out l); if (l == null) return r;
      int n; l.GetCount(out n);
      for (int i = 0; i < n; i++) { IPart q; l.GetPart(i, out q); r.Add(q); } } catch { }
    return r;
  }
  static List<IPart> Chain(IPart p) {
    var r = new List<IPart>(); int guard = 0;
    while (p != null && guard++ < 32) {
      r.Add(p);
      var c = Children(p);
      p = c.Count > 0 ? c[0] : null;
    }
    return r;
  }
  static bool Has(IPart p, Guid iid) {
    int n; p.GetControlInterfaceCount(out n);
    for (int i = 0; i < n; i++) { IControlInterface c; p.GetControlInterface(i, out c); Guid g; c.GetIID(out g); if (g == iid) return true; }
    return false;
  }
  static string NameOf(IPart p) { string s; p.GetName(out s); return s ?? ""; }

  public void Disconnect() { monMute = capMute = null; micVol = hpVol = null; }

  public bool Connect() {
    Disconnect();
    try {
      var en = (IMMDeviceEnumerator)new MMDevEnumComObj();
      IMMDeviceCollection col; en.EnumAudioEndpoints(1 /*capture*/, 1 /*active*/, out col);
      int n; col.GetCount(out n);
      for (int i = 0; i < n; i++) {
        IMMDevice d; col.Item(i, out d);
        object o; Guid iid = IID_Topo; d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
        var topo = (IDeviceTopology)o;
        IConnector con; topo.GetConnector(0, out con);
        string devId; con.GetDeviceIdConnectedTo(out devId);
        if (devId == null || devId.IndexOf(VidMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;
        IConnector adapterCon; con.GetConnectedTo(out adapterCon);
        IPart mic = (IPart)adapterCon;
        // mic -> ADC -> { capture branch (Mute->Volume->Mux...) , monitor branch (Mute->SuperMix->Sum->Mute->Volume->DAC) }
        foreach (var adc in Children(mic)) {
          foreach (var branch in Children(adc)) {
            var ch = Chain(branch);
            bool isMonitor = false; int sumIdx = -1;
            for (int k = 0; k < ch.Count; k++) {
              string nm = NameOf(ch[k]);
              if (nm == "SuperMix" || nm == "Sum") isMonitor = true;
              if (nm == "Sum" && sumIdx < 0) sumIdx = k;
            }
            if (isMonitor) {
              if (Has(ch[0], IID_Mute)) monMute = Act<IAudioMute>(ch[0], IID_Mute);
              for (int k = Math.Max(sumIdx, 0); k < ch.Count; k++)
                if (Has(ch[k], IID_Vol)) { hpVol = Act<IAudioVolumeLevel>(ch[k], IID_Vol); break; }
            } else {
              if (Has(ch[0], IID_Mute)) capMute = Act<IAudioMute>(ch[0], IID_Mute);
              foreach (var p in ch) if (Has(p, IID_Vol)) { micVol = Act<IAudioVolumeLevel>(p, IID_Vol); break; }
            }
          }
        }
        if (Connected) return true;
        Disconnect();
      }
    } catch { Disconnect(); }
    return false;
  }
  static T Act<T>(IPart p, Guid iid) { object o; p.Activate(CLSCTX_ALL, ref iid, out o); return (T)o; }

  bool GetMute(IAudioMute m) { bool b; m.GetMute(out b); return b; }
  public bool MonitorOn { get { return !GetMute(monMute); } set { monMute.SetMute(!value, ref ctx); } }
  public bool MicMuted  { get { return capMute != null && GetMute(capMute); } set { if (capMute != null) capMute.SetMute(value, ref ctx); } }

  static float Lvl(IAudioVolumeLevel v) { float f; v.GetLevel(0, out f); return f; }
  static void Range(IAudioVolumeLevel v, out float mn, out float mx, out float st) { v.GetLevelRange(0, out mn, out mx, out st); }
  public float MicGainDb { get { return Lvl(micVol); } set { micVol.SetLevelUniform(value, ref ctx); } }
  public void MicRange(out float mn, out float mx, out float st) { Range(micVol, out mn, out mx, out st); }
  public float MonitorVolDb { get { return Lvl(hpVol); } set { hpVol.SetLevelUniform(value, ref ctx); } }
  public void MonitorRange(out float mn, out float mx, out float st) { Range(hpVol, out mn, out mx, out st); }

  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
}
}
'@
Add-Type -TypeDefinition $cs -Language CSharp
$mic = New-Object GoMicNative.GoMic

# ---------------------------------------------------------------- settings
function Load-Settings {
    $s = [pscustomobject]@{ monitor = $null; gainDb = $null; monitorVolDb = $null }
    try { if (Test-Path $SettingsFile) { $j = Get-Content $SettingsFile -Raw | ConvertFrom-Json
        foreach ($k in 'monitor','gainDb','monitorVolDb') { if ($null -ne $j.$k) { $s.$k = $j.$k } } } } catch {}
    $s
}
function Save-Settings {
    try {
        if (-not (Test-Path $SettingsDir)) { New-Item -ItemType Directory -Path $SettingsDir | Out-Null }
        $script:settings | ConvertTo-Json | Set-Content -Path $SettingsFile -Encoding UTF8
    } catch {}
}
$script:settings = Load-Settings

# ---------------------------------------------------------------- startup shortcut
function Test-Autostart { Test-Path $StartupLnk }
function Set-Autostart([bool]$on) {
    if ($on) {
        $ws = New-Object -ComObject WScript.Shell
        $l = $ws.CreateShortcut($StartupLnk)
        $l.TargetPath = (Get-Command powershell.exe).Source
        $l.Arguments = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$ScriptPath`""
        $l.WorkingDirectory = Split-Path $ScriptPath
        $l.WindowStyle = 7
        $l.Description = $AppName
        $l.Save()
    } elseif (Test-Path $StartupLnk) { Remove-Item $StartupLnk -Force }
}

if ($Install)   { Set-Autostart $true;  Write-Host "Autostart enabled: $StartupLnk"; return }
if ($Uninstall) { Set-Autostart $false; Write-Host "Autostart disabled"; return }

# ---------------------------------------------------------------- test mode (no UI)
if ($Test) {
    if (-not $mic.Connect()) { Write-Host "Go Mic not found"; return }
    $mn=0.0;$mx=0.0;$st=0.0
    $mic.MicRange([ref]$mn,[ref]$mx,[ref]$st)
    Write-Host ("Mic gain: {0} dB  range {1}..{2} step {3}" -f $mic.MicGainDb,$mn,$mx,$st)
    Write-Host ("Mic muted: {0}" -f $mic.MicMuted)
    Write-Host ("Monitoring on: {0}" -f $mic.MonitorOn)
    if ($mic.HasMonitorVolume) { $mic.MonitorRange([ref]$mn,[ref]$mx,[ref]$st); Write-Host ("Monitor vol: {0} dB range {1}..{2} step {3}" -f $mic.MonitorVolDb,$mn,$mx,$st) }
    return
}

# ---------------------------------------------------------------- UI
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\GoMicTray_SingleInstance', [ref]$createdNew)
if (-not $createdNew) { return }

function New-TrayIcon([bool]$connected, [bool]$monitor, [bool]$muted) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $body = if (-not $connected) { [System.Drawing.Color]::FromArgb(120,120,120) } else { [System.Drawing.Color]::White }
    $pen = New-Object System.Drawing.Pen($body, 2.5)
    $brush = New-Object System.Drawing.SolidBrush($body)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(10, 2, 10, 10, 180, 180); $path.AddLine(20, 7, 20, 14)
    $path.AddArc(10, 10, 10, 10, 0, 180);  $path.AddLine(10, 15, 10, 7)
    $g.FillPath($brush, $path)
    $g.DrawArc($pen, 6, 7, 18, 14, 0, 180)
    $g.DrawLine($pen, 15, 21, 15, 26)
    $g.DrawLine($pen, 10, 27, 20, 27)
    if ($connected) {
        $dot = if ($monitor) { [System.Drawing.Color]::FromArgb(46,204,113) } else { [System.Drawing.Color]::FromArgb(231,76,60) }
        $db = New-Object System.Drawing.SolidBrush($dot)
        $g.FillEllipse([System.Drawing.Brushes]::Black, 17, 17, 15, 15)
        $g.FillEllipse($db, 19, 19, 11, 11)
        $db.Dispose()
    }
    if ($muted) { $rp = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(231,76,60), 3); $g.DrawLine($rp, 4, 4, 22, 28); $rp.Dispose() }
    $h = $bmp.GetHicon()
    $ic = [System.Drawing.Icon]::FromHandle($h)
    $clone = $ic.Clone()
    [GoMicNative.GoMic]::DestroyIcon($h) | Out-Null
    $ic.Dispose(); $g.Dispose(); $bmp.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose()
    $clone
}

function New-SliderItem {
    $tb = New-Object System.Windows.Forms.TrackBar
    $tb.AutoSize = $false; $tb.Width = 210; $tb.Height = 32; $tb.TickStyle = 'None'; $tb.Minimum = 0; $tb.Maximum = 100
    $h = New-Object System.Windows.Forms.ToolStripControlHost($tb)
    $h.AutoSize = $false; $h.Width = 220; $h.Height = 34
    [pscustomobject]@{ Track = $tb; Host = $h; Min = 0.0; Max = 0.0; Step = 1.0 }
}

$script:ni = New-Object System.Windows.Forms.NotifyIcon
$script:ni.Visible = $true
$script:updating = $false
$script:lastIcon = $null

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miMon    = New-Object System.Windows.Forms.ToolStripMenuItem 'Direct monitoring'
$miMon.CheckOnClick = $false
$miMute   = New-Object System.Windows.Forms.ToolStripMenuItem 'Mute microphone'
$lblGain  = New-Object System.Windows.Forms.ToolStripLabel 'Mic gain'
$gainS    = New-SliderItem
$lblMonV  = New-Object System.Windows.Forms.ToolStripLabel 'Monitor (headphone) volume'
$monS     = New-SliderItem
$miAuto   = New-Object System.Windows.Forms.ToolStripMenuItem 'Start with Windows'
$miExit   = New-Object System.Windows.Forms.ToolStripMenuItem 'Exit'
$lblGain.Font = New-Object System.Drawing.Font($lblGain.Font, [System.Drawing.FontStyle]::Bold)
$lblMonV.Font = $lblGain.Font
[void]$menu.Items.Add($miMon)
[void]$menu.Items.Add($miMute)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($lblGain)
[void]$menu.Items.Add($gainS.Host)
[void]$menu.Items.Add($lblMonV)
[void]$menu.Items.Add($monS.Host)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($miAuto)
[void]$menu.Items.Add($miExit)
$script:ni.ContextMenuStrip = $menu

function Db-ToPos($s, [double]$db) { [int][math]::Round(($db - $s.Min) / $s.Step) }
function Pos-ToDb($s, [int]$pos)   { [float]($s.Min + $pos * $s.Step) }

function Setup-Slider($s, [scriptblock]$rangeFn) {
    $mn = 0.0; $mx = 0.0; $st = 0.0
    & $rangeFn ([ref]$mn) ([ref]$mx) ([ref]$st)
    if ($st -le 0) { $st = 1.0 }
    $s.Min = $mn; $s.Max = $mx; $s.Step = $st
    $s.Track.Minimum = 0
    $s.Track.Maximum = [int][math]::Round(($mx - $mn) / $st)
    $s.Track.TickFrequency = [math]::Max(1, [int]($s.Track.Maximum / 10))
    $s.Track.LargeChange = [math]::Max(1, [int]($s.Track.Maximum / 10))
    $s.Track.SmallChange = 1
}

function Refresh-UI {
    $connected = $mic.Connected
    try {
        if ($connected) {
            $script:updating = $true
            $on = $mic.MonitorOn
            $muted = $mic.MicMuted
            $g = $mic.MicGainDb
            $gp = [math]::Min($gainS.Track.Maximum, [math]::Max(0, (Db-ToPos $gainS $g)))
            $gainS.Track.Value = $gp
            $lblGain.Text = ('Mic gain: {0:0.#} dB' -f $g)
            if ($mic.HasMonitorVolume) {
                $mv = $mic.MonitorVolDb
                $mp = [math]::Min($monS.Track.Maximum, [math]::Max(0, (Db-ToPos $monS $mv)))
                $monS.Track.Value = $mp
                $lblMonV.Text = ('Monitor volume: {0:0.#} dB' -f $mv)
            }
            $script:updating = $false
        }
    } catch { $script:updating = $false; $mic.Disconnect(); $connected = $false }

    $miMon.Enabled = $connected; $miMute.Enabled = $connected
    $gainS.Host.Enabled = $connected; $monS.Host.Enabled = $connected
    $monS.Host.Visible = $mic.HasMonitorVolume -or -not $connected; $lblMonV.Visible = $monS.Host.Visible
    if ($connected) {
        $miMon.Checked = $on; $miMute.Checked = $muted
        $miMon.Text = if ($on) { 'Direct monitoring: ON  (click to turn off)' } else { 'Direct monitoring: OFF  (click to turn on)' }
        $tip = 'Go Mic - monitoring ' + $(if ($on) { 'ON' } else { 'OFF' }) + (' | gain {0:0.#} dB' -f $g)
        if ($muted) { $tip += ' | MUTED' }
        $script:ni.Text = $tip
    } else {
        $miMon.Checked = $false; $miMute.Checked = $false
        $miMon.Text = 'Go Mic not connected'
        $script:ni.Text = 'Go Mic - not connected'
        $on = $false; $muted = $false
    }
    $old = $script:lastIcon
    $script:lastIcon = New-TrayIcon $connected $on $muted
    $script:ni.Icon = $script:lastIcon
    if ($old) { $old.Dispose() }
}

function On-Connected {
    # Re-apply saved preferences (the hardware forgets them when unplugged)
    try {
        Setup-Slider $gainS { param($a,$b,$c) $mic.MicRange($a.Value, $b.Value, $c.Value) }
        if ($mic.HasMonitorVolume) { Setup-Slider $monS { param($a,$b,$c) $mic.MonitorRange($a.Value, $b.Value, $c.Value) } }
        if ($null -ne $script:settings.monitor)      { $mic.MonitorOn = [bool]$script:settings.monitor }
        if ($null -ne $script:settings.gainDb)       { $mic.MicGainDb = [float]$script:settings.gainDb }
        if ($null -ne $script:settings.monitorVolDb -and $mic.HasMonitorVolume) { $mic.MonitorVolDb = [float]$script:settings.monitorVolDb }
    } catch {}
}

function Try-Connect {
    if ($mic.Connected) { return }
    if ($mic.Connect()) { On-Connected; Refresh-UI }
}

function Toggle-Monitor {
    if (-not $mic.Connected) { Try-Connect; if (-not $mic.Connected) { return } }
    try {
        $mic.MonitorOn = -not $mic.MonitorOn
        $script:settings.monitor = $mic.MonitorOn; Save-Settings
    } catch { $mic.Disconnect() }
    Refresh-UI
}

$script:ni.add_MouseClick({ param($s, $e) if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Toggle-Monitor } })
$miMon.add_Click({ Toggle-Monitor })
$miMute.add_Click({
    try { $mic.MicMuted = -not $mic.MicMuted } catch { $mic.Disconnect() }
    Refresh-UI
})
$gainS.Track.add_ValueChanged({
    if ($script:updating -or -not $mic.Connected) { return }
    try {
        $db = Pos-ToDb $gainS $gainS.Track.Value
        $mic.MicGainDb = $db
        $script:settings.gainDb = $db
        $lblGain.Text = ('Mic gain: {0:0.#} dB' -f $db)
    } catch { $mic.Disconnect() }
})
$gainS.Track.add_MouseUp({ Save-Settings; Refresh-UI })
$monS.Track.add_ValueChanged({
    if ($script:updating -or -not $mic.Connected) { return }
    try {
        $db = Pos-ToDb $monS $monS.Track.Value
        $mic.MonitorVolDb = $db
        $script:settings.monitorVolDb = $db
        $lblMonV.Text = ('Monitor volume: {0:0.#} dB' -f $db)
    } catch { $mic.Disconnect() }
})
$monS.Track.add_MouseUp({ Save-Settings; Refresh-UI })
$menu.add_Opening({ Try-Connect; Refresh-UI; $miAuto.Checked = (Test-Autostart) })
$miAuto.add_Click({ Set-Autostart (-not (Test-Autostart)) })
$miExit.add_Click({
    $script:timer.Stop()
    $script:ni.Visible = $false
    $script:ni.Dispose()
    [System.Windows.Forms.Application]::Exit()
})

$script:timer = New-Object System.Windows.Forms.Timer
$script:timer.Interval = 3000
$script:timer.add_Tick({
    if ($mic.Connected) {
        try { [void]$mic.MonitorOn; if (-not $menu.Visible) { Refresh-UI } } catch { $mic.Disconnect(); Refresh-UI }
    } else { Try-Connect }
})

if ($mic.Connect()) { On-Connected }
Refresh-UI
$script:timer.Start()
[System.Windows.Forms.Application]::Run()
$mutex.ReleaseMutex()
