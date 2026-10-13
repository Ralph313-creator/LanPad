using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using LanPad.Join;

namespace LanPad.Config
{
    /// <summary>All settings from lanpad.ini, with safe defaults when a value is missing or invalid.</summary>
    public sealed class LanPadConfig
    {
        // [General]
        public int Port = 47800;
        public int StreamPort = 5004;

        // [Host]
        public string HostName = "";
        /// <summary>Selected game (a section name in games.ini / games.user.ini).</summary>
        public string Game = "NBA 2K14";
        /// <summary>Folders searched for installed games, separated by ;</summary>
        public string GameFolders = @"G:\Offline Games;D:\Games;E:\Games;C:\Games;C:\Program Files (x86)\Steam\steamapps\common;C:\Program Files\Epic Games";
        /// <summary>The host PC's own keyboard is a player (so one controller fewer is offered to joined PCs).</summary>
        public bool HostPlays = true;
        public string RoomPin = "random";
        public int MaxPlayers = 4;
        public bool PrePlugPads = true;
        public int Fps = 60;
        public int BitrateKbps = 15000;
        public string Encoder = "auto";
        public int Display = 0;
        public bool AlwaysStream = false;

        // [Join]
        public string PlayerName = "";
        // LanPad hotkeys: key code + Ctrl / Shift / Alt flags (Settings > Hotkeys)
        /// <summary>Pad mode on / off ("ToggleKey" in lanpad.ini).</summary>
        public Keys PadKey = Keys.F12;
        public Keys LeaveKey = Keys.Control | Keys.F12;
        /// <summary>Show / hide the key cheat sheet.</summary>
        public Keys OverlayKey = Keys.Shift | Keys.F12;
        public bool SwallowMouse = true;
        public string Viewer = "mpv";
        public bool AutoPadMode = true;
        public bool Fullscreen = true;

        // [Performance] (see Tweaks)
        public bool HighPriority = true;
        public bool NoThrottling = true;
        public bool KeepAwake = true;
        /// <summary>Extra copies of each controller change, 10 ms apart (covers a lost packet). 0-4.</summary>
        public int InputResend = 2;

        /// <summary>Key cheat sheet on top of the game while joined (the overlay hotkey hides / shows it).</summary>
        public bool Overlay = true;
        /// <summary>TopLeft, TopRight, BottomLeft or BottomRight.</summary>
        public string OverlayCorner = "TopRight";
        /// <summary>30-100 %.</summary>
        public int OverlayOpacity = 85;

        public static readonly string[] OverlayCorners = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

        // [Keys]
        public KeyMap KeyMap = KeyMap.Default();

        public string EffectiveHostName => string.IsNullOrWhiteSpace(HostName) ? Environment.MachineName : HostName.Trim();
        public string EffectivePlayerName => string.IsNullOrWhiteSpace(PlayerName) ? Environment.MachineName : PlayerName.Trim();

