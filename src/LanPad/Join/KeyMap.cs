using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LanPad.Protocol;

namespace LanPad.Join
{
    public enum PadAction
    {
        LeftUp, LeftDown, LeftLeft, LeftRight,
        RightUp, RightDown, RightLeft, RightRight,
        A, B, X, Y,
        LB, RB, LT, RT,
        Start, Back, L3, R3,
        DUp, DDown, DLeft, DRight,
        Walk,
    }

    /// <summary>Which keyboard keys press which controller button / stick direction.</summary>
    public sealed class KeyMap
    {
        static readonly Keys[] None = new Keys[0];
        readonly Dictionary<PadAction, Keys[]> map = new Dictionary<PadAction, Keys[]>();

        public static KeyMap Default()
        {
            var m = new KeyMap();
            m.Set(PadAction.LeftUp, Keys.W);
            m.Set(PadAction.LeftDown, Keys.S);
            m.Set(PadAction.LeftLeft, Keys.A);
            m.Set(PadAction.LeftRight, Keys.D);
            m.Set(PadAction.RightUp, Keys.Up);
            m.Set(PadAction.RightDown, Keys.Down);
            m.Set(PadAction.RightLeft, Keys.Left);
            m.Set(PadAction.RightRight, Keys.Right);
            m.Set(PadAction.A, Keys.K);
            m.Set(PadAction.B, Keys.L);
            m.Set(PadAction.X, Keys.J);
            m.Set(PadAction.Y, Keys.I);
            m.Set(PadAction.LB, Keys.Q);
            m.Set(PadAction.RB, Keys.E);
            m.Set(PadAction.LT, Keys.Space);
            m.Set(PadAction.RT, Keys.LShiftKey);
            m.Set(PadAction.Start, Keys.Return);
            m.Set(PadAction.Back, Keys.Back);
            m.Set(PadAction.L3, Keys.R);
            m.Set(PadAction.R3, Keys.F);
            m.Set(PadAction.DUp, Keys.D1);
            m.Set(PadAction.DDown, Keys.D2);
            m.Set(PadAction.DLeft, Keys.D3);
            m.Set(PadAction.DRight, Keys.D4);
            m.Set(PadAction.Walk);
            return m;
        }

        public void Set(PadAction action, params Keys[] keys) => map[action] = keys ?? None;

        public Keys[] Get(PadAction action) => map.TryGetValue(action, out var k) ? k : None;

        public KeyMap Clone()
        {
            var m = new KeyMap();
            foreach (var kv in map) m.map[kv.Key] = (Keys[])kv.Value.Clone();
            return m;
        }

        /// <summary>Compact form sent from the host to joined PCs: count u16, then (action, key) byte pairs.</summary>
        public byte[] ToBytes()
        {
            var pairs = new List<byte>();
            foreach (PadAction a in Enum.GetValues(typeof(PadAction)))
                foreach (Keys k in Get(a))
                {
                    pairs.Add((byte)a);
                    pairs.Add((byte)k);
                }
            int count = pairs.Count / 2;
            var result = new byte[2 + pairs.Count];
            result[0] = (byte)(count & 0xFF);
            result[1] = (byte)(count >> 8);
            pairs.CopyTo(result, 2);
            return result;
        }

        /// <summary>Opposite of <see cref="ToBytes"/>. Returns null for broken data.</summary>
        public static KeyMap FromBytes(byte[] data)
        {
            if (data == null || data.Length < 2) return null;
            int count = data[0] | (data[1] << 8);
            if (data.Length != 2 + count * 2) return null;
            var lists = new Dictionary<PadAction, List<Keys>>();
            for (int i = 0; i < count; i++)
            {
                var action = (PadAction)data[2 + i * 2];
                var key = (Keys)data[3 + i * 2];
                if (!Enum.IsDefined(typeof(PadAction), action) || key == Keys.None) return null;
                if (!lists.TryGetValue(action, out var l)) lists[action] = l = new List<Keys>();
                l.Add(key);
            }
            var m = new KeyMap();
            foreach (PadAction a in Enum.GetValues(typeof(PadAction)))
                m.Set(a, lists.TryGetValue(a, out var l) ? l.ToArray() : None);
            return m;
        }

        /// <summary>Parses "K" or "K,NumPad2" or "Shift". Empty text = no key.</summary>
        public static bool TryParseKeyList(string text, out Keys[] keys, out string error)
        {
            var list = new List<Keys>();
            error = null;
            foreach (string part in (text ?? "").Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0) continue;
                if (!TryParseKey(name, list))
                {
                    error = $"unknown key '{name}'";
                    keys = None;
                    return false;
                }
            }
            keys = list.Distinct().ToArray();
            return true;
        }

