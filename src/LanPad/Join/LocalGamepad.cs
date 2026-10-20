using System;
using System.Runtime.InteropServices;
using System.Threading;
using LanPad.Protocol;

namespace LanPad.Join
{
    /// <summary>
    /// A real Xbox (XInput) controller plugged into the joined PC. Polls it every few ms and raises
    /// <see cref="Changed"/> when its state changes, so it is sent to the host together with the keyboard.
    /// The first connected controller is used; it can be plugged in or out at any time.
    /// </summary>
    public sealed class LocalGamepad : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState14(uint user, out XINPUT_STATE state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState910(uint user, out XINPUT_STATE state);

        const uint ERROR_SUCCESS = 0;
        const int PollMs = 4, SearchMs = 1000;

        // Windows' recommended dead zones: a worn stick resting slightly off centre must not walk the player
        const int LeftDeadZone = 7849, RightDeadZone = 8689, TriggerThreshold = 30;

        readonly Thread thread;
        readonly object gate = new object();
        volatile bool running = true;
        bool useOld; // xinput1_4 missing (Windows 7): use xinput9_1_0
        volatile int user = -1;
        PadState state;

        /// <summary>Raised on the poll thread when the controller state changes (or it is unplugged).</summary>
        public event Action Changed;

        public LocalGamepad()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "LanPad local controller" };
            thread.Start();
        }

        /// <summary>Current state of the local controller (neutral when none is plugged in).</summary>
        public PadState State
        {
            get { lock (gate) return state; }
        }

        public bool Connected => user >= 0;

        void Loop()
        {
            using (Tweaks.GameThread())
            {
                uint lastPacket = 0;
                DateTime nextSearch = DateTime.MinValue;
                while (running)
                {
                    if (user < 0)
                    {
                        if (DateTime.UtcNow >= nextSearch)
                        {
                            nextSearch = DateTime.UtcNow.AddMilliseconds(SearchMs);
                            for (uint i = 0; i < 4 && user < 0; i++)
                                if (Read(i, out _)) user = (int)i;
                            if (user >= 0) Log.Info($"Xbox controller found on this PC (XInput {user + 1}): it controls your player too");
                        }
                        Thread.Sleep(PollMs);
                        continue;
                    }

                    if (!Read((uint)user, out var x))
                    {
                        Log.Info("Xbox controller on this PC unplugged");
                        user = -1;
                        Set(PadState.Neutral);
                        continue;
                    }
                    if (x.dwPacketNumber != lastPacket)
                    {
                        lastPacket = x.dwPacketNumber;
                        Set(ToPad(x.Gamepad));
                    }
                    Thread.Sleep(PollMs);
                }
            }
        }

        bool Read(uint index, out XINPUT_STATE x)
        {
            x = default(XINPUT_STATE);
            try
            {
                if (!useOld) return GetState14(index, out x) == ERROR_SUCCESS;
            }
            catch (DllNotFoundException)
            {
                useOld = true;
            }
            catch (EntryPointNotFoundException)
            {
                useOld = true;
            }
            try
            {
                return GetState910(index, out x) == ERROR_SUCCESS;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                if (running) Log.Warn("XInput is missing on this PC: a plugged-in Xbox controller cannot be used");
                running = false;
                return false;
            }
        }

        void Set(PadState s)
        {
            lock (gate)
            {
                if (state == s) return;
                state = s;
            }
            Changed?.Invoke();
        }

        static PadState ToPad(XINPUT_GAMEPAD g)
        {
            var s = new PadState
            {
                Buttons = (PadButtons)g.wButtons & ~PadButtons.Guide,
                LeftTrigger = g.bLeftTrigger > TriggerThreshold ? g.bLeftTrigger : (byte)0,
                RightTrigger = g.bRightTrigger > TriggerThreshold ? g.bRightTrigger : (byte)0,
            };
            DeadZone(g.sThumbLX, g.sThumbLY, LeftDeadZone, out s.LeftX, out s.LeftY);
            DeadZone(g.sThumbRX, g.sThumbRY, RightDeadZone, out s.RightX, out s.RightY);
            return s;
        }

        static void DeadZone(short x, short y, int zone, out short ox, out short oy)
        {
            bool inside = (long)x * x + (long)y * y < (long)zone * zone;
            ox = inside ? (short)0 : x;
            oy = inside ? (short)0 : y;
        }

        /// <summary>Keyboard and controller together: buttons of both, the stronger trigger, the keyboard's stick if it is used.</summary>
        public static PadState Merge(PadState keys, PadState pad)
        {
            var s = keys;
            s.Buttons |= pad.Buttons;
            s.LeftTrigger = Math.Max(keys.LeftTrigger, pad.LeftTrigger);
            s.RightTrigger = Math.Max(keys.RightTrigger, pad.RightTrigger);
            if (keys.LeftX == 0 && keys.LeftY == 0) { s.LeftX = pad.LeftX; s.LeftY = pad.LeftY; }
            if (keys.RightX == 0 && keys.RightY == 0) { s.RightX = pad.RightX; s.RightY = pad.RightY; }
            return s;
        }

        public void Dispose()
        {
            running = false;
            thread.Join(200);
        }
    }
}
