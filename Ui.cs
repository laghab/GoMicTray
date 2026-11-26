using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GoMicTray
{
    class Theme
    {
        public Color Bg, Text, Sub, Hover, Border, Accent, Danger, Track;
        public bool Dark;

        public static Theme Current()
        {
            bool light = true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) light = ((int)v) != 0;
                }
            }
            catch { }
            var t = new Theme();
            t.Dark = !light;
            if (light)
            {
                t.Bg = Color.FromArgb(249, 249, 249); t.Text = Color.FromArgb(28, 28, 28); t.Sub = Color.FromArgb(120, 120, 120);
                t.Hover = Color.FromArgb(232, 232, 236); t.Border = Color.FromArgb(226, 226, 226);
                t.Accent = Color.FromArgb(0, 120, 212); t.Danger = Color.FromArgb(209, 52, 56); t.Track = Color.FromArgb(213, 213, 217);
            }
            else
            {
                t.Bg = Color.FromArgb(44, 44, 46); t.Text = Color.FromArgb(242, 242, 244); t.Sub = Color.FromArgb(150, 150, 156);
                t.Hover = Color.FromArgb(64, 64, 68); t.Border = Color.FromArgb(70, 70, 74);
                t.Accent = Color.FromArgb(96, 190, 255); t.Danger = Color.FromArgb(255, 110, 110); t.Track = Color.FromArgb(80, 80, 85);
            }
            return t;
        }
    }

    static class Gfx
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // Windows 11: ask DWM for rounded corners on the pop-up menu
        public static void RoundCorners(IntPtr hwnd)
        {
            try { int pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, 4); } catch { }
        }
    }

    // Flat, rounded slider that matches the menu theme. Raises ValueChanged only for user input.
    class SoftSlider : Control
    {
        int min, max = 100, val;
        bool drag, hot;
        public Theme T = Theme.Current();
        public event EventHandler ValueChanged;
        public event EventHandler DragCompleted;
        const int PadX = 16;

        public SoftSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = 30; Cursor = Cursors.Hand;
        }
        public int Minimum { get { return min; } set { min = value; Invalidate(); } }
        public int Maximum { get { return max; } set { max = Math.Max(value, min + 1); Invalidate(); } }
        public int Value
        {
            get { return val; }
            set { val = Math.Min(max, Math.Max(min, value)); Invalidate(); }
        }
        int FromX(int x)
        {
            float t = (x - PadX) / (float)Math.Max(1, Width - 2 * PadX);
            t = Math.Min(1f, Math.Max(0f, t));
            return min + (int)Math.Round(t * (max - min));
        }
        void UserSet(int v)
        {
            v = Math.Min(max, Math.Max(min, v));
            if (v == val) return;
            val = v; Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            drag = true; Capture = true; UserSet(FromX(e.X));
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) UserSet(FromX(e.X));
            else if (!hot) { hot = true; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!drag) return;
            drag = false; Capture = false;
            if (DragCompleted != null) DragCompleted(this, EventArgs.Empty);
        }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            UserSet(val + (e.Delta > 0 ? 1 : -1));
            if (DragCompleted != null) DragCompleted(this, EventArgs.Empty);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int cy = Height / 2;
            var track = new Rectangle(PadX, cy - 2, Width - 2 * PadX, 4);
            float t = (val - min) / (float)Math.Max(1, max - min);
            int x = PadX + (int)Math.Round(t * track.Width);
            using (var p = Gfx.Round(track, 2)) using (var b = new SolidBrush(T.Track)) g.FillPath(b, p);
            if (x > track.X)
            {
                var fill = new Rectangle(track.X, track.Y, Math.Max(4, x - track.X), track.Height);
                using (var p = Gfx.Round(fill, 2)) using (var b = new SolidBrush(T.Accent)) g.FillPath(b, p);
            }
            int r = (drag || hot) ? 8 : 7;
            using (var b = new SolidBrush(T.Dark ? Color.FromArgb(240, 240, 240) : Color.White))
            using (var pen = new Pen(T.Accent, 2f))
            {
                g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(pen, x - r, cy - r, r * 2, r * 2);
            }
        }
    }

    class SoftRenderer : ToolStripProfessionalRenderer
    {
        public Theme T = Theme.Current();
        public SoftRenderer() : base(new ProfessionalColorTable()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(T.Bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!(e.Item is ToolStripMenuItem)) return;
            var g = e.Graphics;
            if (e.Item.Selected && e.Item.Enabled)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                using (var p = Gfx.Round(r, 6)) using (var b = new SolidBrush(T.Hover)) g.FillPath(b, p);
            }
            // optional right-aligned state text ("On", "Off", "Live", "Muted") stored in Tag
            string state = e.Item.Tag as string;
            if (!string.IsNullOrEmpty(state))
            {
                Color c = (state == "On" || state == "Live") ? T.Accent : (state == "Muted" ? T.Danger : T.Sub);
                if (!e.Item.Enabled) c = T.Sub;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                TextRenderer.DrawText(g, state, e.Item.Font, new Rectangle(0, -3, e.Item.Width - 12, e.Item.Height), c,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(T.Border)) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var mi = e.Item as ToolStripMenuItem;
            if (mi != null)
            {
                e.TextColor = mi.Enabled ? T.Text : T.Sub;
            }
            base.OnRenderItemText(e);
        }
    }
}
