using System;
using System.Collections.Generic;
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
        public Keys ToggleKey = Keys.F12;
        public bool SwallowMouse = true;
        public string Viewer = "mpv";
        public bool AutoPadMode = true;
        public bool Fullscreen = true;

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
            string toggle = ini.Get("Join", "ToggleKey", "");
            if (toggle.Length > 0)
            {
                if (KeyMap.TryParseKeyList(toggle, out var tk, out var err) && tk.Length == 1) c.ToggleKey = tk[0];
                else warnings?.Add($"[Join] ToggleKey: {err ?? "use exactly one key"} (using F12)");
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

            foreach (PadAction action in Enum.GetValues(typeof(PadAction)))
            {
                foreach (Keys k in c.KeyMap.Get(action))
                    if (k == c.ToggleKey)
                        warnings?.Add($"[Keys] {action} uses the toggle key {c.ToggleKey}; it will not work as a button");
            }
            return c;
        }

        static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
