using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LanPad.Config;
using LanPad.Join;

namespace LanPad.Games
{
    /// <summary>Control style for a game. The host sends the matching key map to every joined PC.</summary>
    public enum ControlLayout { Sports, Fighting, Action, Racing }

    /// <summary>One supported game: name, players, controls, how to find its exe, and a tip for the host.</summary>
    public sealed class GameProfile
    {
        public string Name = "";
        public int Players = 2;
        public ControlLayout Layout = ControlLayout.Sports;
        public string[] Find = new string[0];
        public string Exe = "";
        public string Notes = "";
        public readonly Dictionary<PadAction, Keys[]> KeyChanges = new Dictionary<PadAction, Keys[]>();

        public bool Installed => !string.IsNullOrEmpty(Exe) && File.Exists(Exe);

        /// <summary>Key map for this game: its layout (Sports = the [Keys] map from lanpad.ini) plus per-game changes.</summary>
        public KeyMap BuildKeyMap(KeyMap sportsKeys) => WithChanges(Layouts.Build(Layout, sportsKeys));

        /// <summary>Key map for this game from this PC's keys for its layout (Controls page), plus per-game changes.</summary>
        public KeyMap BuildKeyMap(LanPadConfig cfg) => WithChanges(cfg.KeysFor(Layout).Clone());

        KeyMap WithChanges(KeyMap m)
        {
            foreach (var kv in KeyChanges) m.Set(kv.Key, kv.Value);
            return m;
        }

        public string ListText => $"{(Installed ? "✔ " : "    ")}{Name}   ({Players}P, {Layout})";

        public override string ToString() => ListText;
    }

    /// <summary>The four built-in control layouts. All keep WASD = move, arrows = right stick, Enter/Backspace = Start/Back.</summary>
    public static class Layouts
    {
        public static KeyMap Build(ControlLayout layout, KeyMap sportsKeys)
        {
            switch (layout)
            {
                case ControlLayout.Fighting: return Fighting();
                case ControlLayout.Action: return Action();
                case ControlLayout.Racing: return Racing();
                default: return (sportsKeys ?? KeyMap.Default()).Clone();
            }
        }

        static KeyMap Common()
        {
            var m = KeyMap.Default(); // WASD, arrows, Enter/Backspace, 1-4 D-pad, R/F stick clicks
            foreach (var a in new[] { PadAction.A, PadAction.B, PadAction.X, PadAction.Y, PadAction.LB, PadAction.RB, PadAction.LT, PadAction.RT })
                m.Set(a);
            return m;
        }

        /// <summary>Arcade layout: U I O P = X Y RB LB (punches), J K L ; = A B RT LT (kicks). Tekken: U I J K = 1 2 3 4.</summary>
        public static KeyMap Fighting()
        {
            var m = Common();
            m.Set(PadAction.X, Keys.U);
            m.Set(PadAction.Y, Keys.I);
            m.Set(PadAction.RB, Keys.O);
            m.Set(PadAction.LB, Keys.P);
            m.Set(PadAction.A, Keys.J);
            m.Set(PadAction.B, Keys.K);
            m.Set(PadAction.RT, Keys.L);
            m.Set(PadAction.LT, Keys.OemSemicolon);
            return m;
        }

        /// <summary>Platformer / co-op / party: Space = A (jump/confirm), J = X (attack/use), K = B, I = Y.</summary>
        public static KeyMap Action()
        {
            var m = Common();
            m.Set(PadAction.A, Keys.Space);
            m.Set(PadAction.X, Keys.J);
            m.Set(PadAction.B, Keys.K);
            m.Set(PadAction.Y, Keys.I);
            m.Set(PadAction.LB, Keys.Q);
            m.Set(PadAction.RB, Keys.E);
            m.Set(PadAction.LT, Keys.U);
            m.Set(PadAction.RT, Keys.O);
            m.Set(PadAction.L3, Keys.LShiftKey);
            m.Set(PadAction.R3, Keys.R);
            return m;
        }

        /// <summary>Racing: W = gas (RT) and stick up, S = brake (LT) and stick down, A/D steer, Space = A.</summary>
        public static KeyMap Racing()
        {
            var m = Common();
            m.Set(PadAction.RT, Keys.W);
            m.Set(PadAction.LT, Keys.S);
            m.Set(PadAction.A, Keys.Space);
            m.Set(PadAction.X, Keys.J);
            m.Set(PadAction.B, Keys.K);
            m.Set(PadAction.Y, Keys.I);
            m.Set(PadAction.LB, Keys.Q);
            m.Set(PadAction.RB, Keys.E);
            return m;
        }
    }

