using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LanPad.Join;

namespace LanPad.UI
{
    /// <summary>
    /// Key cheat sheet in a corner of the screen, on top of the stream / game: which key is which
    /// Xbox button, the game, and Pad mode on / off. Never takes focus and lets clicks pass through.
    /// Re-asserts "topmost" every second because the full-screen viewer is topmost too.
    /// </summary>
    public sealed class KeyOverlay : Form
    {
        // one keycap group: "A" badge + key text
        struct Item
        {
            public string Badge;
            public Color? BadgeColor; // round coloured badge (A B X Y), else a small text label
            public string Keys;
        }

        static readonly Color Back = Color.FromArgb(16, 18, 24);
        static readonly Color CapFill = Color.FromArgb(44, 48, 60);
        static readonly Color CapEdge = Color.FromArgb(78, 84, 100);
        static readonly Color XboxA = Color.FromArgb(96, 178, 60), XboxB = Color.FromArgb(222, 60, 50),
                              XboxX = Color.FromArgb(50, 120, 220), XboxY = Color.FromArgb(240, 180, 30);

        readonly Font titleFont = Theme.Font(11f, FontStyle.Bold);
        readonly Font smallFont = Theme.Font(8f, FontStyle.Bold);
        readonly Font capFont = Theme.Font(9.5f, FontStyle.Bold);
        readonly Font footFont = Theme.Font(8f);
        readonly Timer topmostTimer = new Timer { Interval = 1000 };

        string title = "", subtitle = "", footer = "";
        bool padOn;
        List<List<Item>> lines = new List<List<Item>>();
        string corner = "TopRight";

