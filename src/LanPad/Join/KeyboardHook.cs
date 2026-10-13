using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LanPad.Join
{
    /// <summary>
    /// Global low-level keyboard (and mouse) hook. Works while the stream viewer has focus.
    /// In Pad mode every key is captured for the controller and blocked from other programs,
    /// so typing W/A/S/D never reaches the viewer or Windows. Three LanPad hotkeys (Settings) work
    /// in and out of Pad mode: Pad mode on/off (F12), leave the game (Ctrl+F12), show / hide the
    /// key cheat sheet (Shift+F12) and switch to this PC / back to the game (Alt+F12).
    /// Must be installed on a thread with a message loop (the WinForms UI thread).
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const int WM_MOUSEMOVE = 0x0200;
        const uint LLKHF_INJECTED = 0x10;

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        // Keep the delegates referenced, otherwise the GC frees them while Windows still calls them.
        readonly HookProc keyboardProc;
        readonly HookProc mouseProc;
        IntPtr keyboardHook = IntPtr.Zero;
        IntPtr mouseHook = IntPtr.Zero;

        readonly bool[] down = new bool[256];
        int hotkeyHeldVk; // key of the hotkey being held: its repeats and key-up are swallowed too
        volatile bool padMode;

        /// <summary>LanPad shortcuts (key code + Ctrl / Shift / Alt flags), set in Settings > Hotkeys.</summary>
        public Keys PadKey { get; set; } = Keys.F12;
        public Keys LeaveKey { get; set; } = Keys.Control | Keys.F12;
        public Keys OverlayKey { get; set; } = Keys.Shift | Keys.F12;
        /// <summary>Switch to this PC (stream hidden, keyboard / mouse normal) and back to the game.</summary>
        public Keys SwitchKey { get; set; } = Keys.Alt | Keys.F12;
        public bool SwallowMouse { get; set; } = true;

        public enum Hotkey { None, PadMode, Leave, Overlay, Switch }

        /// <summary>Which LanPad shortcut a key press is: same key and exactly the same Ctrl / Shift / Alt.</summary>
        public static Hotkey Match(int vk, Keys modifiers, Keys padKey, Keys leaveKey, Keys overlayKey, Keys switchKey)
        {
            bool Is(Keys hotkey) => (int)(hotkey & Keys.KeyCode) == vk && (hotkey & Keys.Modifiers) == modifiers;
            if (Is(leaveKey)) return Hotkey.Leave;
            if (Is(overlayKey)) return Hotkey.Overlay;
            if (Is(switchKey)) return Hotkey.Switch;
            if (Is(padKey)) return Hotkey.PadMode;
            return Hotkey.None;
        }

        /// <summary>A captured key went down or up (raised on the UI thread, keep handlers fast).</summary>
        public event Action KeysChanged;
        public event Action<bool> PadModeChanged;
        public event Action LeaveRequested;
        public event Action OverlayToggleRequested;
        public event Action SwitchRequested;

        public KeyboardHook()
        {
            keyboardProc = KeyboardCallback;
            mouseProc = MouseCallback;
        }

        public bool PadMode
        {
            get => padMode;
            set
            {
                if (padMode == value) return;
                padMode = value;
                ClearKeys();
                PadModeChanged?.Invoke(value);
                KeysChanged?.Invoke();
            }
        }

        public void Install()
        {
            if (keyboardHook != IntPtr.Zero) return;
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook failed");
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            if (mouseHook == IntPtr.Zero) Log.Warn("Mouse hook failed; mouse clicks will not be blocked in Pad mode");
        }

        /// <summary>Is the key currently held (only tracked while in Pad mode)?</summary>
        public bool IsDown(Keys k)
        {
            int v = (int)k;
            return v > 0 && v < 256 && Volatile.Read(ref down[v]);
        }

        void ClearKeys()
        {
            for (int i = 0; i < down.Length; i++) Volatile.Write(ref down[i], false);
        }

        IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                int vk = (int)info.vkCode;
                bool injected = (info.flags & LLKHF_INJECTED) != 0;

                if (!injected && vk > 0 && vk < 256 && (isDown || isUp))
                {
                    if (vk == hotkeyHeldVk)
                    {
                        if (isUp) hotkeyHeldVk = 0;
                        return (IntPtr)1; // repeat / release of a hotkey: never pass it on
                    }
                    if (isDown)
                    {
                        var hit = Match(vk, Modifiers(), PadKey, LeaveKey, OverlayKey, SwitchKey);
                        if (hit != Hotkey.None)
                        {
                            hotkeyHeldVk = vk;
                            if (hit == Hotkey.Leave) LeaveRequested?.Invoke();
                            else if (hit == Hotkey.Overlay) OverlayToggleRequested?.Invoke();
                            else if (hit == Hotkey.Switch) SwitchRequested?.Invoke();
                            else PadMode = !PadMode;
                            return (IntPtr)1;
                        }
                    }

                    if (padMode)
                    {
                        bool was = Volatile.Read(ref down[vk]);
                        if (was != isDown)
                        {
                            Volatile.Write(ref down[vk], isDown);
                            KeysChanged?.Invoke();
                        }
                        return (IntPtr)1; // block the key for every other program
                    }
                }
            }
            return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && padMode && SwallowMouse && wParam.ToInt32() != WM_MOUSEMOVE)
                return (IntPtr)1; // block clicks and wheel, let the pointer move
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        static bool IsPhysicallyDown(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;

        // In Pad mode this hook blocks every key, so Windows' own key state may not see a held Ctrl / Shift:
        // check what the hook recorded too.
        bool Held(Keys k) => IsDown(k) || IsPhysicallyDown(k);

        Keys Modifiers()
        {
            Keys m = Keys.None;
            if (Held(Keys.LControlKey) || Held(Keys.RControlKey)) m |= Keys.Control;
            if (Held(Keys.LShiftKey) || Held(Keys.RShiftKey)) m |= Keys.Shift;
            if (Held(Keys.LMenu) || Held(Keys.RMenu)) m |= Keys.Alt;
            return m;
        }

        public void Dispose()
        {
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
        }
    }
}
