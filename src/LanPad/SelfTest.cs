using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LanPad.Config;
using LanPad.Games;
using LanPad.Host;
using LanPad.Join;
using LanPad.Net;
using LanPad.Protocol;

namespace LanPad
{
    /// <summary>Built-in tests: LanPad.exe --selftest (no driver, no network card needed; uses loopback).</summary>
    static class SelfTest
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        static readonly StringBuilder Output = new StringBuilder();
        static int failures;
        static int passes;

        public static int Run()
        {
            AttachConsole(-1);
            Log.FilePath = Path.Combine(Path.GetTempPath(), "LanPad-selftest-trace.log");
            var sw = Stopwatch.StartNew();
            try
            {
                PacketTests();
                KeyMapTests();
                ConfigTests();
                GameTests();
                NetworkTests();
                GameSwitchTests();
                InputResendTests();
                DriverTests();
            }
            catch (Exception ex)
            {
                Fail("test run crashed: " + ex);
            }
            Output.AppendLine(failures == 0
                ? $"ALL {passes} CHECKS PASSED ({sw.ElapsedMilliseconds} ms)"
                : $"{failures} CHECK(S) FAILED, {passes} passed");

            string text = Output.ToString();
            string file = Path.Combine(AppPaths.AppDir, "selftest.log");
            try { File.WriteAllText(file, text); }
            catch (Exception) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "LanPad-selftest.log"), text); }
            try { Console.Write(text); } catch (IOException) { }
            return failures == 0 ? 0 : 1;
        }

        static void Check(bool ok, string name)
        {
            if (ok) { passes++; Output.AppendLine("PASS  " + name); }
            else Fail(name);
        }

        static void Fail(string name)
        {
            failures++;
            Output.AppendLine("FAIL  " + name);
        }

        static bool WaitFor(Func<bool> condition, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        static Packet RoundTrip(Packet p)
        {
            byte[] b = p.ToBytes();
            return Packet.TryParse(b, b.Length, out var q) ? q : null;
        }

        // ------------------------------------------------------------------ protocol

        static void PacketTests()
        {
            var offer = RoundTrip(new Packet(PacketType.Offer)
            {
                HostName = "PC-07", GameName = "NBA 2K14", SlotsFree = 3, SlotsTotal = 4, PinRequired = true,
                StreamGroup = "239.255.77.7", StreamPort = 5004,
            });
            Check(offer != null && offer.Type == PacketType.Offer && offer.HostName == "PC-07" && offer.GameName == "NBA 2K14" &&
                  offer.SlotsFree == 3 && offer.SlotsTotal == 4 && offer.PinRequired && offer.StreamGroup == "239.255.77.7" &&
                  offer.StreamPort == 5004, "packet: Offer round trip");

            var join = RoundTrip(new Packet(PacketType.Join, 77) { PlayerName = "Juan", Pin = "1234" });
            Check(join != null && join.ClientId == 77 && join.PlayerName == "Juan" && join.Pin == "1234", "packet: Join round trip");

            byte[] map = KeyMap.Default().ToBytes();
            var welcome = RoundTrip(new Packet(PacketType.Welcome, 5)
            {
                Slot = 2, StreamGroup = "239.255.77.9", StreamPort = 6000, GameName = "Tekken 7", KeyMap = map,
            });
            Check(welcome != null && welcome.Slot == 2 && welcome.StreamGroup == "239.255.77.9" && welcome.StreamPort == 6000 &&
                  welcome.GameName == "Tekken 7" && welcome.KeyMap.SequenceEqual(map),
                "packet: Welcome round trip (with game and controls)");
            var alive = RoundTrip(new Packet(PacketType.Alive, 5) { Slot = 1 });
            Check(alive != null && alive.KeyMap.Length == 0 && alive.GameName == "", "packet: Alive without controls");

            var pad = new PadState
            {
                Buttons = PadButtons.A | PadButtons.Start | PadButtons.DPadLeft, LeftTrigger = 255, RightTrigger = 7,
                LeftX = -32767, LeftY = 32767, RightX = 123, RightY = -456,
            };
            var state = RoundTrip(new Packet(PacketType.State, 9) { Seq = 4000000000, Pad = pad });
            Check(state != null && state.Seq == 4000000000 && state.Pad == pad, "packet: State round trip");

            foreach (var t in new[] { PacketType.Discover, PacketType.Full, PacketType.BadPin, PacketType.Leave, PacketType.Kick, PacketType.NotJoined })
            {
                var q = RoundTrip(new Packet(t, 1234));
                Check(q != null && q.Type == t && q.ClientId == 1234, $"packet: {t} round trip");
            }

            byte[] bad = new Packet(PacketType.Discover).ToBytes();
            bad[0] ^= 0xFF;
            Check(!Packet.TryParse(bad, bad.Length, out _), "packet: wrong magic rejected");
            byte[] cut = new Packet(PacketType.State, 1) { Pad = pad }.ToBytes();
            Check(!Packet.TryParse(cut, cut.Length - 3, out _), "packet: truncated packet rejected");
            Check(!Packet.TryParse(new byte[3], 3, out _), "packet: tiny packet rejected");
            var longName = RoundTrip(new Packet(PacketType.Join, 1) { PlayerName = new string('x', 300) });
            Check(longName != null && longName.PlayerName.Length == Packet.MaxStringBytes, "packet: long name is cut, not broken");
        }

        // ------------------------------------------------------------------ key mapping

        static PadState MapKeys(KeyMap map, params Keys[] pressed) => KeyMapper.Map(k => pressed.Contains(k), map);

        static void KeyMapTests()
        {
            var m = KeyMap.Default();
            Check(MapKeys(m).IsNeutral, "keys: nothing pressed = neutral");
            var w = MapKeys(m, Keys.W);
            Check(w.LeftY == 32767 && w.LeftX == 0, "keys: W = left stick up");
            var s = MapKeys(m, Keys.S);
            Check(s.LeftY == -32767, "keys: S = left stick down");
            var wd = MapKeys(m, Keys.W, Keys.D);
            Check(wd.LeftX == 23170 && wd.LeftY == 23170, "keys: W+D = diagonal (not faster than straight)");
            Check(MapKeys(m, Keys.A, Keys.D).LeftX == 0, "keys: A+D cancel out");
            Check(MapKeys(m, Keys.Up).RightY == 32767 && MapKeys(m, Keys.Left).RightX == -32767, "keys: arrows = right stick");
            Check(MapKeys(m, Keys.K).Buttons == PadButtons.A && MapKeys(m, Keys.L).Buttons == PadButtons.B &&
                  MapKeys(m, Keys.J).Buttons == PadButtons.X && MapKeys(m, Keys.I).Buttons == PadButtons.Y, "keys: K L J I = A B X Y");
            Check(MapKeys(m, Keys.LShiftKey).RightTrigger == 255 && MapKeys(m, Keys.Space).LeftTrigger == 255, "keys: Shift = RT, Space = LT");
            Check(MapKeys(m, Keys.Q).Buttons == PadButtons.LeftShoulder && MapKeys(m, Keys.E).Buttons == PadButtons.RightShoulder, "keys: Q E = LB RB");
            Check(MapKeys(m, Keys.Return).Buttons == PadButtons.Start && MapKeys(m, Keys.Back).Buttons == PadButtons.Back, "keys: Enter/Backspace = Start/Back");
            Check(MapKeys(m, Keys.D1).Buttons == PadButtons.DPadUp && MapKeys(m, Keys.D4).Buttons == PadButtons.DPadRight, "keys: 1-4 = D-pad");
            var combo = MapKeys(m, Keys.K, Keys.J, Keys.LShiftKey, Keys.W);
            Check(combo.Buttons == (PadButtons.A | PadButtons.X) && combo.RightTrigger == 255 && combo.LeftY == 32767, "keys: several keys at once");

            m.Set(PadAction.Walk, Keys.LControlKey);
            Check(MapKeys(m, Keys.W, Keys.LControlKey).LeftY == 16384, "keys: Walk modifier = half stick");

            Check(KeyMap.TryParseKeyList("Shift", out var k1, out _) && k1.Length == 2, "keys: 'Shift' = both shift keys");
            Check(KeyMap.TryParseKeyList("K, NumPad2", out var k2, out _) && k2.SequenceEqual(new[] { Keys.K, Keys.NumPad2 }), "keys: list parsing");
            Check(KeyMap.TryParseKeyList("1", out var k3, out _) && k3[0] == Keys.D1, "keys: digit names");
            Check(KeyMap.TryParseKeyList("Enter", out var k4, out _) && k4[0] == Keys.Return, "keys: Enter alias");
            Check(!KeyMap.TryParseKeyList("Banana", out _, out _), "keys: unknown key rejected");
            Check(KeyMap.TryParseKeyList("", out var k5, out _) && k5.Length == 0, "keys: empty = no key");
        }

        // ------------------------------------------------------------------ config

        static void ConfigTests()
        {
            var ini = IniFile.Parse("; comment\n[Host]\nGameName = Test Game \nMaxPlayers=9\n[Keys]\nA=Z\n# other comment\n");
            Check(ini.Get("Host", "GameName") == "Test Game", "ini: value trimmed");
            Check(ini.Get("host", "gamename") == "Test Game", "ini: case-insensitive");
            Check(ini.Get("Nope", "X", "def") == "def", "ini: default for missing value");

            string tmp = Path.Combine(Path.GetTempPath(), "lanpad-selftest.ini");
            File.WriteAllText(tmp, "[Host]\nMaxPlayers=9\nRoomPin=off\n[Join]\nToggleKey=F11\n[Keys]\nA=Z\nB=Banana\n");
            var warnings = new List<string>();
            var cfg = LanPadConfig.Load(tmp, warnings);
            Check(cfg.MaxPlayers == 4, "config: MaxPlayers clamped to 4");
            Check(cfg.KeyMap.Get(PadAction.A).SequenceEqual(new[] { Keys.Z }), "config: custom key loaded");
            Check(cfg.KeyMap.Get(PadAction.B).SequenceEqual(new[] { Keys.L }), "config: bad key keeps default");
            Check(cfg.PadKey == Keys.F11, "config: toggle key loaded");
            Check(warnings.Count == 1 && warnings[0].Contains("Banana"), "config: warning for bad key");
            File.Delete(tmp);

            File.WriteAllText(tmp, "; keep me\n[Performance]\nHighPriority=false\nInputResend=9\n");
            var perf = LanPadConfig.Load(tmp, null);
            Check(!perf.HighPriority && perf.NoThrottling && perf.KeepAwake && perf.InputResend == 4,
                "config: [Performance] tweaks loaded (InputResend clamped to 4)");
            perf.Fps = 30;
            perf.KeepAwake = false;
            perf.Viewer = "ffplay";
            perf.SaveSettings(tmp);
            var saved = LanPadConfig.Load(tmp, null);
            Check(saved.Fps == 30 && !saved.KeepAwake && !saved.HighPriority && saved.InputResend == 4 && saved.Viewer == "ffplay",
                "config: Settings page values are saved and read back");
            Check(File.ReadAllText(tmp).Contains("; keep me"), "config: saving settings keeps comments");
            File.Delete(tmp);

            // hotkeys (Settings > Hotkeys)
            var hotkeys = new[] { Keys.F12, Keys.Control | Keys.F12, Keys.Shift | Keys.F12, Keys.Alt | Keys.P,
                                  Keys.Control | Keys.Shift | Keys.Oemcomma, Keys.Pause, Keys.Control | Keys.D9, Keys.Escape | Keys.Shift };
            Check(hotkeys.All(h => KeyMap.TryParseHotkey(KeyMap.HotkeyText(h), out var back, out _) && back == h),
                "hotkeys: every hotkey is saved as text that reads back the same (e.g. Ctrl+F12, Alt+P)");
            Check(!KeyMap.TryParseHotkey("Ctrl+Shift", out _, out _) && !KeyMap.TryParseHotkey("Win+F1", out _, out _) &&
                  !KeyMap.TryParseHotkey("", out _, out _), "hotkeys: Ctrl+Shift alone, unknown modifiers and empty are refused");

            var d0 = new LanPadConfig();
            KeyboardHook.Hotkey M(Keys key, Keys mods) =>
                KeyboardHook.Match((int)key, mods, d0.PadKey, d0.LeaveKey, d0.SwitchKey);
            Check(M(Keys.F12, Keys.None) == KeyboardHook.Hotkey.PadMode && M(Keys.F12, Keys.Control) == KeyboardHook.Hotkey.Leave &&
                  M(Keys.F12, Keys.Shift) == KeyboardHook.Hotkey.None && M(Keys.F12, Keys.Alt) == KeyboardHook.Hotkey.Switch &&
                  M(Keys.F12, Keys.Control | Keys.Alt) == KeyboardHook.Hotkey.None && M(Keys.K, Keys.None) == KeyboardHook.Hotkey.None,
                "hotkeys: F12 / Ctrl+F12 / Alt+F12 = pad mode / leave / my PC; Shift+F12, Ctrl+Alt+F12 and K are not");

            File.WriteAllText(tmp, "[Join]\nToggleKey=F9\nLeaveKey=Alt+Q\nSwitchKey=Ctrl+Tab\n");
            var hk = LanPadConfig.Load(tmp, null);
            Check(hk.PadKey == Keys.F9 && hk.LeaveKey == (Keys.Alt | Keys.Q) && hk.SwitchKey == (Keys.Control | Keys.Tab),
                "config: own hotkeys loaded (F9, Alt+Q, Ctrl+Tab)");
            hk.LeaveKey = Keys.Control | Keys.Shift | Keys.L;
            hk.SwitchKey = Keys.Alt | Keys.D1;
            hk.SaveSettings(tmp);
            var hkBack = LanPadConfig.Load(tmp, null);
            Check(hkBack.LeaveKey == (Keys.Control | Keys.Shift | Keys.L) && hkBack.SwitchKey == (Keys.Alt | Keys.D1),
                "config: hotkeys saved and read back");
            File.WriteAllText(tmp, "[Join]\nSwitchKey=Ctrl+F12\n");
            var swWarn = new List<string>();
            var sw = LanPadConfig.Load(tmp, swWarn);
            Check(sw.SwitchKey == (Keys.Alt | Keys.F12) && sw.LeaveKey == (Keys.Control | Keys.F12) && swWarn.Count == 1,
                "config: switch key equal to the leave key -> default hotkeys + warning");
            File.WriteAllText(tmp, "[Join]\nToggleKey=F9\nLeaveKey=F9\n[Keys]\nA=F12\n");
            var hkWarn = new List<string>();
            var dup = LanPadConfig.Load(tmp, hkWarn);
            Check(dup.PadKey == Keys.F12 && dup.LeaveKey == (Keys.Control | Keys.F12) && hkWarn.Count == 2,
                "config: the same key twice -> default hotkeys + warning; a controller key used as hotkey -> warning");
            File.Delete(tmp);


            string real = Path.Combine(AppPaths.AppDir, "lanpad.ini");
            if (File.Exists(real))
            {
                var w2 = new List<string>();
                var c2 = LanPadConfig.Load(real, w2);
                Check(w2.Count == 0, "config: shipped lanpad.ini has no warnings" + (w2.Count > 0 ? ": " + string.Join("; ", w2) : ""));
                Check(c2.KeyMap.Get(PadAction.RT).SequenceEqual(new[] { Keys.LShiftKey }), "config: shipped lanpad.ini RT = LShift");
            }
        }

        // ------------------------------------------------------------------ games, layouts, profiles

        static void GameTests()
        {
            var sports = KeyMap.Default();
            foreach (ControlLayout layout in Enum.GetValues(typeof(ControlLayout)))
            {
                var m = Layouts.Build(layout, sports);
                var back = KeyMap.FromBytes(m.ToBytes());
                bool same = back != null && Enum.GetValues(typeof(PadAction)).Cast<PadAction>()
                    .All(a => back.Get(a).SequenceEqual(m.Get(a)));
                Check(same, $"layout {layout}: survives sending host -> join");
                bool usesToggle = Enum.GetValues(typeof(PadAction)).Cast<PadAction>().Any(a => m.Get(a).Contains(Keys.F12));
                Check(!usesToggle, $"layout {layout}: does not use F12 (toggle key)");
            }
            Check(KeyMap.FromBytes(new byte[] { 5, 0, 1 }) == null, "layout: broken key data rejected");

            var f = Layouts.Fighting();
            Check(MapKeys(f, Keys.U).Buttons == PadButtons.X && MapKeys(f, Keys.I).Buttons == PadButtons.Y &&
                  MapKeys(f, Keys.J).Buttons == PadButtons.A && MapKeys(f, Keys.K).Buttons == PadButtons.B,
                "fighting: U I J K = X Y A B (Tekken 1 2 3 4)");
            Check(MapKeys(f, Keys.O).Buttons == PadButtons.RightShoulder && MapKeys(f, Keys.L).RightTrigger == 255,
                "fighting: O = RB, L = RT (Street Fighter heavy punch / kick)");
            var a = Layouts.Action();
            Check(MapKeys(a, Keys.Space).Buttons == PadButtons.A && MapKeys(a, Keys.J).Buttons == PadButtons.X,
                "action: Space = A (jump), J = X");
            var r = Layouts.Racing();
            var gas = MapKeys(r, Keys.W);
            Check(gas.RightTrigger == 255 && gas.LeftY == 32767, "racing: W = gas (RT) and stick up");
            Check(MapKeys(r, Keys.S).LeftTrigger == 255 && MapKeys(r, Keys.D).LeftX == 32767, "racing: S = brake (LT), D = steer right");

            // The shipped game list
            string shipped = Path.Combine(AppPaths.AppDir, "games.ini");
            if (File.Exists(shipped))
            {
                var w = new List<string>();
                var games = GameCatalog.Load(shipped, null, w);
                Check(w.Count == 0, "games.ini: loads without warnings" + (w.Count > 0 ? ": " + string.Join("; ", w.Take(3)) : ""));
                Check(games.Count >= 100, $"games.ini: {games.Count} games");
                Check(games.Select(g => g.Name.ToLowerInvariant()).Distinct().Count() == games.Count, "games.ini: no duplicate names");
                Check(games.All(g => g.Players >= 1 && g.Players <= 5), "games.ini: player counts 1-5");
                Check(games.All(g => g.Name.Length <= 64), "games.ini: names fit in a packet");
                bool allBuild = games.All(g => KeyMap.FromBytes(g.BuildKeyMap(sports).ToBytes()) != null);
                Check(allBuild, "games.ini: every game's controls can be sent");
                var nba = games.FirstOrDefault(g => g.Name == "NBA 2K14");
                Check(nba != null && nba.Find.Contains("nba2k14.exe") && nba.Layout == ControlLayout.Sports && nba.Players == 5,
                    "games.ini: NBA 2K14 = Sports, 5 players, finds nba2k14.exe");
                var rl = games.FirstOrDefault(g => g.Name == "Rocket League");
                Check(rl != null && MapKeys(rl.BuildKeyMap(sports), Keys.LShiftKey).Buttons == PadButtons.B,
                    "games.ini: Rocket League Shift = boost (B)");
                Check(games.Any(g => g.Name == GameCatalog.GenericName), "games.ini: has 'Any other game'");
            }
            else Fail("games.ini missing next to LanPad.exe");

            // games.user.ini: change a shipped game, add a new one, Browse saves the exe
            string dir = Path.Combine(Path.GetTempPath(), "lanpad-selftest-games");
            Directory.CreateDirectory(dir);
            string cat = Path.Combine(dir, "games.ini"), user = Path.Combine(dir, "games.user.ini");
            File.WriteAllText(cat, "[Tekken 7]\nPlayers=2\nLayout=Fighting\nFind=TEKKEN 7.exe\n");
            File.WriteAllText(user, "; mine\n[Tekken 7]\nA=Space\n\n[My Party Game]\nPlayers=4\nLayout=Action\n[Bad Game]\nLayout=Dancing\n");
            var w2 = new List<string>();
            var list = GameCatalog.Load(cat, user, w2);
            var tekken = list.FirstOrDefault(g => g.Name == "Tekken 7");
            Check(tekken != null && MapKeys(tekken.BuildKeyMap(sports), Keys.Space).Buttons == PadButtons.A &&
                  tekken.Layout == ControlLayout.Fighting, "games.user.ini: changes a key of a shipped game");
            Check(list.Any(g => g.Name == "My Party Game" && g.Players == 4 && g.Layout == ControlLayout.Action), "games.user.ini: adds a new game");
            Check(w2.Count == 1 && w2[0].Contains("Layout"), "games.user.ini: warns about a bad layout");

            GameCatalog.SaveExe(user, "Tekken 7", @"D:\Games\Tekken 7\TEKKEN 7.exe");
            GameCatalog.SaveExe(user, "Brand New", @"E:\x\y.exe");
            var again = GameCatalog.Load(cat, user, null);
            Check(again.First(g => g.Name == "Tekken 7").Exe == @"D:\Games\Tekken 7\TEKKEN 7.exe" &&
                  again.First(g => g.Name == "Brand New").Exe == @"E:\x\y.exe" &&
                  File.ReadAllText(user).Contains("; mine"), "games.user.ini: Browse saves the exe, keeps comments");

            // Finding installed games
            string root = Path.Combine(dir, "Offline Games");
            string gameDir = Path.Combine(root, "Overcooked 2", "Game");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "Overcooked2.exe"), "");
            var found = new List<GameProfile>
            {
                new GameProfile { Name = "Overcooked! 2", Find = new[] { "Overcooked2.exe" } },
                new GameProfile { Name = "Not here", Find = new[] { "nothere.exe" } },
            };
            int n = GameCatalog.Detect(found, new[] { root, Path.Combine(dir, "missing-folder") });
            Check(n == 1 && found[0].Installed && found[0].Exe.EndsWith(@"Overcooked 2\Game\Overcooked2.exe") && !found[1].Installed,
                "games: finds installed games in the game folders");

            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        static void GameSwitchTests()
        {
            var loop = IPAddress.Loopback;
            const int port = 47897;
            var pads = new FakePads(4);
            HostServer server = null;
            JoinClient c1 = null, c2 = null;
            try
            {
                server = new HostServer(pads, port, 4, false, "GameHost", "NBA 2K14", "", loop);
                server.SetGame("NBA 2K14", KeyMap.Default().ToBytes(), 4);
                var hostEp = new IPEndPoint(loop, port);
                c1 = new JoinClient(hostEp, "P1", "", () => PadState.Neutral, loop);
                Check(WaitFor(() => c1.Status == JoinStatus.Joined && c1.HostGameName == "NBA 2K14" && c1.HostKeyMap != null, 3000),
                    "game: joined PC learns the host's game and controls");

                server.SetGame("Tekken 7", Layouts.Fighting().ToBytes(), 1);
                Check(WaitFor(() => c1.HostGameName == "Tekken 7", 1500), "game: switching game reaches the joined PC within a moment");
                Check(c1.HostKeyMap != null && MapKeys(c1.HostKeyMap, Keys.U).Buttons == PadButtons.X,
                    "game: joined PC now uses the fighting controls (U = X)");

                c2 = new JoinClient(hostEp, "P2", "", () => PadState.Neutral, loop);
                Check(WaitFor(() => c2.Status == JoinStatus.Full, 3000), "game: 2-player game lets only 1 PC join (host plays too)");
                Check(c1.Status == JoinStatus.Joined, "game: player already in stays connected after the switch");
            }
            finally
            {
                c1?.Dispose();
                c2?.Dispose();
                server?.Dispose();
            }
        }

        /// <summary>A key change goes out once right away plus ExtraSends copies 10 ms apart (before the 50 ms heartbeat).</summary>
        static void InputResendTests()
        {
            var loop = IPAddress.Loopback;
            using (var fakeHost = new UdpClient(new IPEndPoint(loop, 0)))
            using (Tweaks.BeginSession()) // 1 ms timer, as in a real game
            {
                fakeHost.Client.ReceiveTimeout = 2000;
                var hostEp = (IPEndPoint)fakeHost.Client.LocalEndPoint;
                var want = new PadState { Buttons = PadButtons.A, LeftY = 32767 };
                PadState current = PadState.Neutral;
                var c = new JoinClient(hostEp, "Resend", "", () => current, loop) { ExtraSends = 2 };
                try
                {
                    IPEndPoint from = null;
                    Packet join = null;
                    var until = DateTime.UtcNow.AddSeconds(2);
                    while (join == null && DateTime.UtcNow < until)
                    {
                        byte[] d = fakeHost.Receive(ref from);
                        if (Packet.TryParse(d, d.Length, out var p) && p.Type == PacketType.Join) join = p;
                    }
                    Check(join != null, "input: join request reaches the host");
                    if (join == null) return;
                    byte[] welcome = new Packet(PacketType.Welcome, join.ClientId) { Slot = 1 }.ToBytes();
                    fakeHost.Send(welcome, welcome.Length, from);
                    Check(WaitFor(() => c.Status == JoinStatus.Joined, 2000), "input: joined the fake host");

                    current = want;
                    var sw = Stopwatch.StartNew();
                    c.Signal();
                    int copies = 0;
                    while (sw.ElapsedMilliseconds < 45)
                    {
                        if (fakeHost.Available == 0) { Thread.Sleep(1); continue; }
                        byte[] d = fakeHost.Receive(ref from);
                        if (Packet.TryParse(d, d.Length, out var p) && p.Type == PacketType.State && p.Pad == want) copies++;
                    }
                    Check(copies >= 3, $"input: a key change is sent 1 + 2 extra times within 45 ms ({copies} copies)");
                }
                finally
                {
                    c.Dispose();
                }
            }
        }

        // ------------------------------------------------------------------ network (loopback)

        static void NetworkTests()
        {
            var loop = IPAddress.Loopback;
            const int port = 47899;
            var oldIdle = HostServer.IdleAfter;
            var oldDrop = HostServer.DropAfter;
            HostServer.IdleAfter = TimeSpan.FromMilliseconds(400);
            HostServer.DropAfter = TimeSpan.FromMilliseconds(1500);
            var pads = new FakePads(2);
            HostServer server = null;
            var clients = new List<JoinClient>();
            try
            {
                pads.Plug(1);
                pads.Plug(2);
                server = new HostServer(pads, port, 2, true, "TestHost", "TestGame", "1234", loop)
                {
                    StreamGroup = "239.255.77.1",
                    StreamPort = 5004,
                };
                var hostEp = new IPEndPoint(loop, port);

                using (var discovery = new HostDiscovery(port, loop))
                {
                    bool found = WaitFor(() => discovery.Snapshot().Any(h => h.HostName == "TestHost" && h.PinRequired && h.SlotsFree == 2), 3000);
                    Check(found, "net: discovery finds the host");
                }

                PadState state1 = PadState.Neutral;
                var c1 = new JoinClient(hostEp, "Player One", "1234", () => state1, loop);
                clients.Add(c1);
                Check(WaitFor(() => c1.Status == JoinStatus.Joined, 3000) && c1.Slot == 1, "net: first player gets controller 1");
                Check(c1.StreamGroup == "239.255.77.1" && c1.StreamPort == 5004, "net: welcome carries the stream address");

                state1 = new PadState { Buttons = PadButtons.A, LeftY = 32767, RightTrigger = 255 };
                c1.Signal();
                Check(WaitFor(() => pads.Get(1) == state1, 1000), "net: key press reaches the host controller");
                var lat = Stopwatch.StartNew();
                state1 = new PadState { Buttons = PadButtons.B };
                c1.Signal();
                bool fast = WaitFor(() => pads.Get(1) == state1, 1000);
                Check(fast && lat.ElapsedMilliseconds < 100, $"net: state change arrives quickly ({lat.ElapsedMilliseconds} ms on loopback)");

                var wrongPin = new JoinClient(hostEp, "Wrong", "0000", () => PadState.Neutral, loop);
                clients.Add(wrongPin);
                Check(WaitFor(() => wrongPin.Status == JoinStatus.BadPin, 3000), "net: wrong PIN refused");

                var c2 = new JoinClient(hostEp, "Player Two", "1234", () => PadState.Neutral, loop);
                clients.Add(c2);
                Check(WaitFor(() => c2.Status == JoinStatus.Joined, 3000) && c2.Slot == 2, "net: second player gets controller 2");

                var c3 = new JoinClient(hostEp, "Player Three", "1234", () => PadState.Neutral, loop);
                clients.Add(c3);
                Check(WaitFor(() => c3.Status == JoinStatus.Full, 3000), "net: third player told room is full");
                Check(server.PlayerCount == 2, "net: host has 2 players");

                server.Kick(c2.ClientId);
                Check(WaitFor(() => c2.Status == JoinStatus.Kicked, 2000), "net: kicked player is told");
                Check(server.PlayerCount == 1, "net: kick frees the slot");

                c1.Dispose(); // sends Leave
                Check(WaitFor(() => server.PlayerCount == 0, 2000), "net: leaving frees the slot");

                // Raw client: sequence handling, idle release, timeout drop, unknown client.
                using (var raw = new UdpClient(new IPEndPoint(loop, 0)))
                {
                    NetUtil.DisableUdpConnReset(raw);
                    raw.Client.ReceiveTimeout = 2000;
                    void SendRaw(Packet p) { byte[] b = p.ToBytes(); raw.Send(b, b.Length, hostEp); }
                    Packet ReceiveRaw()
                    {
                        try
                        {
                            IPEndPoint from = null;
                            byte[] d = raw.Receive(ref from);
                            return Packet.TryParse(d, d.Length, out var p) ? p : null;
                        }
                        catch (SocketException) { return null; }
                    }

                    SendRaw(new Packet(PacketType.State, 999) { Seq = 1 });
                    var reply = ReceiveRaw();
                    Check(reply != null && reply.Type == PacketType.NotJoined, "net: unknown client asked to join again");

                    SendRaw(new Packet(PacketType.Join, 555) { PlayerName = "Raw", Pin = "1234" });
                    var w = ReceiveRaw();
                    Check(w != null && w.Type == PacketType.Welcome && w.Slot >= 1, "net: raw join welcomed");
                    int slot = w?.Slot ?? 1;

                    var newer = new PadState { Buttons = PadButtons.Y };
                    var older = new PadState { Buttons = PadButtons.X };
                    SendRaw(new Packet(PacketType.State, 555) { Seq = 10, Pad = newer });
                    Thread.Sleep(100);
                    SendRaw(new Packet(PacketType.State, 555) { Seq = 9, Pad = older });
                    Thread.Sleep(100);
                    Check(pads.Get(slot) == newer, "net: late (older) packet ignored");

                    Check(WaitFor(() => pads.Get(slot).IsNeutral, 1500), "net: buttons released when input stops (no stuck keys)");
                    Check(WaitFor(() => server.PlayerCount == 0, 3000), "net: silent player removed after timeout");
                }
            }
            finally
            {
                foreach (var c in clients) c.Dispose();
                server?.Dispose();
                HostServer.IdleAfter = oldIdle;
                HostServer.DropAfter = oldDrop;
            }

            Check(NetUtil.MulticastGroupFor(IPAddress.Parse("192.168.1.23")).ToString() == "239.255.77.23", "net: multicast group from host IP");
            Check(NetUtil.BroadcastTargets().Contains(IPAddress.Broadcast), "net: broadcast targets include 255.255.255.255");
        }

        // ------------------------------------------------------------------ real driver (ViGEmBus + XInput)

        [StructLayout(LayoutKind.Sequential)]
        struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LX, LY, RX, RY;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [DllImport("xinput1_4.dll")]
        static extern int XInputGetState(int userIndex, out XInputState state);

        static List<int> ConnectedXInput()
        {
            var list = new List<int>();
            for (int i = 0; i < 4; i++)
                if (XInputGetState(i, out _) == 0) list.Add(i);
            return list;
        }

        static bool AnyXInputMatches(PadState s)
        {
            for (int i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out var x) != 0) continue;
                var g = x.Gamepad;
                if (g.Buttons == (ushort)s.Buttons && g.LeftTrigger == s.LeftTrigger && g.RightTrigger == s.RightTrigger &&
                    g.LX == s.LeftX && g.LY == s.LeftY && g.RX == s.RightX && g.RY == s.RightY)
                    return true;
            }
            return false;
        }

        static void DriverTests()
        {
            VirtualPads pads;
            int before = ConnectedXInput().Count;
            if (before > 2)
            {
                // Windows has only 4 XInput slots; a LanPad host (or real pads) may be using them.
                Output.AppendLine($"SKIP  driver: {before} Xbox controllers already connected (close LanPad hosting / unplug pads to run this test)");
                return;
            }
            try
            {
                pads = new VirtualPads(2, true);
            }
            catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
            {
                Output.AppendLine("SKIP  driver: ViGEmBus not installed (virtual controller tests skipped)");
                return;
            }

            const int port = 47898;
            HostServer server = null;
            JoinClient join = null;
            try
            {
                Check(WaitFor(() => ConnectedXInput().Count == before + 2, 3000),
                    $"driver: 2 virtual Xbox controllers visible to games (XInput pads: {before} -> {ConnectedXInput().Count})");

                server = new HostServer(pads, port, 2, true, "DriverTest", "Test", "", IPAddress.Loopback);
                var want = new PadState { Buttons = PadButtons.A | PadButtons.DPadUp, RightTrigger = 255, LeftY = 32767, RightX = -32767 };
                PadState current = PadState.Neutral;
                join = new JoinClient(new IPEndPoint(IPAddress.Loopback, port), "Driver", "", () => current, IPAddress.Loopback);
                Check(WaitFor(() => join.Status == JoinStatus.Joined, 3000), "driver: join over loopback");
                current = want;
                join.Signal();
                Check(WaitFor(() => AnyXInputMatches(want), 1500), "driver: keyboard state shows up on a real XInput controller (end to end)");
                current = PadState.Neutral;
                join.Signal();
                Check(WaitFor(() => !AnyXInputMatches(want), 1500), "driver: release shows up too");
            }
            finally
            {
                join?.Dispose();
                server?.Dispose();
                pads.Dispose();
            }
            Check(WaitFor(() => ConnectedXInput().Count == before, 3000), "driver: virtual controllers removed when hosting stops");
        }
    }
}
