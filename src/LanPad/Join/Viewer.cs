using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using LanPad.Host;

namespace LanPad.Join
{
    /// <summary>
    /// Shows the host's stream: one full-screen video player and one hidden sound player
    /// (separate streams, so video never waits for audio). Uses mpv, or ffplay as fallback.
    /// All player shortcuts are disabled so game keys can never pause or close it.
    /// Players that exit are restarted while we are still joined.
    ///
    /// Low-latency settings (measured on this hardware with --streamtest):
    ///  - software decoding + low_delay: ~0.12 s glass-to-glass; d3d11va hardware decoding added ~0.25 s
    ///  - no vsync wait, no frame queue: a slow display otherwise makes the delay grow without limit
    /// </summary>
    public sealed class Viewer : IDisposable
    {
        sealed class Player
        {
            public string Name;
            public string Exe;
            public string Args;
            public Process Proc;
            public DateTime Started;
            public int QuickExits;
        }

        readonly string preferred;
        readonly object gate = new object();
        readonly List<Player> players = new List<Player>();
        Thread watchdog;
        volatile bool wanted;
        volatile bool away;

        public Viewer(string preferred)
        {
            this.preferred = (preferred ?? "mpv").ToLowerInvariant();
        }

        /// <summary>Extra options for the video player (tests / tuning), added before the URL.</summary>
        public string ExtraArgs { get; set; } = "";

        public static string FindMpv() => AppPaths.FindTool(@"mpv\mpv.exe", "mpv.exe");
        public static string FindFfplay() => AppPaths.FindTool("ffplay.exe", @"ffmpeg\ffplay.exe");

        /// <summary>True while the video player is running.</summary>
        public bool IsRunning
        {
            get
            {
                lock (gate) return players.Count > 0 && players[0].Proc != null && !players[0].Proc.HasExited;
            }
        }

        public bool Start(string group, int port, IPAddress localAddress, out string error)
            => Start(group, port, localAddress, out error, fullscreen: true, mute: false, geometry: null);

        /// <summary>Test variant: windowed (geometry like "960x540+960+270") and/or muted.</summary>
        public bool Start(string group, int port, IPAddress localAddress, out string error, bool fullscreen, bool mute, string geometry)
        {
            error = null;
            Stop();
            string Url(int p) => $"udp://{group}:{p}?localaddr={localAddress}&overrun_nonfatal=1&fifo_size=500000&buffer_size=4194304";
            string videoUrl = Url(port);
            string audioUrl = Url(StreamBroadcaster.AudioPort(port));
            string mpv = FindMpv();
            string ffplay = FindFfplay();
            bool useMpv = mpv != null && (preferred != "ffplay" || ffplay == null);

            var list = new List<Player>();
            if (useMpv)
            {
                string log = Path.Combine(AppPaths.TempDir, "LanPad-mpv.log");
                string common = "--profile=low-latency --untimed --no-cache --cache-pause=no --idle=no --keep-open=no " +
                                "--no-input-default-bindings --input-vo-keyboard=no --input-cursor=no --msg-level=all=warn";
                string screen = fullscreen ? "--fs" : $"--geometry={geometry ?? "50%x50%"}";
                list.Add(new Player
                {
                    Name = "video",
                    Exe = mpv,
                    Args = string.Join(" ", common, "--no-audio",
                        "--video-sync=desync", "--framedrop=decoder+vo", "--d3d11-sync-interval=0", "--swapchain-depth=1",
                        "--hwdec=no", "--vd-lavc-o=flags=+low_delay",
                        screen, "--ontop", "--no-border", "--no-osc", "--osd-level=0", "--cursor-autohide=always",
                        "--force-window=immediate", "--title=LanPad",
                        $"\"--log-file={log}\"", ExtraArgs ?? "", $"\"{videoUrl}\""),
                });
                list.Add(new Player
                {
                    Name = "sound",
                    Exe = mpv,
                    Args = string.Join(" ", common, "--no-video", "--force-window=no", "--audio-buffer=0.05",
                        mute ? "--mute=yes" : "", $"\"{audioUrl}\""),
                });
            }
            else if (ffplay != null)
            {
                const string low = "-hide_banner -loglevel error -fflags nobuffer -flags low_delay -framedrop";
                list.Add(new Player
                {
                    Name = "video",
                    Exe = ffplay,
                    Args = string.Join(" ", low, "-an -sync ext", fullscreen ? "-fs" : FfplayGeometry(geometry),
                        "-alwaysontop -noborder -window_title LanPad", ExtraArgs ?? "", $"\"{videoUrl}\""),
                });
                if (!mute)
                    list.Add(new Player { Name = "sound", Exe = ffplay, Args = $"{low} -nodisp -vn \"{audioUrl}\"" });
            }
            else
            {
                error = "No viewer found. Put mpv in the tools\\mpv folder (or ffplay.exe in tools).";
                return false;
            }

            lock (gate)
            {
                players.Clear();
                players.AddRange(list);
            }
            wanted = true;
            foreach (var p in list) Launch(p);
            if (watchdog == null)
            {
                watchdog = new Thread(Watch) { IsBackground = true, Name = "LanPad viewer watchdog" };
                watchdog.Start();
            }
            Log.Info($"Viewer started ({Path.GetFileName(list[0].Exe)}): video {videoUrl}, sound {audioUrl}");
            return true;
        }

