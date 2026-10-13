using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using LanPad.Config;

namespace LanPad
{
    /// <summary>
    /// Windows tweaks for smooth play while hosting or joined. Each one is a setting in [Performance]
    /// of lanpad.ini (and on the Settings page):
    ///  - HighPriority: LanPad (keyboard hook, input threads) runs at High priority, ffmpeg / mpv at
    ///    Above normal, and the input threads join the "Games" multimedia class (MMCSS).
    ///  - NoThrottling: Windows 11 slows down hidden / background processes (EcoQoS) and ignores their
    ///    timer requests. ffmpeg, the sound player and LanPad behind the game are exactly that, so
    ///    they opt out, and the Windows timer runs at 1 ms so short waits are exact.
    ///  - KeepAwake: no sleep, screen saver or screen off during a game.
    /// Everything is undone when the last session ends. Failures are logged and ignored.
    /// </summary>
    public static class Tweaks
    {
        static readonly object gate = new object();
        static int sessions;

        public static bool HighPriority = true;
        public static bool NoThrottling = true;
        public static bool KeepAwake = true;

        public static void Configure(LanPadConfig cfg)
        {
            HighPriority = cfg.HighPriority;
            NoThrottling = cfg.NoThrottling;
            KeepAwake = cfg.KeepAwake;
        }

        /// <summary>Start of hosting / joining. Call from the UI thread; dispose the result at the end.</summary>
        public static IDisposable BeginSession()
        {
            lock (gate)
            {
                if (sessions++ == 0) Apply(true);
            }
            return new Ender(() =>
            {
                lock (gate)
                {
                    if (--sessions == 0) Apply(false);
                }
            });
        }

        // what the running session changed, so the end undoes exactly that (settings may change meanwhile)
        static bool prioritySet, timerSet, awakeSet;

        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

        static void Apply(bool on)
        {
            var self = Process.GetCurrentProcess();
            if (on)
            {
                var applied = new System.Collections.Generic.List<string>();
                if (HighPriority && Try("process priority", () => self.PriorityClass = ProcessPriorityClass.High))
                {
                    prioritySet = true;
                    applied.Add("high priority");
                }
                if (NoThrottling)
                {
                    bool ok = false;
                    if (Try("power throttling", () => ok = SetThrottling(self.Handle, false)) && ok) applied.Add("no power throttling");
                    if (Try("timer resolution", () => timeBeginPeriod(1)))
                    {
                        timerSet = true;
                        applied.Add("1 ms timer");
                    }
                }
                if (KeepAwake && Try("keep awake", () => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)))
                {
                    awakeSet = true;
                    applied.Add("keep PC awake");
                }
                Log.Info("Smooth-play tweaks on: " + (applied.Count > 0 ? string.Join(", ", applied) : "none (all off in Settings)"));
            }
            else
            {
                if (prioritySet) Try("process priority", () => self.PriorityClass = ProcessPriorityClass.Normal);
                if (timerSet) Try("timer resolution", () => timeEndPeriod(1));
                if (awakeSet) Try("keep awake", () => SetThreadExecutionState(ES_CONTINUOUS));
                prioritySet = timerSet = awakeSet = false;
                Log.Info("Smooth-play tweaks off");
            }
        }

        /// <summary>Tweaks for a helper process (ffmpeg / mpv) we started.</summary>
        public static void Boost(Process p, string name)
        {
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
                if (HighPriority) p.PriorityClass = ProcessPriorityClass.AboveNormal;
                if (NoThrottling) SetThrottling(p.Handle, false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not tune the {name} process: {ex.Message}");
            }
        }

        /// <summary>
        /// For a latency-critical thread (input receive / send): highest priority and the MMCSS "Games"
        /// class while it runs. Call at the start of the thread, dispose at its end (same thread).
        /// </summary>
        public static IDisposable GameThread()
        {
            if (!HighPriority) return new Ender(null);
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch (Exception) { }
            IntPtr task = IntPtr.Zero;
            try
            {
                uint index = 0;
                task = AvSetMmThreadCharacteristics("Games", ref index);
            }
            catch (Exception) { } // avrt.dll missing (Server Core): priority alone is fine
            return new Ender(() =>
            {
                if (task != IntPtr.Zero) try { AvRevertMmThreadCharacteristics(task); } catch (Exception) { }
            });
        }

        static bool Try(string what, Action a)
        {
            try { a(); return true; }
            catch (Exception ex) { Log.Warn($"Tweak '{what}' not applied: {ex.Message}"); return false; }
        }

        sealed class Ender : IDisposable
        {
            Action end;
            public Ender(Action end) { this.end = end; }
            public void Dispose() { Interlocked.Exchange(ref end, null)?.Invoke(); }
        }

        // ------------------------------------------------------------------ Win32

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        const int ProcessPowerThrottling = 4;
        const uint THROTTLE_EXECUTION_SPEED = 0x1, THROTTLE_IGNORE_TIMER_RESOLUTION = 0x4;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        /// <summary>throttle = false: always full speed (no EcoQoS) and its timer requests are honoured.</summary>
        static bool SetThrottling(IntPtr process, bool throttle)
        {
            var s = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = THROTTLE_EXECUTION_SPEED | THROTTLE_IGNORE_TIMER_RESOLUTION,
                StateMask = throttle ? THROTTLE_EXECUTION_SPEED | THROTTLE_IGNORE_TIMER_RESOLUTION : 0,
            };
            int size = Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE));
            if (SetProcessInformation(process, ProcessPowerThrottling, ref s, size)) return true;
            // Windows 10 before 2004 does not know the timer flag
            s.ControlMask = THROTTLE_EXECUTION_SPEED;
            s.StateMask &= THROTTLE_EXECUTION_SPEED;
            return SetProcessInformation(process, ProcessPowerThrottling, ref s, size);
        }

        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint ms);

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll")]
        static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }
}
