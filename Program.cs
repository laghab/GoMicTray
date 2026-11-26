using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using GoMicNative;
using Microsoft.Win32;

namespace GoMicTray
{
    class Slider
    {
        public TrackBar Track = new TrackBar();
        public ToolStripControlHost Host;
        public float Min, Max, Step = 1;
        public Slider()
        {
            Track.AutoSize = false; Track.Width = 210; Track.Height = 32;
            Track.TickStyle = TickStyle.None; Track.Minimum = 0; Track.Maximum = 100;
            Host = new ToolStripControlHost(Track);
            Host.AutoSize = false; Host.Width = 220; Host.Height = 34;
        }
        public void Setup(float mn, float mx, float st)
        {
            if (st <= 0) st = 1;
            Min = mn; Max = mx; Step = st;
            Track.Minimum = 0;
            Track.Maximum = (int)Math.Round((mx - mn) / st);
            Track.TickFrequency = Math.Max(1, Track.Maximum / 10);
            Track.LargeChange = Math.Max(1, Track.Maximum / 10);
            Track.SmallChange = 1;
        }
        public int ToPos(float db) { return Math.Min(Track.Maximum, Math.Max(0, (int)Math.Round((db - Min) / Step))); }
        public float ToDb(int pos) { return Min + pos * Step; }
    }

    class TrayApp : ApplicationContext
    {
        const string AppName = "GoMic Tray";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoMicTray");
        string settingsFile { get { return Path.Combine(settingsDir, "settings.ini"); } }

        GoMic mic = new GoMic();
        NotifyIcon ni = new NotifyIcon();
        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem miMon = new ToolStripMenuItem("Direct monitoring");
        ToolStripMenuItem miMute = new ToolStripMenuItem("Mute microphone");
        ToolStripMenuItem miAuto = new ToolStripMenuItem("Start with Windows");
        ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
        ToolStripLabel lblGain = new ToolStripLabel("Mic gain");
        ToolStripLabel lblMonV = new ToolStripLabel("Monitor (headphone) volume");
        Slider gainS = new Slider();
        Slider monS = new Slider();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool updating;
        Icon lastIcon;

        // saved settings (null = not set)
        bool? sMonitor; float? sGain; float? sMonVol;

        public TrayApp()
        {
            LoadSettings();
            lblGain.Font = new Font(lblGain.Font, FontStyle.Bold);
            lblMonV.Font = lblGain.Font;
            menu.Items.Add(miMon);
            menu.Items.Add(miMute);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(lblGain);
            menu.Items.Add(gainS.Host);
            menu.Items.Add(lblMonV);
            menu.Items.Add(monS.Host);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miAuto);
            menu.Items.Add(miExit);
            ni.ContextMenuStrip = menu;

            ni.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleMonitor(); };
            miMon.Click += delegate { ToggleMonitor(); };
            miMute.Click += delegate
            {
                try { mic.MicMuted = !mic.MicMuted; } catch { mic.Disconnect(); }
                RefreshUI();
            };
            gainS.Track.ValueChanged += delegate
            {
                if (updating || !mic.Connected) return;
                try
                {
                    float db = gainS.ToDb(gainS.Track.Value);
                    mic.MicGainDb = db; sGain = db;
                    lblGain.Text = string.Format("Mic gain: {0:0.#} dB", db);
                }
                catch { mic.Disconnect(); }
            };
            gainS.Track.MouseUp += delegate { SaveSettings(); RefreshUI(); };
            monS.Track.ValueChanged += delegate
            {
                if (updating || !mic.Connected) return;
                try
                {
                    float db = monS.ToDb(monS.Track.Value);
                    mic.MonitorVolDb = db; sMonVol = db;
                    lblMonV.Text = string.Format("Monitor volume: {0:0.#} dB", db);
                }
                catch { mic.Disconnect(); }
            };
            monS.Track.MouseUp += delegate { SaveSettings(); RefreshUI(); };
            menu.Opening += delegate { TryConnect(); RefreshUI(); miAuto.Checked = AutostartEnabled(); };
            miAuto.Click += delegate { SetAutostart(!AutostartEnabled()); };
            miExit.Click += delegate { Shutdown(); };

            timer.Interval = 3000;
            timer.Tick += delegate
            {
                if (mic.Connected)
                {
                    try { bool b = mic.MonitorOn; if (!menu.Visible) RefreshUI(); }
                    catch { mic.Disconnect(); RefreshUI(); }
                }
                else TryConnect();
            };

