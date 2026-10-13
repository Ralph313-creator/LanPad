using System;
using System.Collections.Generic;
using System.Windows.Forms;
using LanPad.Games;
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

        // [Performance] (see Tweaks)
        public bool HighPriority = true;
        public bool NoThrottling = true;
        public bool KeepAwake = true;
        /// <summary>Extra copies of each controller change, 10 ms apart (covers a lost packet). 0-4.</summary>
        public int InputResend = 2;

        /// <summary>Joined PC uses this PC's keys (Controls page) instead of the controls the host sends.</summary>
        public bool OwnKeys = false;
        /// <summary>Key cheat sheet on top of the game while joined (Shift + toggle key hides / shows it).</summary>
        public bool Overlay = true;
        /// <summary>TopLeft, TopRight, BottomLeft or BottomRight.</summary>
        public string OverlayCorner = "TopRight";
        /// <summary>30-100 %.</summary>
        public int OverlayOpacity = 85;

        public static readonly string[] OverlayCorners = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

        // [Keys] = Sports layout
        public KeyMap KeyMap = KeyMap.Default();

        // [Keys.Fighting], [Keys.Action], [Keys.Racing]: built-in layouts with this PC's changes
        readonly Dictionary<ControlLayout, KeyMap> layoutKeys = new Dictionary<ControlLayout, KeyMap>
        {
            [ControlLayout.Fighting] = Layouts.Fighting(),
            [ControlLayout.Action] = Layouts.Action(),
            [ControlLayout.Racing] = Layouts.Racing(),
        };

        /// <summary>This PC's keys for a layout (Sports = [Keys]).</summary>
        public KeyMap KeysFor(ControlLayout layout) =>
            layout == ControlLayout.Sports || !layoutKeys.TryGetValue(layout, out var m) ? KeyMap : m;

        public void SetKeys(ControlLayout layout, KeyMap keys)
        {
            if (layout == ControlLayout.Sports) KeyMap = keys;
            else layoutKeys[layout] = keys;
        }

        /// <summary>The keys a layout has before any change: [Keys] defaults for Sports, built-in for the others.</summary>
        public static KeyMap DefaultKeys(ControlLayout layout) =>
            layout == ControlLayout.Sports ? KeyMap.Default() : Layouts.Build(layout, null);

        public static string KeySection(ControlLayout layout) => layout == ControlLayout.Sports ? "Keys" : "Keys." + layout;

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

            string toggle = ini.Get("Join", "ToggleKey", "");
            if (toggle.Length > 0)
            {
                if (KeyMap.TryParseKeyList(toggle, out var tk, out var err) && tk.Length == 1) c.ToggleKey = tk[0];
                else warnings?.Add($"[Join] ToggleKey: {err ?? "use exactly one key"} (using F12)");
            }

            c.OwnKeys = ini.GetBool("Join", "OwnKeys", c.OwnKeys);
            c.Overlay = ini.GetBool("Join", "Overlay", c.Overlay);
            c.OverlayOpacity = Clamp(ini.GetInt("Join", "OverlayOpacity", c.OverlayOpacity), 30, 100);
            string corner = ini.Get("Join", "OverlayCorner", c.OverlayCorner);
            c.OverlayCorner = Array.Find(OverlayCorners, x => string.Equals(x, corner, StringComparison.OrdinalIgnoreCase));
            if (c.OverlayCorner == null)
            {
                warnings?.Add($"[Join] OverlayCorner: use {string.Join(", ", OverlayCorners)} (using TopRight)");
                c.OverlayCorner = "TopRight";
            }

            foreach (ControlLayout layout in Enum.GetValues(typeof(ControlLayout)))
            {
                string section = KeySection(layout);
                var map = c.KeysFor(layout);
                foreach (PadAction action in Enum.GetValues(typeof(PadAction)))
                {
                    string name = action.ToString();
                    if (!ini.Has(section, name)) continue;
                    if (KeyMap.TryParseKeyList(ini.Get(section, name), out var keys, out var error))
                        map.Set(action, keys);
                    else
                        warnings?.Add($"[{section}] {name}: {error} (keeping default)");
                }
                foreach (PadAction action in Enum.GetValues(typeof(PadAction)))
                {
                    foreach (Keys k in map.Get(action))
                        if (k == c.ToggleKey)
                            warnings?.Add($"[{section}] {action} uses the toggle key {c.ToggleKey}; it will not work as a button");
                }
            }
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
            IniFile.SetValue(path, "Join", "Overlay", B(Overlay));
            IniFile.SetValue(path, "Join", "OverlayCorner", OverlayCorner);
            IniFile.SetValue(path, "Join", "OverlayOpacity", OverlayOpacity.ToString());
            IniFile.SetValue(path, "Performance", "HighPriority", B(HighPriority));
            IniFile.SetValue(path, "Performance", "NoThrottling", B(NoThrottling));
            IniFile.SetValue(path, "Performance", "KeepAwake", B(KeepAwake));
            IniFile.SetValue(path, "Performance", "InputResend", InputResend.ToString());
        }

        /// <summary>Writes one layout's keys ([Keys] or [Keys.&lt;layout&gt;]) plus the Pad mode key and OwnKeys.</summary>
        public void SaveKeys(string path, ControlLayout layout)
        {
            var map = KeysFor(layout);
            foreach (PadAction action in Enum.GetValues(typeof(PadAction)))
                IniFile.SetValue(path, KeySection(layout), action.ToString(), KeyMap.IniText(map.Get(action)));
            IniFile.SetValue(path, "Join", "ToggleKey", KeyMap.IniText(new[] { ToggleKey }));
            IniFile.SetValue(path, "Join", "OwnKeys", OwnKeys ? "true" : "false");
        }

        static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
