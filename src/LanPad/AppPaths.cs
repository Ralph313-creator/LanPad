using System;
using System.IO;

namespace LanPad
{
    public static class AppPaths
    {
        public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string ToolsDir => Path.Combine(AppDir, "tools");
        public static string ConfigFile => Path.Combine(AppDir, "lanpad.ini");
        public static string TempDir => Path.GetTempPath();

        /// <summary>First existing path among the candidates (relative to the tools folder), or null.</summary>
        public static string FindTool(params string[] relativeCandidates)
        {
            foreach (string rel in relativeCandidates)
            {
                string p = Path.Combine(ToolsDir, rel);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