            ni.Visible = true;
            if (mic.Connect()) OnConnected();
            RefreshUI();
            timer.Start();
        }

        void Shutdown()
        {
            timer.Stop();
            ni.Visible = false;
            ni.Dispose();
            ExitThread();
        }

        // ---------- settings
        void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsFile)) return;
                foreach (string line in File.ReadAllLines(settingsFile))
                {
                    int i = line.IndexOf('='); if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "monitor") sMonitor = (v == "1");
                    else if (k == "gainDb") sGain = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    else if (k == "monitorVolDb") sMonVol = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { }
        }
        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(settingsDir);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var l = new System.Collections.Generic.List<string>();
                if (sMonitor.HasValue) l.Add("monitor=" + (sMonitor.Value ? "1" : "0"));
                if (sGain.HasValue) l.Add("gainDb=" + sGain.Value.ToString(ci));
                if (sMonVol.HasValue) l.Add("monitorVolDb=" + sMonVol.Value.ToString(ci));
                File.WriteAllLines(settingsFile, l.ToArray());
            }
            catch { }
        }

        // ---------- autostart (HKCU Run key)
        static bool AutostartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) { return k != null && k.GetValue(AppName) != null; }
        }
        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        // ---------- device logic
        void OnConnected()
        {
            try
            {
                float mn, mx, st;
                mic.MicRange(out mn, out mx, out st); gainS.Setup(mn, mx, st);
                if (mic.HasMonitorVolume) { mic.MonitorRange(out mn, out mx, out st); monS.Setup(mn, mx, st); }
                if (sMonitor.HasValue) mic.MonitorOn = sMonitor.Value;
                if (sGain.HasValue) mic.MicGainDb = sGain.Value;
                if (sMonVol.HasValue && mic.HasMonitorVolume) mic.MonitorVolDb = sMonVol.Value;
            }
            catch { }
        }
        void TryConnect()
        {
            if (mic.Connected) return;
            if (mic.Connect()) { OnConnected(); RefreshUI(); }
        }
        void ToggleMonitor()
        {
            if (!mic.Connected) { TryConnect(); if (!mic.Connected) return; }
            try { mic.MonitorOn = !mic.MonitorOn; sMonitor = mic.MonitorOn; SaveSettings(); }
            catch { mic.Disconnect(); }
            RefreshUI();
        }

        void RefreshUI()
        {
            bool connected = mic.Connected, on = false, muted = false;
            float g = 0;
            try
            {
                if (connected)
                {
                    updating = true;
                    on = mic.MonitorOn; muted = mic.MicMuted; g = mic.MicGainDb;
                    gainS.Track.Value = gainS.ToPos(g);
                    lblGain.Text = string.Format("Mic gain: {0:0.#} dB", g);
                    if (mic.HasMonitorVolume)
                    {
                        float mv = mic.MonitorVolDb;
                        monS.Track.Value = monS.ToPos(mv);
                        lblMonV.Text = string.Format("Monitor volume: {0:0.#} dB", mv);
                    }
                    updating = false;
                }
            }
            catch { updating = false; mic.Disconnect(); connected = false; }

            miMon.Enabled = connected; miMute.Enabled = connected;
            gainS.Host.Enabled = connected; monS.Host.Enabled = connected;
            monS.Host.Visible = lblMonV.Visible = mic.HasMonitorVolume || !connected;
            if (connected)
            {
                miMon.Checked = on; miMute.Checked = muted;
                miMon.Text = on ? "Direct monitoring: ON  (click to turn off)" : "Direct monitoring: OFF  (click to turn on)";
                string tip = "Go Mic - monitoring " + (on ? "ON" : "OFF") + string.Format(" | gain {0:0.#} dB", g);
                if (muted) tip += " | MUTED";
                ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            else
            {
                miMon.Checked = false; miMute.Checked = false;
                miMon.Text = "Go Mic not connected";
                ni.Text = "Go Mic - not connected";
            }
            Icon old = lastIcon;
            lastIcon = MakeIcon(connected, on, muted);
            ni.Icon = lastIcon;
            if (old != null) old.Dispose();
        }

        static Icon MakeIcon(bool connected, bool monitor, bool muted)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color body = connected ? Color.White : Color.FromArgb(120, 120, 120);
                using (var pen = new Pen(body, 2.5f))
                using (var brush = new SolidBrush(body))
                using (var path = new GraphicsPath())
                {
                    path.AddArc(10, 2, 10, 10, 180, 180); path.AddLine(20, 7, 20, 14);
                    path.AddArc(10, 10, 10, 10, 0, 180); path.AddLine(10, 15, 10, 7);
                    g.FillPath(brush, path);
                    g.DrawArc(pen, 6, 7, 18, 14, 0, 180);
                    g.DrawLine(pen, 15, 21, 15, 26);
                    g.DrawLine(pen, 10, 27, 20, 27);
                }
                if (connected)
                {
                    using (var db = new SolidBrush(monitor ? Color.FromArgb(46, 204, 113) : Color.FromArgb(231, 76, 60)))
                    {
                        g.FillEllipse(Brushes.Black, 17, 17, 15, 15);
                        g.FillEllipse(db, 19, 19, 11, 11);
                    }
                }
                if (muted) using (var rp = new Pen(Color.FromArgb(231, 76, 60), 3)) g.DrawLine(rp, 4, 4, 22, 28);
                IntPtr h = bmp.GetHicon();
                Icon ic = Icon.FromHandle(h);
                Icon clone = (Icon)ic.Clone();
                GoMic.DestroyIcon(h);
                ic.Dispose();
                return clone;
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\GoMicTray_SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }
    }
}