    /// <summary>
    /// Loads the game list: games.ini (shipped list, replaced on update) and then games.user.ini
    /// (this cafe's exe paths and own games, never replaced). Finds installed games on disk.
    /// </summary>
    public static class GameCatalog
    {
        public const string GenericName = "Any other game";

        public static string CatalogFile => Path.Combine(AppPaths.AppDir, "games.ini");
        public static string UserFile => Path.Combine(AppPaths.AppDir, "games.user.ini");

        public static List<GameProfile> Load(List<string> warnings) => Load(CatalogFile, UserFile, warnings);

        public static List<GameProfile> Load(string catalogFile, string userFile, List<string> warnings)
        {
            var list = new List<GameProfile>();
            foreach (string file in new[] { catalogFile, userFile })
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                var ini = IniFile.Load(file);
                foreach (string section in ini.Sections)
                {
                    var p = list.FirstOrDefault(g => string.Equals(g.Name, section, StringComparison.OrdinalIgnoreCase));
                    if (p == null)
                    {
                        p = new GameProfile { Name = section };
                        list.Add(p);
                    }
                    Apply(p, ini.Section(section), Path.GetFileName(file), warnings);
                }
            }
            if (!list.Any(g => g.Name == GenericName))
            {
                list.Add(new GameProfile
                {
                    Name = GenericName,
                    Players = 5,
                    Layout = ControlLayout.Sports,
                    Notes = "Any game with local multiplayer using Xbox controllers. Browse to its .exe to launch it from here.",
                });
            }
            return list;
        }

        static void Apply(GameProfile p, IReadOnlyDictionary<string, string> values, string file, List<string> warnings)
        {
            foreach (var kv in values)
            {
                string key = kv.Key, value = kv.Value;
                switch (key.ToLowerInvariant())
                {
                    case "players":
                        if (int.TryParse(value, out int n) && n >= 1) p.Players = Math.Min(n, 5);
                        else warnings?.Add($"{file} [{p.Name}] Players: '{value}' is not a number");
                        break;
                    case "layout":
                        if (Enum.TryParse(value, true, out ControlLayout layout)) p.Layout = layout;
                        else warnings?.Add($"{file} [{p.Name}] Layout: use Sports, Fighting, Action or Racing");
                        break;
                    case "find":
                        p.Find = value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                        break;
                    case "exe":
                        p.Exe = value;
                        break;
                    case "notes":
                        p.Notes = value;
                        break;
                    default:
                        if (Enum.TryParse(key, true, out PadAction action) && Enum.IsDefined(typeof(PadAction), action))
                        {
                            if (KeyMap.TryParseKeyList(value, out var keys, out string err)) p.KeyChanges[action] = keys;
                            else warnings?.Add($"{file} [{p.Name}] {key}: {err}");
                        }
                        else warnings?.Add($"{file} [{p.Name}] unknown setting '{key}'");
                        break;
                }
            }
        }

        /// <summary>
        /// Looks for each game's exe (Find=) under the given folders, a few levels deep.
        /// Games that already have a working Exe= keep it. Returns how many games were found.
        /// </summary>
        public static int Detect(IEnumerable<GameProfile> games, IEnumerable<string> roots, int maxDepth = 4, int maxFolders = 40000)
        {
            var wanted = new Dictionary<string, List<GameProfile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games.Where(g => !g.Installed))
                foreach (string exe in g.Find)
                {
                    if (!wanted.TryGetValue(exe, out var l)) wanted[exe] = l = new List<GameProfile>();
                    l.Add(g);
                }
            if (wanted.Count == 0) return 0;

            int found = 0, folders = 0;
            var queue = new Queue<(string Dir, int Depth)>();
            foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
                queue.Enqueue((root, 0));

            while (queue.Count > 0 && wanted.Count > 0 && folders < maxFolders)
            {
                var (dir, depth) = queue.Dequeue();
                folders++;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*.exe"))
                    {
                        string name = Path.GetFileName(file);
                        if (!wanted.TryGetValue(name, out var matches)) continue;
                        foreach (var g in matches.Where(m => !m.Installed))
                        {
                            g.Exe = file;
                            found++;
                        }
                        wanted.Remove(name);
                    }
                    if (depth < maxDepth)
                        foreach (string sub in Directory.EnumerateDirectories(dir))
                            queue.Enqueue((sub, depth + 1));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }
            }
            return found;
        }

        /// <summary>Remembers a game's exe in games.user.ini (kept across LanPad updates).</summary>
        public static void SaveExe(string userFile, string gameName, string exePath) =>
            IniFile.SetValue(userFile, gameName, "Exe", exePath);

        public static IEnumerable<string> SplitFolders(string folders) =>
            (folders ?? "").Split(';').Select(f => Environment.ExpandEnvironmentVariables(f.Trim())).Where(f => f.Length > 0);
    }
}