        public static LanPadConfig Load(string path, List<string> warnings)
        {
            var ini = IniFile.Load(path);
            var c = new LanPadConfig();

            c.Port = Clamp(ini.GetInt("General", "Port", c.Port), 1024, 65535);
            c.StreamPort = Clamp(ini.GetInt("General", "StreamPort", c.StreamPort), 1024, 65535);

            c.HostName = ini.Get("Host", "HostName", c.HostName);
            c.Game = ini.Get("Host", "Game", c.Game);
            c.GameFolders = ini.Get("Host", "GameFolders", c.GameFolders);
            c.HostPlays = ini.GetBool("Host", "HostPlays", c.HostPlays);
            c.RoomPin = ini.Get("Host", "RoomPin", c.RoomPin);
            c.MaxPlayers = Clamp(ini.GetInt("Host", "MaxPlayers", c.MaxPlayers), 1, 4);
            c.PrePlugPads = ini.GetBool("Host", "PrePlugPads", c.PrePlugPads);
            c.Fps = Clamp(ini.GetInt("Host", "Fps", c.Fps), 15, 120);
            c.BitrateKbps = Clamp(ini.GetInt("Host", "BitrateKbps", c.BitrateKbps), 1000, 100000);
            c.Encoder = ini.Get("Host", "Encoder", c.Encoder).ToLowerInvariant();
            c.Display = Clamp(ini.GetInt("Host", "Display", c.Display), 0, 8);
            c.AlwaysStream = ini.GetBool("Host", "AlwaysStream", c.AlwaysStream);

            c.PlayerName = ini.Get("Join", "PlayerName", c.PlayerName);
            c.SwallowMouse = ini.GetBool("Join", "SwallowMouse", c.SwallowMouse);
            c.Viewer = ini.Get("Join", "Viewer", c.Viewer).ToLowerInvariant();
            c.AutoPadMode = ini.GetBool("Join", "AutoPadMode", c.AutoPadMode);
            c.Fullscreen = ini.GetBool("Join", "Fullscreen", c.Fullscreen);
            c.HighPriority = ini.GetBool("Performance", "HighPriority", c.HighPriority);
            c.NoThrottling = ini.GetBool("Performance", "NoThrottling", c.NoThrottling);
            c.KeepAwake = ini.GetBool("Performance", "KeepAwake", c.KeepAwake);
            c.InputResend = Clamp(ini.GetInt("Performance", "InputResend", c.InputResend), 0, 4);

            Keys Hotkey(string key, Keys def)
            {
                string text = ini.Get("Join", key, "");
                if (text.Length == 0) return def;
                if (KeyMap.TryParseHotkey(text, out var hk, out var err)) return hk;
                warnings?.Add($"[Join] {key}: {err} (using {KeyMap.HotkeyText(def)})");
                return def;
            }
            c.PadKey = Hotkey("ToggleKey", c.PadKey);
            c.LeaveKey = Hotkey("LeaveKey", c.LeaveKey);
            c.OverlayKey = Hotkey("OverlayKey", c.OverlayKey);
            if (c.PadKey == c.LeaveKey || c.PadKey == c.OverlayKey || c.LeaveKey == c.OverlayKey)
            {
                warnings?.Add("[Join] ToggleKey, LeaveKey and OverlayKey must be different (using F12, Ctrl+F12, Shift+F12)");
                var d = new LanPadConfig();
                c.PadKey = d.PadKey;
                c.LeaveKey = d.LeaveKey;
                c.OverlayKey = d.OverlayKey;
            }

            c.Overlay = ini.GetBool("Join", "Overlay", c.Overlay);
            c.OverlayOpacity = Clamp(ini.GetInt("Join", "OverlayOpacity", c.OverlayOpacity), 30, 100);
            string corner = ini.Get("Join", "OverlayCorner", c.OverlayCorner);
            c.OverlayCorner = Array.Find(OverlayCorners, x => string.Equals(x, corner, StringComparison.OrdinalIgnoreCase));
            if (c.OverlayCorner == null)
            {
                warnings?.Add($"[Join] OverlayCorner: use {string.Join(", ", OverlayCorners)} (using TopRight)");
                c.OverlayCorner = "TopRight";
            }

            foreach (PadAction action in Enum.GetValues(typeof(PadAction)))
            {
                string name = action.ToString();
                if (!ini.Has("Keys", name)) continue;
                if (KeyMap.TryParseKeyList(ini.Get("Keys", name), out var keys, out var error))
                    c.KeyMap.Set(action, keys);
                else
                    warnings?.Add($"[Keys] {name}: {error} (keeping default)");
            }

            // a hotkey without Ctrl / Shift / Alt takes that key away from the controller
            foreach (Keys hotkey in new[] { c.PadKey, c.LeaveKey, c.OverlayKey }.Where(h => (h & Keys.Modifiers) == 0))
                foreach (PadAction action in Enum.GetValues(typeof(PadAction)))
                    if (c.KeyMap.Get(action).Contains(hotkey))
                        warnings?.Add($"[Keys] {action} uses the hotkey {KeyMap.HotkeyText(hotkey)}; it will not work as a button");
            return c;
        }

        /// <summary>Writes the settings the Settings page can change into lanpad.ini (comments and other keys stay).</summary>
        public void SaveSettings(string path)
        {
            string B(bool b) => b ? "true" : "false";
            IniFile.SetValue(path, "Host", "Fps", Fps.ToString());
            IniFile.SetValue(path, "Host", "BitrateKbps", BitrateKbps.ToString());
            IniFile.SetValue(path, "Host", "Encoder", Encoder);
            IniFile.SetValue(path, "Join", "Viewer", Viewer);
            IniFile.SetValue(path, "Join", "AutoPadMode", B(AutoPadMode));
            IniFile.SetValue(path, "Join", "Fullscreen", B(Fullscreen));
            IniFile.SetValue(path, "Join", "SwallowMouse", B(SwallowMouse));
            IniFile.SetValue(path, "Join", "ToggleKey", KeyMap.HotkeyText(PadKey));
            IniFile.SetValue(path, "Join", "LeaveKey", KeyMap.HotkeyText(LeaveKey));
            IniFile.SetValue(path, "Join", "OverlayKey", KeyMap.HotkeyText(OverlayKey));
            IniFile.SetValue(path, "Join", "Overlay", B(Overlay));
            IniFile.SetValue(path, "Join", "OverlayCorner", OverlayCorner);
            IniFile.SetValue(path, "Join", "OverlayOpacity", OverlayOpacity.ToString());
            IniFile.SetValue(path, "Performance", "HighPriority", B(HighPriority));
            IniFile.SetValue(path, "Performance", "NoThrottling", B(NoThrottling));
            IniFile.SetValue(path, "Performance", "KeepAwake", B(KeepAwake));
            IniFile.SetValue(path, "Performance", "InputResend", InputResend.ToString());
        }

        static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
