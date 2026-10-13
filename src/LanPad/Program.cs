using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using LanPad.Config;
using LanPad.UI;

namespace LanPad
{
    static class Program
    {
        /// <summary>
        /// LanPad.exe            -> menu (Host / Join)
        /// LanPad.exe --host     -> start hosting right away
        /// LanPad.exe --join [ip]-> open the join screen (and join that IP)
        /// LanPad.exe --selftest -> run built-in tests, write selftest.log, exit code 0 = pass
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
                return SelfTest.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.Error("UI error", e.Exception);
                MessageBox.Show(e.Exception.Message, "LanPad error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);

            var warnings = new List<string>();
            var cfg = LanPadConfig.Load(AppPaths.ConfigFile, warnings);
            Log.Info($"LanPad {Application.ProductVersion} started on {Environment.MachineName}");
            foreach (string w in warnings) Log.Warn(w);
            Tweaks.Configure(cfg);

            if (args.Any(a => a.Equals("--streamtest", StringComparison.OrdinalIgnoreCase)))
                return StreamTest.Run(cfg, args);

            var mode = MainForm.StartMode.Menu;
            string joinAddress = null;
            string pin = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--host") mode = MainForm.StartMode.Host;
                else if (a == "--join")
                {
                    mode = MainForm.StartMode.Join;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) joinAddress = args[++i];
                }
                else if (a == "--pin" && i + 1 < args.Length) pin = args[++i];
            }

            Application.Run(new MainForm(cfg, warnings, mode, joinAddress, pin));
            return 0;
        }
    }
}
