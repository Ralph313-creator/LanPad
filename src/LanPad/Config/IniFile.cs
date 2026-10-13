using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LanPad.Config
{
    /// <summary>Minimal INI reader: [Section] / Key=Value / ; or # comments. Case-insensitive, keeps section order.</summary>
    public sealed class IniFile
    {
        readonly Dictionary<string, Dictionary<string, string>> sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();

        public static IniFile Load(string path) =>
            File.Exists(path) ? Parse(File.ReadAllText(path)) : new IniFile();

        public static IniFile Parse(string text)
        {
            var ini = new IniFile();
            var current = ini.GetOrAdd("");
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    current = ini.GetOrAdd(line.Substring(1, line.Length - 2).Trim());
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return ini;
        }

        Dictionary<string, string> GetOrAdd(string name)
        {
            if (!sections.TryGetValue(name, out var s))
            {
                s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[name] = s;
                order.Add(name);
            }
            return s;
        }

        /// <summary>Section names in file order (without the unnamed top section).</summary>
        public IEnumerable<string> Sections => order.Where(s => s.Length > 0);

        /// <summary>All key/value pairs of a section (empty if the section does not exist).</summary>
        public IReadOnlyDictionary<string, string> Section(string section) =>
            sections.TryGetValue(section, out var s) ? s : new Dictionary<string, string>();

        public bool Has(string section, string key) =>
            sections.TryGetValue(section, out var s) && s.ContainsKey(key);

        public string Get(string section, string key, string def = "") =>
            sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : def;

        public int GetInt(string section, string key, int def) =>
            int.TryParse(Get(section, key, ""), out int v) ? v : def;

        public bool GetBool(string section, string key, bool def)
        {
            string v = Get(section, key, "").ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
            if (v == "false" || v == "0" || v == "no" || v == "off") return false;
            return def;
        }

        /// <summary>
        /// Sets one value in an INI file on disk, keeping comments and everything else as it is.
        /// Creates the file / section / key when missing.
        /// </summary>
        public static void SetValue(string path, string section, string key, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int sectionStart = -1, sectionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (!(t.StartsWith("[") && t.EndsWith("]"))) continue;
                if (sectionStart >= 0) { sectionEnd = i; break; }
                if (string.Equals(t.Substring(1, t.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase))
                    sectionStart = i;
            }

            string newLine = $"{key}={value}";
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add($"[{section}]");
                lines.Add(newLine);
            }
            else
            {
                int found = -1, lastValue = sectionStart;
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
                    lastValue = i;
                    int eq = t.IndexOf('=');
                    if (eq > 0 && string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        found = i;
                        break;
                    }
                }
                if (found >= 0) lines[found] = newLine;
                else lines.Insert(lastValue + 1, newLine);
            }
            File.WriteAllLines(path, lines);
        }
    }
}
