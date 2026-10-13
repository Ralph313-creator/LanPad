using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LanPad.Games;
using LanPad.Join;

namespace LanPad.UI
{
    /// <summary>Dark colours, the app icon and the small custom-drawn controls the LanPad windows use.</summary>
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(17, 19, 25);
        public static readonly Color Surface = Color.FromArgb(27, 30, 38);
        public static readonly Color SurfaceHi = Color.FromArgb(37, 41, 52);
        public static readonly Color Border = Color.FromArgb(51, 56, 70);
        public static readonly Color Text = Color.FromArgb(236, 238, 243);
        public static readonly Color Muted = Color.FromArgb(145, 152, 170);
        public static readonly Color Accent = Color.FromArgb(59, 130, 246);
        public static readonly Color Violet = Color.FromArgb(139, 92, 246);
        public static readonly Color Good = Color.FromArgb(34, 197, 94);
        public static readonly Color Warn = Color.FromArgb(245, 158, 11);
        public static readonly Color Bad = Color.FromArgb(239, 68, 68);

        public static Font Font(float size, FontStyle style = FontStyle.Regular) => new Font("Segoe UI", size, style);

        public static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));

        static Icon appIcon;
        static Bitmap logo;

        /// <summary>LanPad.ico (embedded; made by scripts\make-icon.ps1).</summary>
        public static Icon AppIcon
        {
            get
            {
                if (appIcon != null) return appIcon;
                using (var s = typeof(Theme).Assembly.GetManifestResourceStream("LanPad.ico"))
                    appIcon = s != null ? new Icon(s) : SystemIcons.Application;
                return appIcon;
            }
        }

        /// <summary>The largest image of the app icon, for drawing the logo at any size.</summary>
        public static Bitmap Logo
        {
            get
            {
                if (logo != null) return logo;
                using (var i = new Icon(AppIcon, 256, 256)) logo = i.ToBitmap();
                return logo;
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>Dark window title bar on Windows 10 (20H1+) and 11; ignored elsewhere.</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch (Exception) { }
        }

        /// <summary>Dark scroll bars for <paramref name="c"/> (Windows 10 1809+); ignored elsewhere.</summary>
        public static void DarkScrollBars(Control c, string theme = "DarkMode_Explorer")
        {
            void Apply() { try { SetWindowTheme(c.Handle, theme, null); } catch (Exception) { } }
            if (c.IsHandleCreated) Apply();
            c.HandleCreated += (s, e) => Apply();
        }

        public enum Glyph { Screen, Gamepad }

        /// <summary>Simple line/solid pictures for the menu cards, drawn inside <paramref name="r"/>.</summary>
        public static void DrawGlyph(Graphics g, Glyph glyph, RectangleF r, Color color)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(r.Width / 100f, r.Height / 100f);
            using (var pen = new Pen(color, 8) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(color))
            {
                if (glyph == Glyph.Screen)
                {
                    using (var screen = RoundRect(new RectangleF(8, 14, 84, 56), 8)) g.DrawPath(pen, screen);
                    g.DrawLine(pen, 50, 70, 50, 84);
                    g.DrawLine(pen, 32, 86, 68, 86);
                    g.FillPolygon(brush, new[] { new PointF(43, 30), new PointF(62, 42), new PointF(43, 54) });
                }
                else
                {
                    using (var body = new GraphicsPath { FillMode = FillMode.Winding })
                    {
                        body.AddPath(RoundRect(new RectangleF(6, 28, 88, 36), 18), false);
                        body.AddEllipse(4, 40, 32, 32);
                        body.AddEllipse(64, 40, 32, 32);
                        g.FillPath(brush, body);
                    }
                    using (var cut = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        g.FillRectangle(cut, 22, 36, 7, 20);
                        g.FillRectangle(cut, 15.5f, 42.5f, 20, 7);
                        g.FillEllipse(cut, 68, 34, 8, 8);
                        g.FillEllipse(cut, 68, 50, 8, 8);
                        g.FillEllipse(cut, 60, 42, 8, 8);
                        g.FillEllipse(cut, 76, 42, 8, 8);
                    }
                }
            }
            g.Restore(st);
        }
    }

    public enum ButtonKind { Primary, Secondary, Danger, Ghost }

    /// <summary>Flat rounded button with hover / pressed colours.</summary>
    sealed class FlatButton : Button
    {
        bool hover, down;
        public ButtonKind Kind { get; set; }

        public FlatButton(string text, ButtonKind kind)
        {
            Text = text;
            Kind = kind;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = Theme.Font(10f, FontStyle.Bold);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill, fg = Color.White;
            switch (Kind)
            {
                case ButtonKind.Primary: fill = Theme.Accent; break;
                case ButtonKind.Danger: fill = Color.FromArgb(200, 45, 45); break;
                case ButtonKind.Ghost: fill = Parent?.BackColor ?? Theme.Back; fg = Theme.Muted; break;
                default: fill = Theme.SurfaceHi; fg = Theme.Text; break;
            }
            if (!Enabled) { fill = Theme.Blend(fill, Theme.Back, 0.5f); fg = Theme.Muted; }
            else if (down) fill = Theme.Blend(fill, Color.Black, 0.18f);
            else if (hover) fill = Kind == ButtonKind.Ghost ? Theme.SurfaceHi : Theme.Blend(fill, Color.White, 0.12f);
            if (Kind == ButtonKind.Ghost && hover) fg = Theme.Text;

            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(r, 7 * s))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, path);
                if (Kind == ButtonKind.Secondary || Kind == ButtonKind.Ghost)
                    using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f * s)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Big menu choice: picture, title and a short line under it.</summary>
    sealed class CardButton : Button
    {
        bool hover, down;
        readonly Theme.Glyph glyph;
        readonly Color accent;
        readonly string title, subtitle;
        readonly Font titleFont = Theme.Font(16f, FontStyle.Bold), subFont = Theme.Font(10f);

        public CardButton(string title, string subtitle, Theme.Glyph glyph, Color accent)
        {
            this.title = title;
            this.subtitle = subtitle;
            this.glyph = glyph;
            this.accent = accent;
            Text = title;
            AccessibleName = title + ". " + subtitle;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); subFont.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool lit = hover || (Focused && ShowFocusCues);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.RoundRect(r, 14 * s))
            {
                Color top = lit ? Theme.Blend(Theme.SurfaceHi, accent, 0.10f) : Theme.Surface;
                if (down) top = Theme.Blend(top, Color.Black, 0.15f);
                using (var b = new LinearGradientBrush(r, top, Theme.Blend(top, Theme.Back, 0.4f), 90f)) g.FillPath(b, path);
                using (var pen = new Pen(lit ? accent : Theme.Border, (lit ? 2f : 1f) * s)) g.DrawPath(pen, path);
            }

            var tile = new RectangleF(22 * s, 22 * s, 60 * s, 60 * s);
            using (var path = Theme.RoundRect(tile, 14 * s))
            using (var b = new LinearGradientBrush(tile, accent, Theme.Blend(accent, Theme.Violet, 0.6f), 45f))
                g.FillPath(b, path);
            var pic = tile;
            pic.Inflate(-13 * s, -13 * s);
            Theme.DrawGlyph(g, glyph, pic, Color.White);

            int x = (int)(22 * s), y = (int)(96 * s), w = Width - 2 * x;
            Size ts = TextRenderer.MeasureText(title, titleFont);
            TextRenderer.DrawText(g, title, titleFont, new Point(x - (int)(3 * s), y), Theme.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, subtitle, subFont, new Rectangle(x, y + ts.Height, w, Height - y - ts.Height), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            // arrow in the top right corner
            using (var pen = new Pen(lit ? accent : Theme.Muted, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float ax = Width - 34 * s, ay = 44 * s;
                g.DrawLine(pen, ax, ay + 8 * s, ax + 12 * s, ay + 8 * s);
                g.DrawLines(pen, new[] { new PointF(ax + 7 * s, ay + 3 * s), new PointF(ax + 12 * s, ay + 8 * s), new PointF(ax + 7 * s, ay + 13 * s) });
            }
        }
    }

    /// <summary>
    /// Shows the key(s) for one controller button like a keycap. Click, then press a key to choose it
    /// (left / right Shift, Ctrl and Alt are told apart); Esc or a second click cancels; right-click clears.
    /// </summary>
    sealed class KeyButton : Button
    {
        const int WM_SYSKEYUP = 0x0105;
        static readonly Font WaitFont = Theme.Font(9f, FontStyle.Italic);
        bool capturing, hover;
        Keys swallowUp = Keys.None;
        Keys[] keys = new Keys[0];

        public KeyButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = Theme.Font(10f, FontStyle.Bold);
            Size = new Size(132, 30);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public Keys[] Value
        {
            get => keys;
            set { keys = value ?? new Keys[0]; Invalidate(); }
        }

        /// <summary>A key was pressed while waiting (left / right modifier already resolved).</summary>
        public event Action<KeyButton, Keys> KeyChosen;
        /// <summary>Right-click: no key for this button.</summary>
        public event Action<KeyButton> Cleared;

        public bool Capturing => capturing;

        public void Cancel()
        {
            if (!capturing) return;
            capturing = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            capturing = !capturing;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            capturing = false;
            Cleared?.Invoke(this);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Cancel();
        }

        // while waiting, every key (Tab, arrows, Enter...) comes here instead of moving the focus
        protected override bool IsInputKey(Keys keyData) => capturing || base.IsInputKey(keyData);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            !capturing && base.ProcessCmdKey(ref msg, keyData);

        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            if (capturing) e.IsInputKey = true;
            base.OnPreviewKeyDown(e);
        }

        protected override bool ProcessDialogKey(Keys keyData) => !capturing && base.ProcessDialogKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!capturing) { base.OnKeyDown(e); return; }
            e.Handled = e.SuppressKeyPress = true;
            capturing = false;
            swallowUp = e.KeyCode; // so its key-up does not click the button (Space) or open the window menu (Alt, F10)
            Invalidate();
            if (e.KeyCode == Keys.Escape) return;
            KeyChosen?.Invoke(this, Resolve(e.KeyCode));
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (swallowUp != Keys.None && e.KeyCode == swallowUp)
            {
                swallowUp = Keys.None;
                e.Handled = true;
                return;
            }
            base.OnKeyUp(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSKEYUP && swallowUp != Keys.None)
            {
                swallowUp = Keys.None;
                return;
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        static bool Down(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;

        static Keys Resolve(Keys k)
        {
            switch (k)
            {
                case Keys.ShiftKey:
                    return Down(Keys.RShiftKey) ? Keys.RShiftKey : Keys.LShiftKey;
                case Keys.ControlKey:
                    return Down(Keys.RControlKey) ? Keys.RControlKey : Keys.LControlKey;
                case Keys.Menu:
                    return Down(Keys.RMenu) ? Keys.RMenu : Keys.LMenu;
                default:
                    return k;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = capturing ? Theme.Blend(Theme.SurfaceHi, Theme.Accent, 0.25f) : hover ? Theme.Border : Theme.SurfaceHi;
            using (var path = Theme.RoundRect(r, 6 * s))
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(capturing ? Theme.Accent : Theme.Border, (capturing ? 1.8f : 1f) * s))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
                if (!capturing && Focused && ShowFocusCues)
                    using (var focus = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f * s)) g.DrawPath(focus, path);
            }
            // keycap bottom edge
            if (!capturing)
                using (var edge = new Pen(Theme.Blend(Theme.Border, Theme.Back, 0.3f), 2f * s))
                    g.DrawLine(edge, 6 * s, Height - 1.5f * s, Width - 6 * s, Height - 1.5f * s);

            string text = capturing ? "Press a key..." : keys.Length == 0 ? "—" : KeyMap.DisplayText(keys);
            Color fg = capturing ? Theme.Accent : keys.Length == 0 ? Theme.Muted : Theme.Text;
            TextRenderer.DrawText(g, text, capturing ? WaitFont : Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>On / off switch (a CheckBox drawn as a sliding pill).</summary>
    sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            AutoSize = false;
            Size = new Size(46, 24);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(track, track.Height / 2))
            using (var b = new SolidBrush(Checked ? Theme.Accent : Theme.Border))
            {
                g.FillPath(b, path);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f * s)) g.DrawPath(pen, path);
            }
            float d = Height - 7 * s;
            float x = Checked ? Width - d - 3.5f * s : 3.5f * s;
            g.FillEllipse(Brushes.White, x, (Height - d) / 2f, d, d);
        }
    }

    /// <summary>Rounded panel with a thin border; child labels pick up its background.</summary>
    sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            BackColor = Theme.Surface;
            Padding = new Padding(16, 12, 16, 12);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12 * DeviceDpi / 96f))
            using (var b = new SolidBrush(BackColor))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Label on a rounded coloured background (used for PAD MODE ON / OFF).</summary>
    sealed class Pill : Label
    {
        Color fill = Theme.SurfaceHi;
        public Color Fill { get => fill; set { fill = value; Invalidate(); } }

        public Pill()
        {
            AutoSize = true;
            Padding = new Padding(16, 7, 16, 7);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Label with a coloured status dot in front of the text.</summary>
    sealed class DotLabel : Label
    {
        Color dot = Theme.Muted;
        public Color DotColor { get => dot; set { dot = value; Invalidate(); } }

        public DotLabel()
        {
            AutoSize = true;
            Padding = new Padding(20, 0, 0, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float s = DeviceDpi / 96f, d = 9 * s;
            float y = (Font.Height - d) / 2f + Padding.Top;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var glow = new SolidBrush(Color.FromArgb(60, dot))) e.Graphics.FillEllipse(glow, 2 * s - 3 * s, y - 3 * s, d + 6 * s, d + 6 * s);
            using (var b = new SolidBrush(dot)) e.Graphics.FillEllipse(b, 2 * s, y, d, d);
        }
    }

    /// <summary>The app icon drawn smoothly at any size.</summary>
    sealed class LogoBox : Control
    {
        public LogoBox(int size)
        {
            Size = new Size(size, size);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Back);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(Theme.Logo, new Rectangle(0, 0, Width, Height));
        }
    }

    /// <summary>
    /// Dark details list: custom header and rows, taller rows, the last column fills the width,
    /// and <see cref="EmptyText"/> is shown when there are no rows. A sub-item with its own
    /// ForeColor (set UseItemStyleForSubItems = false) gets a status dot in that colour.
    /// </summary>
    sealed class DarkListView : ListView
    {
        readonly Font headerFont = Theme.Font(8.5f, FontStyle.Bold);
        bool fitting;
        public string EmptyText { get; set; } = "";

        public DarkListView()
        {
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            OwnerDraw = true;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            SmallImageList = new ImageList { ImageSize = new Size(1, 30) }; // row height
            // ItemsView: dark scroll bars without the Explorer theme's column lines in the empty area
            Theme.DarkScrollBars(this, "DarkMode_ItemsView");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { headerFont.Dispose(); SmallImageList?.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            float s = DeviceDpi / 96f;
            SmallImageList.ImageSize = new Size(1, (int)(30 * s));
            FitColumns();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitColumns();
        }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            FitColumns();
        }

        void FitColumns()
        {
            if (fitting || Columns.Count == 0) return;
            fitting = true;
            int used = 0;
            for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            int last = Math.Max(60, ClientSize.Width - used);
            if (Columns[Columns.Count - 1].Width != last) Columns[Columns.Count - 1].Width = last;
            fitting = false;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.SurfaceHi)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds;
            r.X += 12;
            r.Width -= 12;
            TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), headerFont, r, Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e) { } // rows are drawn per sub-item

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            Color bg = e.Item.Selected ? Theme.Blend(Theme.Surface, Theme.Accent, 0.38f)
                     : e.ItemIndex % 2 == 1 ? Theme.Blend(Theme.Surface, Theme.SurfaceHi, 0.45f) : Theme.Surface;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);

            var r = e.Bounds;
            r.X += 12;
            r.Width -= 14;
            bool hasDot = !e.Item.UseItemStyleForSubItems && e.SubItem.ForeColor.ToArgb() != ForeColor.ToArgb();
            if (hasDot)
            {
                float s = DeviceDpi / 96f, d = 8 * s;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(e.SubItem.ForeColor))
                    g.FillEllipse(b, r.X, r.Y + (r.Height - d) / 2f, d, d);
                r.X += (int)(16 * s);
                r.Width -= (int)(16 * s);
            }
            TextRenderer.DrawText(g, e.SubItem.Text, Font, r, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        int HeaderHeight()
        {
            const int LVM_GETHEADER = 0x101F;
            IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            return header != IntPtr.Zero && GetWindowRect(header, out RECT r) ? r.Bottom - r.Top : 0;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_PAINT = 0x000F;
            if (m.Msg != WM_PAINT) return;
            // below the last row Windows draws column lines: paint that area plain, with the empty-list text
            int top = Items.Count > 0 ? Items[Items.Count - 1].Bounds.Bottom : HeaderHeight();
            if (top >= ClientSize.Height) return;
            var rest = new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
            using (var g = CreateGraphics())
            using (var b = new SolidBrush(BackColor))
            {
                g.FillRectangle(b, rest);
                if (Items.Count == 0 && EmptyText.Length > 0)
                {
                    rest.Inflate(-16, 0);
                    TextRenderer.DrawText(g, EmptyText, Font, rest, Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    /// <summary>Dark drop-down list. Games show a green check when found on this PC and their player count / layout.</summary>
    sealed class DarkComboBox : ComboBox
    {
        public DarkComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.SurfaceHi;
            ForeColor = Theme.Text;
            Font = Theme.Font(10.5f);
            ItemHeight = 26;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ItemHeight = (int)(26 * DeviceDpi / 96f);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_PAINT = 0x000F;
            if (m.Msg != WM_PAINT) return;
            // Flat style still draws a white frame and arrow button: draw dark ones over them
            float s = DeviceDpi / 96f;
            int bw = SystemInformation.VerticalScrollBarWidth + 2;
            using (var g = CreateGraphics())
            using (var back = new SolidBrush(Theme.SurfaceHi))
            using (var frame = new Pen(Theme.Border))
            using (var inner = new Pen(Theme.SurfaceHi))
            using (var arrow = new Pen(Theme.Muted, 1.8f * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                g.FillRectangle(back, Width - bw - 1, 1, bw, Height - 2);
                g.DrawRectangle(inner, 1, 1, Width - 3, Height - 3);
                g.DrawRectangle(frame, 0, 0, Width - 1, Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Width - 1 - bw / 2f, cy = Height / 2f;
                g.DrawLines(arrow, new[] { new PointF(cx - 4 * s, cy - 2 * s), new PointF(cx, cy + 2 * s), new PointF(cx + 4 * s, cy - 2 * s) });
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (var b = new SolidBrush(sel ? Theme.Blend(Theme.SurfaceHi, Theme.Accent, 0.55f) : Theme.SurfaceHi))
                g.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;

            object item = Items[e.Index];
            var r = e.Bounds;
            r.X += 6;
            r.Width -= 8;
            const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            if (item is GameProfile game)
            {
                int check = (int)(20 * DeviceDpi / 96f);
                if (game.Installed)
                    TextRenderer.DrawText(g, "✔", Font, new Rectangle(r.X, r.Y, check, r.Height), Theme.Good, flags);
                string right = $"{game.Players}P · {game.Layout}";
                Size rs = TextRenderer.MeasureText(right, Font);
                TextRenderer.DrawText(g, right, Font, new Rectangle(r.Right - rs.Width, r.Y, rs.Width, r.Height), sel ? Theme.Text : Theme.Muted, flags);
                TextRenderer.DrawText(g, game.Name, Font, new Rectangle(r.X + check, r.Y, r.Width - check - rs.Width - 8, r.Height), Theme.Text, flags);
            }
            else
            {
                TextRenderer.DrawText(g, GetItemText(item), Font, r, Theme.Text, flags);
            }
        }
    }
}
