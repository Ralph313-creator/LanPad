using System;
using System.IO;

namespace LanPad
{
    /// <summary>Tiny thread-safe file logger (writes to %TEMP%\LanPad.log by default).</summary>
    public static class Log
    {
        static readonly object Gate = new object();

        public static string FilePath { get; set; } = Path.Combine(Path.GetTempPath(), "LanPad.log");

        /// <summary>Raised for every line (any thread). The UI can show the latest status.</summary>
        public static event Action<string> Line;

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : msg + ": " + ex.Message);

        static void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
            lock (Gate)
            {
                try
                {
                    if (!string.IsNullOrEmpty(FilePath)) File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Line?.Invoke(line);
        }
    }
}
