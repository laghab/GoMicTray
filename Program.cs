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
    public class TrayApp : ApplicationContext
    {
        const string AppName = "GoMic Tray";
        const int MenuWidth = 264;
        const float RefDb = -30f;              // the "default" monitor volume; slider shows +/- relative to this
        static readonly string Minus = ((char)0x2212).ToString(), Dot = ((char)0x00B7).ToString();

        readonly string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoMicTray");
        string settingsFile { get { return Path.Combine(settingsDir, "settings.ini"); } }

        GoMic mic = new GoMic();
        NotifyIcon ni = new NotifyIcon();
        ContextMenuStrip menu = new ContextMenuStrip();
        SoftRenderer renderer = new SoftRenderer();
        Font fontMain = new Font("Segoe UI", 9.5f);
        Font fontSmall = new Font("Segoe UI", 8.25f);
        Font fontLabel = new Font("Segoe UI Semibold", 9f);

        ToolStripMenuItem miMon = new ToolStripMenuItem("Direct monitoring");
        ToolStripMenuItem miMute = new ToolStripMenuItem("Microphone");
        ToolStripMenuItem miReset = new ToolStripMenuItem("Reset volume");
        ToolStripMenuItem miAuto = new ToolStripMenuItem("Start with Windows");
        ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
        ToolStripLabel lblVol = new ToolStripLabel("Monitor volume");
        ToolStripLabel lblHint = new ToolStripLabel("Click: mute mic   " + Dot + "   Double-click: monitoring");
        SoftSlider slider = new SoftSlider();
        ToolStripControlHost sliderHost;
        ToolStripSeparator sep1 = new ToolStripSeparator(), sep2 = new ToolStripSeparator(), sep3 = new ToolStripSeparator();

        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer clickTimer = new System.Windows.Forms.Timer();
        Icon lastIcon;
        float volMin, volStep = 1;

        bool? sMonitor; float? sMonVol;          // saved preferences (null = never set)

        public ContextMenuStrip Menu { get { return menu; } }

        public TrayApp()
        {
            LoadSettings();

            // Fixed-width menu: every item is exactly MenuWidth wide (edge to edge, no gap on the right);
            // the renderer insets the hover highlight so it still looks like a rounded pill.
            // (non-menu-item entries are laid out with an 8px inset by the dropdown, so they are 16px narrower)
            const int Inner = MenuWidth - 16;
            slider.Size = new Size(Inner, 30);
            sliderHost = new ToolStripControlHost(slider);
            sliderHost.AutoSize = false; sliderHost.Size = new Size(Inner, 30);
            sliderHost.Margin = Padding.Empty; sliderHost.Padding = Padding.Empty;

            foreach (var mi in new[] { miMon, miMute, miReset, miAuto, miExit })
            {
                mi.AutoSize = false; mi.Size = new Size(MenuWidth, 32);
                mi.Font = fontMain; mi.TextAlign = ContentAlignment.MiddleLeft;
                mi.Margin = Padding.Empty; mi.Padding = new Padding(16, 0, 16, 0);
            }
            // labels sit 8px in from the edge, so 8px of padding puts their text on the same 16px margin
            lblVol.AutoSize = false; lblVol.Size = new Size(Inner, 28); lblVol.Font = fontLabel;
            lblVol.Margin = Padding.Empty; lblVol.Padding = new Padding(8, 0, 0, 0);
            lblHint.AutoSize = false; lblHint.Size = new Size(Inner, 26); lblHint.Font = fontSmall;
            lblHint.Margin = Padding.Empty; lblHint.Padding = new Padding(8, 0, 0, 0);
            foreach (var sp in new[] { sep1, sep2, sep3 }) { sp.AutoSize = false; sp.Size = new Size(MenuWidth, 7); sp.Margin = Padding.Empty; }

            menu.Renderer = renderer;
            menu.ShowImageMargin = false; menu.ShowCheckMargin = false;
            menu.Padding = new Padding(0, 6, 0, 6);
            menu.AutoSize = false;
            menu.Width = MenuWidth;
            menu.Items.AddRange(new ToolStripItem[] { miMon, miMute, sep1, lblVol, sliderHost, miReset, sep2, lblHint, sep3, miAuto, miExit });
            ni.ContextMenuStrip = menu;
            menu.HandleCreated += delegate { Gfx.RoundCorners(menu.Handle); };
            menu.Opened += delegate { Gfx.RoundCorners(menu.Handle); };

            // Single click = toggle mic mute, applied IMMEDIATELY (no waiting for a possible double-click).
            // If a second click lands within the window, it's a double-click: put the mute back the way it
            // was before the first click, and toggle direct monitoring instead.
            clickTimer.Interval = 400;
            clickTimer.Tick += delegate { clickTimer.Stop(); };
            ni.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (clickTimer.Enabled)
                {
                    clickTimer.Stop();
                    RestoreMute();
                    ToggleMonitor();
                }
                else
                {
                    mutedBeforeClick = ReadMuted();
                    ToggleMute();
                    clickTimer.Start();
                }
            };

            miMon.Click += delegate { ToggleMonitor(); };
            miMute.Click += delegate { ToggleMute(); };
            miReset.Click += delegate { SetVolume(RefDb); SaveSettings(); RefreshUI(); };
            slider.ValueChanged += delegate { SetVolume(slider.Value * volStep + volMin); };
            slider.DragCompleted += delegate { SaveSettings(); RefreshUI(); };
            menu.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                ApplyTheme(); TryConnect(); RefreshUI();
                int h = menu.Padding.Vertical;
                foreach (ToolStripItem it in menu.Items) h += it.Height + it.Margin.Vertical;
                menu.Size = new Size(MenuWidth, h);
            };
            miAuto.Click += delegate { SetAutostart(!AutostartEnabled()); RefreshUI(); };
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

            ApplyTheme();
            EnsureStartMenuShortcut();
            ni.Visible = true;
            if (mic.Connect()) OnConnected();
            RefreshUI();
            timer.Start();
        }

        void Shutdown()
        {
            timer.Stop(); clickTimer.Stop();
            ni.Visible = false;
            ni.Dispose();
            ExitThread();
        }

        void ApplyTheme()
        {
            var t = Theme.Current();
            renderer.T = t; slider.T = t;
            menu.BackColor = t.Bg; menu.ForeColor = t.Text;
            slider.BackColor = t.Bg; sliderHost.BackColor = t.Bg;
            lblVol.ForeColor = t.Text; lblHint.ForeColor = t.Sub;
            foreach (var mi in new[] { miMon, miMute, miReset, miAuto, miExit }) mi.ForeColor = t.Text;
            slider.Invalidate();
        }

        // ---------- settings (never stores or applies mic gain)
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
                if (sMonVol.HasValue) l.Add("monitorVolDb=" + sMonVol.Value.ToString(ci));
                File.WriteAllLines(settingsFile, l.ToArray());
            }
            catch { }
        }

        // ---------- Start menu shortcut ("Samson Go Mic") so the app can be found/launched from Start
        static void EnsureStartMenuShortcut()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoMicTray");
                Directory.CreateDirectory(dir);
                string ico = Path.Combine(dir, "GoMic.ico");
                using (var icon = MakeAppIcon()) using (var fs = new FileStream(ico, FileMode.Create)) icon.Save(fs);

                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Samson Go Mic.lnk");
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                Type st = sc.GetType();
                Action<string, object> set = delegate (string n, object v)
                { st.InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { v }); };
                set("TargetPath", Program.LaunchTarget);
                set("Arguments", Program.LaunchArgs);
                set("WorkingDirectory", Path.GetDirectoryName(Program.LaunchTarget) ?? "");
                set("IconLocation", ico);
                set("Description", "Samson Go Mic - direct monitoring, mute and monitor volume");
                set("WindowStyle", 7);
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }
        }

        static Icon MakeAppIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = Gfx.Round(new Rectangle(1, 1, 62, 62), 14))
                using (var bb = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(70, 130, 220), Color.FromArgb(40, 70, 150), 90f))
                    g.FillPath(bb, bg);
                g.ScaleTransform(1.5f, 1.5f);
                g.TranslateTransform(2f, 1f);
                using (var pen = new Pen(Color.White, 2.5f))
                using (var brush = new SolidBrush(Color.White))
                using (var path = new GraphicsPath())
                {
                    path.AddArc(10, 2, 10, 10, 180, 180); path.AddLine(20, 7, 20, 14);
                    path.AddArc(10, 10, 10, 10, 0, 180); path.AddLine(10, 15, 10, 7);
                    g.FillPath(brush, path);
                    g.DrawArc(pen, 6, 7, 18, 14, 0, 180);
                    g.DrawLine(pen, 15, 21, 15, 26);
                    g.DrawLine(pen, 10, 27, 20, 27);
                }
                IntPtr h = bmp.GetHicon();
                Icon ic = Icon.FromHandle(h);
                Icon clone = (Icon)ic.Clone();
                GoMic.DestroyIcon(h);
                ic.Dispose();
                return clone;
            }
        }

        // ---------- autostart (HKCU Run key)
        public static bool AutostartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Program.RunKey)) { return k != null && k.GetValue(AppName) != null; }
        }
        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Program.RunKey))
            {
                if (on) k.SetValue(AppName, Program.AutostartCommand);
                else k.DeleteValue(AppName, false);
            }
        }

        // ---------- device logic
        void OnConnected()
        {
            try
            {
                if (mic.HasMonitorVolume)
                {
                    float mn, mx, st;
                    mic.MonitorRange(out mn, out mx, out st);
                    if (st <= 0) st = 1;
                    volMin = mn; volStep = st;
                    slider.Minimum = 0; slider.Maximum = (int)Math.Round((mx - mn) / st);
                }
                if (sMonitor.HasValue) mic.MonitorOn = sMonitor.Value;
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
        bool? mutedBeforeClick;
        bool? ReadMuted()
        {
            try { return mic.Connected ? (bool?)mic.MicMuted : null; } catch { return null; }
        }
        void RestoreMute()
        {
            try { if (mutedBeforeClick.HasValue && mic.Connected) mic.MicMuted = mutedBeforeClick.Value; } catch { mic.Disconnect(); }
        }
        void ToggleMute()
        {
            if (!mic.Connected) { TryConnect(); if (!mic.Connected) return; }
            try { mic.MicMuted = !mic.MicMuted; }
            catch { mic.Disconnect(); }
            RefreshUI();
        }
        void SetVolume(float db)
        {
            if (!mic.Connected || !mic.HasMonitorVolume) return;
            try { mic.MonitorVolDb = db; sMonVol = db; UpdateVolLabel(db); }
            catch { mic.Disconnect(); }
        }
        void UpdateVolLabel(float db)
        {
            float off = db - RefDb;
            lblVol.Text = Math.Abs(off) < 0.5f
                ? "Monitor volume  " + Dot + "  default"
                : string.Format("Monitor volume  {0}  {1}{2:0} dB", Dot, off > 0 ? "+" : Minus, Math.Abs(off));
        }

        void RefreshUI()
        {
            bool connected = mic.Connected, on = false, muted = false;
            try
            {
                if (connected)
                {
                    on = mic.MonitorOn; muted = mic.MicMuted;
                    if (mic.HasMonitorVolume)
                    {
                        float v = mic.MonitorVolDb;
                        slider.Value = (int)Math.Round((v - volMin) / volStep);
                        UpdateVolLabel(v);
                    }
                }
            }
            catch { mic.Disconnect(); connected = false; }

            miMon.Enabled = connected; miMute.Enabled = connected && mic.HasMicMute;
            slider.Enabled = connected; miReset.Enabled = connected;
            miMon.Tag = connected ? (on ? "On" : "Off") : "";          // drawn right-aligned by SoftRenderer
            miMute.Tag = connected ? (muted ? "Muted" : "Live") : "";
            miAuto.Tag = AutostartEnabled() ? "On" : "Off";
            menu.Invalidate();
            if (!connected) lblVol.Text = "Go Mic not connected";

            string tip = connected
                ? "Go Mic " + Dot + " monitoring " + (on ? "on" : "off") + (muted ? " " + Dot + " MUTED" : "")
                : "Go Mic " + Dot + " not connected";
            ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

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
                Color body = !connected ? Color.FromArgb(120, 120, 120) : (muted ? Color.FromArgb(231, 76, 60) : Color.White);
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
                    using (var db = new SolidBrush(monitor ? Color.FromArgb(46, 204, 113) : Color.FromArgb(140, 140, 140)))
                    {
                        g.FillEllipse(Brushes.Black, 17, 17, 15, 15);
                        g.FillEllipse(db, 19, 19, 11, 11);
                    }
                }
                if (muted) using (var rp = new Pen(Color.White, 3)) g.DrawLine(rp, 4, 4, 24, 28);
                IntPtr h = bmp.GetHicon();
                Icon ic = Icon.FromHandle(h);
                Icon clone = (Icon)ic.Clone();
                GoMic.DestroyIcon(h);
                ic.Dispose();
                return clone;
            }
        }
    }

    public static class Program
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // The launcher script overrides this when running from PowerShell instead of the compiled exe.
        public static string AutostartCommand = "\"" + Application.ExecutablePath + "\"";
        // What the Start menu shortcut launches (launcher script overrides these too).
        public static string LaunchTarget = Application.ExecutablePath;
        public static string LaunchArgs = "";

        [STAThread]
        public static void Main() { Run(); }

        public static void Run()
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
