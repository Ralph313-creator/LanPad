using System;
using System.Drawing;
using System.Windows.Forms;

namespace LanPad.UI
{
    /// <summary>Small always-on-top message at the top of the screen that hides itself. Never takes focus.</summary>
    public sealed class ToastForm : Form
    {
        readonly Label label;
        readonly Timer hideTimer;

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 20, 20);
            Opacity = 0.92;
            Padding = new Padding(18, 10, 18, 10);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            label = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                Dock = DockStyle.Fill,
            };
            Controls.Add(label);

            hideTimer = new Timer { Interval = 2500 };
            hideTimer.Tick += (s, e) => { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ShowMessage(string text, Color accent, int ms = 2500)
        {
            label.Text = text;
            label.ForeColor = accent;
            var screen = Screen.PrimaryScreen.Bounds;
            if (!Visible) Show();
            PerformLayout();
            Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + 40);
            BringToFront();
            hideTimer.Stop();
            hideTimer.Interval = ms;
            hideTimer.Start();
        }
    }
}
