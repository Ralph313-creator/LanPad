using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LanPad.Config;
using LanPad.Host;
using LanPad.Join;
using LanPad.Net;

namespace LanPad
{
    /// <summary>
    /// LanPad.exe --streamtest [--noaudio] [--viewer=mpv|ffplay] [--ffopts="..."] [--vopts="..."] [--tag=name]
    /// Streams this PC's screen to itself and measures the delay: a big millisecond clock on the
    /// left, the received stream (windowed, muted) on the right, and a screenshot of both saved to
    /// %TEMP%\LanPad-streamtest-(tag).png. Delay = left clock - clock inside the stream window.
    /// </summary>
    sealed class ClockPanel : Panel
    {
        readonly Font font = new Font("Consolas", 96f, FontStyle.Bold);
        readonly StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        public long Value;

        public ClockPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            e.Graphics.DrawString(Value.ToString("000000"), font, Brushes.Lime, ClientRectangle, center);
        }
    }

    static class StreamTest
    {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        static string Arg(string[] args, string name, string def)
        {
            string a = args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            return a == null ? def : a.Substring(name.Length + 1).Trim('"');
        }

        public static int Run(LanPadConfig cfg, string[] args)
        {
            Application.EnableVisualStyles();
            timeBeginPeriod(1);
            string tag = Arg(args, "--tag", "run");
            var clock = Stopwatch.StartNew();
            var screen = Screen.PrimaryScreen.Bounds;
            var form = new Form
            {
                Text = "LanPad stream test clock",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(screen.Left, screen.Top + screen.Height / 4),
                Size = new Size(screen.Width / 2, screen.Height / 2),
                BackColor = Color.Black,
                TopMost = true,
                ShowInTaskbar = false,
            };
            // Double-buffered clock so a screen capture never catches a half-drawn (black) frame.
            var label = new ClockPanel { Dock = DockStyle.Fill };
            form.Controls.Add(label);
            var tick = new Timer { Interval = 1 };
            tick.Tick += (s, e) => { label.Value = clock.ElapsedMilliseconds; label.Invalidate(); };

            var lan = NetUtil.PrimaryLanAddress();
            string group = NetUtil.MulticastGroupFor(lan).ToString();
            var broadcaster = new StreamBroadcaster(cfg)
            {
                IncludeAudio = !args.Contains("--noaudio"),
                ExtraOutputArgs = Arg(args, "--ffopts", ""),
            };
            var viewer = new Viewer(Arg(args, "--viewer", cfg.Viewer)) { ExtraArgs = Arg(args, "--vopts", "") };
            string result = $"[{tag}] ";
            int code = 1;
            string shot = Path.Combine(Path.GetTempPath(), $"LanPad-streamtest-{tag}.png");

            var steps = new Timer { Interval = 500 };
            int step = 0;
            steps.Tick += (s, e) =>
            {
                step++;
                if (step == 1)
                {
                    if (!broadcaster.Start(group, cfg.StreamPort, lan, out string err))
                    {
                        result += "STREAM FAILED: " + err;
                        form.Close();
                        return;
                    }
                    result += $"stream ON ({broadcaster.EncoderInUse}, audio={broadcaster.IncludeAudio})";
                    string geometry = $"{screen.Width / 2}x{screen.Height / 2}+{screen.Width / 2}+{screen.Height / 4}";
                    if (!viewer.Start(group, cfg.StreamPort, lan, out string verr, fullscreen: false, mute: true, geometry: geometry))
                    {
                        result += " | VIEWER FAILED: " + verr;
                        form.Close();
                    }
                }
                else if (step == 18)
                {
                    // ~9 s in: half-size screenshot of the band that holds both clocks.
                    var band = new Rectangle(screen.Left, screen.Top + screen.Height / 4, screen.Width, screen.Height / 2);
                    using (var full = new Bitmap(band.Width, band.Height))
                    {
                        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(band.Location, Point.Empty, band.Size);
                        using (var half = new Bitmap(band.Width / 2, band.Height / 2))
                        {
                            using (var g = Graphics.FromImage(half))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(full, 0, 0, half.Width, half.Height);
                            }
                            half.Save(shot, ImageFormat.Png);
                        }
                    }
                    result += $" | screenshot {shot}";
                }
                else if (step >= 19)
                {
                    code = viewer.IsRunning && broadcaster.IsRunning ? 0 : 1;
                    result += $" | end: stream={broadcaster.IsRunning} viewer={viewer.IsRunning}";
                    form.Close();
                }
            };

            form.Shown += (s, e) => { tick.Start(); steps.Start(); };
            Application.Run(form);
            steps.Stop();
            tick.Stop();
            viewer.Stop();
            broadcaster.Stop();
            timeEndPeriod(1);

            File.AppendAllText(Path.Combine(Path.GetTempPath(), "LanPad-streamtest.txt"), result + Environment.NewLine);
            Log.Info("Stream test: " + result);
            return code;
        }
    }
}