        public KeyOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Back;
            Opacity = 0.85;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            topmostTimer.Tick += (s, e) => KeepOnTop();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                // topmost, no taskbar / Alt+Tab entry, never activated, clicks go through to the game
                const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                          WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        /// <summary>Shows (or updates) the cheat sheet.</summary>
        public void ShowSheet(string title, string subtitle, KeyMap keys, bool padOn, string hotkeys, string corner, int opacityPercent)
        {
            this.title = title ?? "";
            this.subtitle = subtitle ?? "";
            this.padOn = padOn;
            this.corner = corner ?? "TopRight";
            footer = hotkeys ?? "";
            lines = BuildLines(keys ?? KeyMap.Default());
            // layered windows need an opacity below 100 % to stay visible with WS_EX_LAYERED set
            Opacity = Math.Max(0.3, Math.Min(0.99, opacityPercent / 100.0));

            Size = Measure();
            Region?.Dispose();
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 12 * Dpi))
                Region = new Region(path);
            PlaceInCorner();
            if (!Visible) Show();
            KeepOnTop();
            topmostTimer.Start();
            Invalidate();
        }

        public void HideSheet()
        {
            topmostTimer.Stop();
            Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                topmostTimer.Dispose();
                titleFont.Dispose();
                smallFont.Dispose();
                capFont.Dispose();
                footFont.Dispose();
            }
            base.Dispose(disposing);
        }

        float Dpi => DeviceDpi / 96f;

        static string Arrow(Keys k)
        {
            switch (k)
            {
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                default: return KeyMap.KeyName(k);
            }
        }

        static string KeyText(KeyMap m, PadAction a) => string.Join("/", m.Get(a).Select(Arrow));

        static List<List<Item>> BuildLines(KeyMap m)
        {
            Item I(string badge, PadAction a, Color? color = null) => new Item { Badge = badge, BadgeColor = color, Keys = KeyText(m, a) };
            Item Group(string badge, params PadAction[] actions) => new Item
            {
                Badge = badge,
                Keys = string.Join(" ", actions.Select(a => KeyText(m, a)).Where(s => s.Length > 0)),
            };

            var result = new List<List<Item>>
            {
                new List<Item> { Group("MOVE", PadAction.LeftUp, PadAction.LeftLeft, PadAction.LeftDown, PadAction.LeftRight),
                                 Group("R-STICK", PadAction.RightUp, PadAction.RightLeft, PadAction.RightDown, PadAction.RightRight) },
                new List<Item> { I("A", PadAction.A, XboxA), I("B", PadAction.B, XboxB), I("X", PadAction.X, XboxX), I("Y", PadAction.Y, XboxY) },
                new List<Item> { I("LB", PadAction.LB), I("RB", PadAction.RB), I("LT", PadAction.LT), I("RT", PadAction.RT) },
                new List<Item> { I("START", PadAction.Start), I("BACK", PadAction.Back), I("L3", PadAction.L3), I("R3", PadAction.R3) },
                new List<Item> { Group("D-PAD ↑↓←→", PadAction.DUp, PadAction.DDown, PadAction.DLeft, PadAction.DRight),
                                 I("WALK", PadAction.Walk) },
            };
            // leave out buttons this layout does not use, and empty lines
            foreach (var line in result) line.RemoveAll(i => i.Keys.Length == 0);
            result.RemoveAll(l => l.Count == 0);
            return result;
        }

        // ------------------------------------------------------------------ layout

        const int Pad = 14, LineGap = 6, ItemGap = 14, BadgeGap = 5;

        int CapHeight => (int)(24 * Dpi);

        Size ItemSize(Item it)
        {
            float s = Dpi;
            int badge = it.BadgeColor.HasValue ? CapHeight - (int)(2 * s) : TextRenderer.MeasureText(it.Badge, smallFont).Width;
            int keys = TextRenderer.MeasureText(it.Keys, capFont).Width + (int)(14 * s);
            return new Size(badge + (int)(BadgeGap * s) + keys, CapHeight);
        }

        Size Measure()
        {
            float s = Dpi;
            int width = TextRenderer.MeasureText(title, titleFont).Width + (int)(90 * s); // + PAD ON pill
            width = Math.Max(width, TextRenderer.MeasureText(footer, footFont).Width);
            foreach (var line in lines)
                width = Math.Max(width, line.Sum(i => ItemSize(i).Width) + (int)(ItemGap * s) * (line.Count - 1));
            int height = TitleBlockHeight() + lines.Count * (CapHeight + (int)(LineGap * s)) + footFont.Height + (int)(8 * s);
            return new Size(width + (int)(2 * Pad * s), height + (int)(2 * Pad * s));
        }

        int TitleBlockHeight() => titleFont.Height + (subtitle.Length > 0 ? footFont.Height : 0) + (int)(10 * Dpi);

        void PlaceInCorner()
        {
            var screen = Screen.PrimaryScreen.Bounds;
            int margin = (int)(18 * Dpi);
            bool right = corner.EndsWith("Right", StringComparison.OrdinalIgnoreCase);
            bool bottom = corner.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase);
            Location = new Point(
                right ? screen.Right - Width - margin : screen.Left + margin,
                bottom ? screen.Bottom - Height - margin - (int)(40 * Dpi) : screen.Top + margin + (int)(70 * Dpi)); // clear of the toast at the top
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        void KeepOnTop()
        {
            const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
            if (IsHandleCreated && Visible)
                SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // ------------------------------------------------------------------ paint

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Dpi;
            g.Clear(Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x0 = (int)(Pad * s), y = (int)(Pad * s);

            // thin border
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12 * s))
            using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
                g.DrawPath(pen, path);

            // title + PAD ON / OFF
            TextRenderer.DrawText(g, title, titleFont, new Point(x0, y), Color.White, TextFormatFlags.NoPrefix);
            string pill = padOn ? "PAD ON" : "PAD OFF";
            Size ps = TextRenderer.MeasureText(pill, smallFont);
            var pr = new RectangleF(Width - x0 - ps.Width - 16 * s, y + 1 * s, ps.Width + 16 * s, ps.Height + 6 * s);
            using (var path = Theme.RoundRect(pr, pr.Height / 2))
            using (var b = new SolidBrush(padOn ? Color.FromArgb(30, 150, 76) : Color.FromArgb(190, 45, 45)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, pill, smallFont, Rectangle.Round(pr), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += titleFont.Height;
            if (subtitle.Length > 0)
            {
                TextRenderer.DrawText(g, subtitle, footFont, new Point(x0, y), Theme.Muted, TextFormatFlags.NoPrefix);
                y += footFont.Height;
            }
            y += (int)(10 * s);

            foreach (var line in lines)
            {
                int x = x0;
                foreach (var it in line)
                {
                    DrawItem(g, it, x, y);
                    x += ItemSize(it).Width + (int)(ItemGap * s);
                }
                y += CapHeight + (int)(LineGap * s);
            }

            y += (int)(2 * s);
            TextRenderer.DrawText(g, footer, footFont, new Point(x0, y), Theme.Muted, TextFormatFlags.NoPrefix);
        }

        void DrawItem(Graphics g, Item it, int x, int y)
        {
            float s = Dpi;
            int h = CapHeight;
            int badgeW;
            if (it.BadgeColor.HasValue)
            {
                badgeW = h - (int)(2 * s);
                using (var b = new SolidBrush(it.BadgeColor.Value)) g.FillEllipse(b, x, y + 1 * s, badgeW, badgeW);
                TextRenderer.DrawText(g, it.Badge, smallFont, new Rectangle(x, y + (int)s, badgeW, badgeW), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else
            {
                badgeW = TextRenderer.MeasureText(it.Badge, smallFont).Width;
                TextRenderer.DrawText(g, it.Badge, smallFont, new Rectangle(x, y, badgeW, h), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int cx = x + badgeW + (int)(BadgeGap * s);
            int cw = TextRenderer.MeasureText(it.Keys, capFont).Width + (int)(14 * s);
            var cap = new RectangleF(cx, y, cw, h - 1);
            using (var path = Theme.RoundRect(cap, 5 * s))
            using (var b = new SolidBrush(CapFill))
            using (var pen = new Pen(CapEdge))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, it.Keys, capFont, Rectangle.Round(cap), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}