        static bool TryParseKey(string name, List<Keys> outKeys)
        {
            switch (name.ToLowerInvariant())
            {
                case "enter": outKeys.Add(Keys.Return); return true;
                case "esc": outKeys.Add(Keys.Escape); return true;
                case "backspace": outKeys.Add(Keys.Back); return true;
                case "shift": outKeys.Add(Keys.LShiftKey); outKeys.Add(Keys.RShiftKey); return true;
                case "lshift": outKeys.Add(Keys.LShiftKey); return true;
                case "rshift": outKeys.Add(Keys.RShiftKey); return true;
                case "ctrl":
                case "control": outKeys.Add(Keys.LControlKey); outKeys.Add(Keys.RControlKey); return true;
                case "lctrl": outKeys.Add(Keys.LControlKey); return true;
                case "rctrl": outKeys.Add(Keys.RControlKey); return true;
                case "alt": outKeys.Add(Keys.LMenu); outKeys.Add(Keys.RMenu); return true;
                case "lalt": outKeys.Add(Keys.LMenu); return true;
                case "ralt": outKeys.Add(Keys.RMenu); return true;
                case "win": outKeys.Add(Keys.LWin); outKeys.Add(Keys.RWin); return true;
                case ";": outKeys.Add(Keys.OemSemicolon); return true;
                case ".": outKeys.Add(Keys.OemPeriod); return true;
                case "/": outKeys.Add(Keys.OemQuestion); return true;
            }
            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                outKeys.Add(Keys.D0 + (name[0] - '0'));
                return true;
            }
            if (Enum.TryParse(name, true, out Keys k) && (int)k > 0 && (int)k < 256)
            {
                outKeys.Add(k);
                return true;
            }
            return false;
        }

        public static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.Return: return "Enter";
                case Keys.Back: return "Backspace";
                case Keys.LShiftKey: return "L-Shift";
                case Keys.RShiftKey: return "R-Shift";
                case Keys.LControlKey: return "L-Ctrl";
                case Keys.RControlKey: return "R-Ctrl";
                case Keys.LMenu: return "L-Alt";
                case Keys.RMenu: return "R-Alt";
                case Keys.Escape: return "Esc";
                case Keys.OemSemicolon: return ";";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
            }
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            return k.ToString();
        }

        /// <summary>Keys for one action as shown on screen: "K", "K / NumPad2", or "" for none.</summary>
        public static string DisplayText(Keys[] keys) => string.Join(" / ", (keys ?? None).Select(KeyName));

        static bool IsModifier(Keys k) =>
            k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey ||
            k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey ||
            k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu || k == Keys.LWin || k == Keys.RWin;

        /// <summary>
        /// LanPad shortcut like "F12", "Ctrl+F12" or "Shift+Alt+P": one key, optionally with Ctrl / Shift / Alt.
        /// The result is the key code with WinForms modifier flags (Keys.Control | Keys.F12).
        /// </summary>
        public static bool TryParseHotkey(string text, out Keys hotkey, out string error)
        {
            hotkey = Keys.None;
            error = null;
            var parts = (text ?? "").Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) { error = "no key"; return false; }
            Keys mods = Keys.None;
            foreach (string m in parts.Take(parts.Count - 1))
            {
                switch (m.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= Keys.Control; break;
                    case "shift": mods |= Keys.Shift; break;
                    case "alt": mods |= Keys.Alt; break;
                    default: error = $"'{m}' is not Ctrl, Shift or Alt"; return false;
                }
            }
            var list = new List<Keys>();
            if (!TryParseKey(parts[parts.Count - 1], list) || list.Count != 1 || IsModifier(list[0]))
            {
                error = $"'{parts[parts.Count - 1]}' is not a single key";
                return false;
            }
            hotkey = list[0] | mods;
            return true;
        }

        /// <summary>"Ctrl+F12" - readable, and <see cref="TryParseHotkey"/> reads it back.</summary>
        public static string HotkeyText(Keys hotkey)
        {
            var sb = new StringBuilder();
            if ((hotkey & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((hotkey & Keys.Shift) != 0) sb.Append("Shift+");
            if ((hotkey & Keys.Alt) != 0) sb.Append("Alt+");
            Keys code = hotkey & Keys.KeyCode;
            sb.Append(code == Keys.Oemcomma ? "Oemcomma" : KeyName(code)); // "," would not read back
            return sb.ToString();
        }

        string Names(PadAction a)
        {
            var keys = Get(a);
            return keys.Length == 0 ? "-" : string.Join("/", keys.Select(KeyName));
        }

        /// <summary>Human-readable cheat sheet for the Join screen.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Move (left stick):   {Names(PadAction.LeftUp)} {Names(PadAction.LeftLeft)} {Names(PadAction.LeftDown)} {Names(PadAction.LeftRight)}");
            sb.AppendLine($"Right stick:         {Names(PadAction.RightUp)} {Names(PadAction.RightLeft)} {Names(PadAction.RightDown)} {Names(PadAction.RightRight)}");
            sb.AppendLine($"A = {Names(PadAction.A)}   B = {Names(PadAction.B)}   X = {Names(PadAction.X)}   Y = {Names(PadAction.Y)}");
            sb.AppendLine($"LB = {Names(PadAction.LB)}   RB = {Names(PadAction.RB)}   LT = {Names(PadAction.LT)}   RT = {Names(PadAction.RT)}");
            sb.AppendLine($"Start = {Names(PadAction.Start)}   Back = {Names(PadAction.Back)}   L3 = {Names(PadAction.L3)}   R3 = {Names(PadAction.R3)}");
            sb.Append($"D-pad: up {Names(PadAction.DUp)}  down {Names(PadAction.DDown)}  left {Names(PadAction.DLeft)}  right {Names(PadAction.DRight)}");
            if (Get(PadAction.Walk).Length > 0) sb.Append($"\nWalk (half speed): {Names(PadAction.Walk)}");
            return sb.ToString();
        }
    }

    /// <summary>Turns the set of pressed keys into an Xbox 360 controller state.</summary>
    public static class KeyMapper
    {
        const double Diagonal = 0.7071067811865476;

        public static PadState Map(Func<Keys, bool> isDown, KeyMap map)
        {
            bool Any(PadAction a)
            {
                foreach (Keys k in map.Get(a))
                    if (isDown(k)) return true;
                return false;
            }

            var s = new PadState();
            if (Any(PadAction.A)) s.Buttons |= PadButtons.A;
            if (Any(PadAction.B)) s.Buttons |= PadButtons.B;
            if (Any(PadAction.X)) s.Buttons |= PadButtons.X;
            if (Any(PadAction.Y)) s.Buttons |= PadButtons.Y;
            if (Any(PadAction.LB)) s.Buttons |= PadButtons.LeftShoulder;
            if (Any(PadAction.RB)) s.Buttons |= PadButtons.RightShoulder;
            if (Any(PadAction.Start)) s.Buttons |= PadButtons.Start;
            if (Any(PadAction.Back)) s.Buttons |= PadButtons.Back;
            if (Any(PadAction.L3)) s.Buttons |= PadButtons.LeftThumb;
            if (Any(PadAction.R3)) s.Buttons |= PadButtons.RightThumb;
            if (Any(PadAction.DUp)) s.Buttons |= PadButtons.DPadUp;
            if (Any(PadAction.DDown)) s.Buttons |= PadButtons.DPadDown;
            if (Any(PadAction.DLeft)) s.Buttons |= PadButtons.DPadLeft;
            if (Any(PadAction.DRight)) s.Buttons |= PadButtons.DPadRight;
            s.LeftTrigger = Any(PadAction.LT) ? (byte)255 : (byte)0;
            s.RightTrigger = Any(PadAction.RT) ? (byte)255 : (byte)0;

            double walk = Any(PadAction.Walk) ? 0.5 : 1.0;
            Stick(Any(PadAction.LeftLeft), Any(PadAction.LeftRight), Any(PadAction.LeftUp), Any(PadAction.LeftDown), walk,
                out s.LeftX, out s.LeftY);
            Stick(Any(PadAction.RightLeft), Any(PadAction.RightRight), Any(PadAction.RightUp), Any(PadAction.RightDown), 1.0,
                out s.RightX, out s.RightY);
            return s;
        }

        /// <summary>Digital directions to stick axes. Y is positive up (XInput convention).</summary>
        static void Stick(bool left, bool right, bool up, bool down, double scale, out short x, out short y)
        {
            int dx = (right ? 1 : 0) - (left ? 1 : 0);
            int dy = (up ? 1 : 0) - (down ? 1 : 0);
            double m = (dx != 0 && dy != 0) ? Diagonal : 1.0;
            x = (short)Math.Round(dx * 32767 * m * scale);
            y = (short)Math.Round(dy * 32767 * m * scale);
        }
    }
}