        /// <summary>mpv-style "WxH+X+Y" to ffplay's -x -y -left -top.</summary>
        static string FfplayGeometry(string geometry)
        {
            var m = Regex.Match(geometry ?? "", @"^(\d+)x(\d+)\+(\d+)\+(\d+)$");
            return m.Success
                ? $"-x {m.Groups[1].Value} -y {m.Groups[2].Value} -left {m.Groups[3].Value} -top {m.Groups[4].Value}"
                : "";
        }

        void Launch(Player p)
        {
            lock (gate)
            {
                if (!wanted) return;
                try
                {
                    p.Proc = Process.Start(new ProcessStartInfo(p.Exe, p.Args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(p.Exe),
                    });
                    p.Started = DateTime.UtcNow;
                    Tweaks.Boost(p.Proc, p.Name + " player");
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not start the {p.Name} player", ex);
                    p.Proc = null;
                }
            }
        }

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        const int SW_MINIMIZE = 6, SW_RESTORE = 9;

        /// <summary>
        /// "On my PC": minimizes the video window (the sound keeps playing), and keeps it minimized even if
        /// the watchdog restarts the player. False = restore it full screen and on top again.
        /// </summary>
        public void SetAway(bool value)
        {
            away = value;
            IntPtr h = VideoWindow();
            if (h == IntPtr.Zero) return;
            if (value)
            {
                ShowWindow(h, SW_MINIMIZE);
            }
            else
            {
                const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;
                ShowWindow(h, SW_RESTORE);
                SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);
            }
        }

        /// <summary>True while the video window is minimized (tests).</summary>
        public bool IsVideoMinimized
        {
            get
            {
                IntPtr h = VideoWindow();
                return h != IntPtr.Zero && IsIconic(h);
            }
        }

        IntPtr VideoWindow()
        {
            Process p;
            lock (gate) p = players.Count > 0 ? players[0].Proc : null;
            try
            {
                if (p == null || p.HasExited) return IntPtr.Zero;
                p.Refresh();
                return p.MainWindowHandle;
            }
            catch (InvalidOperationException) { return IntPtr.Zero; }
        }

        void Watch()
        {
            while (true)
            {
                Thread.Sleep(1000);
                if (!wanted) continue;
                if (away)
                {
                    // a restarted player opens full screen again: put it back down while the player is away
                    IntPtr h = VideoWindow();
                    if (h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h)) ShowWindow(h, SW_MINIMIZE);
                }
                List<Player> snapshot;
                lock (gate) snapshot = new List<Player>(players);
                foreach (var p in snapshot)
                {
                    bool exited;
                    lock (gate) exited = p.Proc == null || p.Proc.HasExited;
                    if (!exited || !wanted) continue;

                    p.QuickExits = DateTime.UtcNow - p.Started < TimeSpan.FromSeconds(5) ? p.QuickExits + 1 : 0;
                    if (p.QuickExits >= 3)
                    {
                        Log.Warn($"The {p.Name} player keeps closing; waiting 5 s before trying again");
                        p.QuickExits = 0;
                        p.Started = DateTime.UtcNow.AddSeconds(5);
                        continue;
                    }
                    if (DateTime.UtcNow < p.Started) continue;
                    Log.Info($"The {p.Name} player closed; starting it again");
                    Launch(p);
                }
            }
        }

        public void Stop()
        {
            wanted = false;
            away = false;
            lock (gate)
            {
                foreach (var p in players)
                {
                    try
                    {
                        if (p.Proc != null && !p.Proc.HasExited) p.Proc.Kill();
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    p.Proc = null;
                }
                players.Clear();
            }
        }

        public void Dispose() => Stop();
    }
}
