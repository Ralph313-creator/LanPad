using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using LanPad.Config;
using LanPad.Games;
using LanPad.Host;
using LanPad.Join;
using LanPad.Net;
using Nefarius.ViGEm.Client.Exceptions;

namespace LanPad.UI
{
    public sealed class MainForm : Form
    {
        public enum StartMode { Menu, Host, Join }

        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        static readonly Color Good = Color.FromArgb(16, 124, 16);
        static readonly Color Bad = Color.FromArgb(196, 43, 28);

        readonly LanPadConfig cfg;
        readonly List<string> warnings;
        readonly StartMode startMode;
        readonly string startJoinAddress;
        readonly ToastForm toast = new ToastForm();
        readonly Timer refreshTimer;

        Panel menuPage, hostPage, joinListPage, joinedPage;

        // Host
        VirtualPads pads;
        HostServer server;
        StreamBroadcaster broadcaster;
        bool streamBusy;
        DateTime streamRetryAfter = DateTime.MinValue;
        string streamStatus = "Stream: waiting for players";
        Label hostTitle, pinLabel, padsLabel, streamLabel;
        ListView playerList;
        List<GameProfile> games = new List<GameProfile>();
        GameProfile currentGame;
        ComboBox gameBox;
        Label gameNotes, gameInfo;
        bool suppressGameChange;

        // Join
        HostDiscovery discovery;
        KeyboardHook hook;
        JoinClient client;
        Viewer viewer;
        TextBox nameBox, pinBox, ipBox, keysText;
        ListView hostList;
        Label joinedTitle, padModeLabel;
        volatile KeyMap activeKeyMap;

        readonly string startPin;

        public MainForm(LanPadConfig cfg, List<string> warnings, StartMode startMode, string startJoinAddress, string startPin = null)
        {
            this.cfg = cfg;
            this.warnings = warnings;
            this.startMode = startMode;
            this.startJoinAddress = startJoinAddress;
            this.startPin = startPin;

            Text = "LanPad - LAN controllers for local multiplayer";
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(680, 520);
            StartPosition = FormStartPosition.CenterScreen;

            BuildMenuPage();
            BuildHostPage();
            BuildJoinListPage();
            BuildJoinedPage();
            ShowPage(menuPage);

            refreshTimer = new Timer { Interval = 500 };
            refreshTimer.Tick += (s, e) => RefreshUi();
            refreshTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startMode == StartMode.Host) StartHost();
            else if (startMode == StartMode.Join) OpenJoin(startJoinAddress);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            refreshTimer.Stop();
            LeaveGame(null);
            StopDiscovery();
            StopHost(false);
            toast.Dispose();
            base.OnFormClosing(e);
        }

        // ---------------------------------------------------------------- layout helpers

        void ShowPage(Panel page)
        {
            foreach (var p in new[] { menuPage, hostPage, joinListPage, joinedPage })
                p.Visible = p == page;
        }

        Panel NewPage()
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20), Visible = false };
            Controls.Add(p);
            return p;
        }

        static Label NewLabel(string text, float size = 10f, FontStyle style = FontStyle.Regular, Color? color = null) =>
            new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", size, style),
                ForeColor = color ?? SystemColors.ControlText,
                Margin = new Padding(0, 4, 0, 4),
                MaximumSize = new Size(700, 0),
            };

        static Button NewButton(string text, EventHandler onClick, int width = 150, int height = 36)
        {
            var b = new Button { Text = text, Size = new Size(width, height), Margin = new Padding(0, 6, 10, 6) };
            b.Click += onClick;
            return b;
        }

        static FlowLayoutPanel Column(Panel parent)
        {
            var f = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
            };
            parent.Controls.Add(f);
            return f;
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        // ---------------------------------------------------------------- menu page

        void BuildMenuPage()
        {
            menuPage = NewPage();
            var col = Column(menuPage);
            col.Controls.Add(NewLabel("LanPad", 26f, FontStyle.Bold, Accent));
            col.Controls.Add(NewLabel("Play local-multiplayer games (like NBA 2K14) together on different PCs over LAN.\n" +
                                      "The host PC runs the game. Every other PC's keyboard becomes an Xbox controller on the host,\n" +
                                      "and the host's screen is shown on every joined PC."));
            var host = NewButton("HOST GAME\nthis PC runs the game", (s, e) => StartHost(), 300, 90);
            var join = NewButton("JOIN GAME\nplay on another PC's game", (s, e) => OpenJoin(null), 300, 90);
            host.Font = join.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            col.Controls.Add(Row(host, join));
            if (warnings.Count > 0)
                col.Controls.Add(NewLabel("Settings warnings (lanpad.ini):\n- " + string.Join("\n- ", warnings), 9f, FontStyle.Regular, Bad));
            col.Controls.Add(NewLabel($"Settings: {AppPaths.ConfigFile}\nLog: {Log.FilePath}", 8.5f, FontStyle.Regular, Color.Gray));
        }

        // ---------------------------------------------------------------- host

        void BuildHostPage()
        {
            hostPage = NewPage();
            var col = Column(hostPage);
            hostTitle = NewLabel("", 14f, FontStyle.Bold, Accent);
            pinLabel = NewLabel("", 26f, FontStyle.Bold);
            padsLabel = NewLabel("");
            streamLabel = NewLabel("");
            playerList = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                Size = new Size(700, 170),
                Margin = new Padding(0, 6, 0, 6),
            };
            playerList.Columns.Add("Controller", 90);
            playerList.Columns.Add("Player", 200);
            playerList.Columns.Add("PC address", 150);
            playerList.Columns.Add("Status", 200);

            gameBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 430,
                MaxDropDownItems = 25,
                Margin = new Padding(0, 6, 10, 6),
            };
            gameBox.SelectedIndexChanged += (s, e) =>
            {
                if (!suppressGameChange && gameBox.SelectedItem is GameProfile g) ApplyGame(g, true);
            };
            gameNotes = NewLabel("", 9.5f, FontStyle.Regular, Color.FromArgb(60, 60, 60));
            gameInfo = NewLabel("", 9f, FontStyle.Regular, Color.DimGray);

            col.Controls.Add(hostTitle);
            col.Controls.Add(pinLabel);
            col.Controls.Add(Row(NewLabel("Game:", 11f, FontStyle.Bold), gameBox,
                NewButton("Browse exe...", (s, e) => BrowseGame(), 110, 30),
                NewButton("Launch game", (s, e) => LaunchGame(), 110, 30)));
            col.Controls.Add(gameNotes);
            col.Controls.Add(gameInfo);
            col.Controls.Add(padsLabel);
            col.Controls.Add(streamLabel);
            col.Controls.Add(NewLabel("Joined players:", 10f, FontStyle.Bold));
            col.Controls.Add(playerList);
            col.Controls.Add(Row(
                NewButton("Kick selected", (s, e) => KickSelected()),
                NewButton("Stop hosting", (s, e) => StopHost(true))));
            col.Controls.Add(NewLabel(
                "Tips: tell players the room PIN. Set the game to windowed / borderless 1920x1080 so the stream works.\n" +
                "Each joined player is one Xbox controller in the game; you (host) use this keyboard. \u2714 = found on this PC.",
                9f, FontStyle.Regular, Color.DimGray));
        }

        void LoadGames()
        {
            var w = new List<string>();
            games = GameCatalog.Load(w);
            foreach (string x in w) Log.Warn(x);
            var pick = games.FirstOrDefault(g => string.Equals(g.Name, cfg.Game, StringComparison.OrdinalIgnoreCase))
                       ?? games.FirstOrDefault(g => g.Name == "NBA 2K14") ?? games[0];
            FillGameBox(pick);

            // Look for installed games in the background, then show the check marks.
            var list = games;
            var roots = GameCatalog.SplitFolders(cfg.GameFolders).ToList();
            Task.Run(() =>
            {
                int found = GameCatalog.Detect(list, roots);
                Log.Info($"Game search: {found} installed games found in {string.Join("; ", roots)}");
                if (IsHandleCreated) BeginInvoke((Action)(() => { if (list == games) FillGameBox(currentGame); }));
            });
        }

        void FillGameBox(GameProfile select)
        {
            suppressGameChange = true;
            gameBox.BeginUpdate();
            gameBox.Items.Clear();
            foreach (var g in games) gameBox.Items.Add(g);
            gameBox.EndUpdate();
            gameBox.SelectedItem = select;
            suppressGameChange = false;
            if (select != null) ApplyGame(select, false);
        }

        /// <summary>Makes <paramref name="g"/> the current game: controls for joined PCs, join limit, notes.</summary>
        void ApplyGame(GameProfile g, bool remember)
        {
            currentGame = g;
            var keys = g.BuildKeyMap(cfg.KeyMap);
            int maxRemote = Math.Max(1, Math.Min(cfg.MaxPlayers, g.Players - (cfg.HostPlays ? 1 : 0)));
            server?.SetGame(g.Name, keys.ToBytes(), maxRemote);

            gameNotes.Text = string.IsNullOrWhiteSpace(g.Notes) ? DefaultNotes(g.Layout) : g.Notes;
            gameInfo.Text = $"Controls sent to joined PCs: {g.Layout} layout   |   up to {maxRemote} PC(s) can join   |   " +
                            (g.Installed ? "found: " + g.Exe : "not found on this PC - use Browse exe...");
            if (pads != null)
                padsLabel.Text = $"Controllers: {pads.Capacity} virtual Xbox 360 controllers ready (up to {maxRemote} players can join this game)";

            if (remember)
            {
                cfg.Game = g.Name;
                try { IniFile.SetValue(AppPaths.ConfigFile, "Host", "Game", g.Name); }
                catch (Exception ex) { Log.Warn("Could not save the selected game: " + ex.Message); }
            }
        }

        static string DefaultNotes(ControlLayout layout)
        {
            switch (layout)
            {
                case ControlLayout.Fighting: return "Versus mode: the joined PC is player 2. Keys: U I O P / J K L ; (Tekken: U I J K = 1 2 3 4).";
                case ControlLayout.Action: return "Start local co-op; joined players press Space (A) or Enter (Start) to join.";
                case ControlLayout.Racing: return "Split-screen race: joined players press Space (A) or Enter (Start) to join.";
                default: return "In the game's controller screen, move each controller to a team.";
            }
        }

        void BrowseGame()
        {
            if (currentGame == null) return;
            using (var dlg = new OpenFileDialog
            {
                Title = $"Find the .exe of {currentGame.Name}",
                Filter = "Game (*.exe)|*.exe",
                InitialDirectory = GameCatalog.SplitFolders(cfg.GameFolders).FirstOrDefault(Directory.Exists) ?? "C:\\",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                currentGame.Exe = dlg.FileName;
                try { GameCatalog.SaveExe(GameCatalog.UserFile, currentGame.Name, dlg.FileName); }
                catch (Exception ex) { Log.Warn("Could not save games.user.ini: " + ex.Message); }
                FillGameBox(currentGame);
            }
        }

        void StartHost()
        {
            if (server != null) { ShowPage(hostPage); return; }

            try
            {
                pads = new VirtualPads(cfg.MaxPlayers, cfg.PrePlugPads);
            }
            catch (VigemBusNotFoundException)
            {
                MessageBox.Show(this,
                    "The ViGEmBus driver (virtual Xbox controllers) is not installed on this PC.\n\n" +
                    "Install it once (as admin) with scripts\\install-image.ps1 or the ViGEmBus installer, then try again.",
                    "LanPad - driver missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not create virtual controllers:\n" + ex.Message, "LanPad",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string pin = MakePin(cfg.RoomPin);
            try
            {
                server = new HostServer(pads, cfg.Port, cfg.MaxPlayers, cfg.PrePlugPads, cfg.EffectiveHostName, cfg.Game, pin);
            }
            catch (SocketException ex)
            {
                pads.Dispose();
                pads = null;
                MessageBox.Show(this, $"UDP port {cfg.Port} is busy (is LanPad already hosting on this PC?).\n{ex.Message}",
                    "LanPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var lan = NetUtil.PrimaryLanAddress();
            server.StreamGroup = NetUtil.MulticastGroupFor(lan).ToString();
            server.StreamPort = cfg.StreamPort;
            server.PlayersChanged += () => BeginInvoke((Action)RefreshUi);
            broadcaster = new StreamBroadcaster(cfg);

            hostTitle.Text = $"HOSTING on {cfg.EffectiveHostName}  -  IP {lan}";
            pinLabel.Text = pin.Length > 0 ? $"ROOM PIN:  {pin}" : "ROOM PIN:  off";
            padsLabel.Text = $"Controllers: {pads.Capacity} virtual Xbox 360 controllers ready (up to {server.MaxPlayers} players can join)";
            padsLabel.ForeColor = Good;
            LoadGames();
            streamStatus = StreamBroadcaster.FindFfmpeg() == null
                ? "Stream: ffmpeg.exe missing in tools folder - players will not see the screen"
                : "Stream: starts when the first player joins";
            ShowPage(hostPage);
            RefreshUi();
        }

        static string MakePin(string setting)
        {
            string s = (setting ?? "").Trim().ToLowerInvariant();
            if (s == "off" || s == "none" || s == "0") return "";
            if (s.Length == 4 && s.All(char.IsDigit)) return s;
            return new Random().Next(1000, 10000).ToString();
        }

        void StopHost(bool showMenu)
        {
            server?.Dispose();
            server = null;
            broadcaster?.Dispose();
            broadcaster = null;
            pads?.Dispose();
            pads = null;
            streamBusy = false;
            if (showMenu) ShowPage(menuPage);
        }

        void LaunchGame()
        {
            if (currentGame == null) return;
            if (!currentGame.Installed)
            {
                MessageBox.Show(this,
                    $"{currentGame.Name} was not found on this PC.\n\nClick \"Browse exe...\" and pick the game's .exe " +
                    "(LanPad remembers it), or start the game yourself.", "LanPad", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(currentGame.Exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(currentGame.Exe),
                    UseShellExecute = true,
                });
                Log.Info($"Launched {currentGame.Name}: {currentGame.Exe}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start the game:\n" + ex.Message, "LanPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void KickSelected()
        {
            if (server == null || playerList.SelectedItems.Count == 0) return;
            server.Kick((uint)playerList.SelectedItems[0].Tag);
            RefreshUi();
        }

        void UpdateStream()
        {
            if (server == null || broadcaster == null || streamBusy) return;
            bool want = cfg.AlwaysStream || server.PlayerCount > 0;
            if (want && (!broadcaster.IsRunning || broadcaster.IsStalled) && DateTime.UtcNow >= streamRetryAfter)
            {
                if (StreamBroadcaster.FindFfmpeg() == null) return;
                // Start() stops the old ffmpeg first, so a hung stream is simply started again.
                if (broadcaster.IsRunning) Log.Warn("The stream picture stopped for 5 s; restarting the stream");
                streamBusy = true;
                streamStatus = "Stream: starting...";
                var first = server.Snapshot().FirstOrDefault();
                var local = NetUtil.LocalAddressFor(first?.EndPoint?.Address ?? IPAddress.Loopback);
                string group = server.StreamGroup;
                int port = server.StreamPort;
                var b = broadcaster;
                Task.Run(() =>
                {
                    bool ok = b.Start(group, port, local, out string error);
                    BeginInvoke((Action)(() =>
                    {
                        streamBusy = false;
                        if (ok)
                        {
                            streamStatus = $"Stream: ON  -  {b.EncoderInUse}  ->  {b.Target}";
                        }
                        else
                        {
                            streamStatus = "Stream FAILED (retry in 10 s): " + error;
                            streamRetryAfter = DateTime.UtcNow.AddSeconds(10);
                        }
                    }));
                });
            }
            else if (!want && broadcaster.IsRunning)
            {
                broadcaster.Stop();
                streamStatus = "Stream: stopped (no players)";
            }
        }

        // ---------------------------------------------------------------- join

        void BuildJoinListPage()
        {
            joinListPage = NewPage();
            var col = Column(joinListPage);
            col.Controls.Add(NewLabel("JOIN A GAME", 14f, FontStyle.Bold, Accent));
            nameBox = new TextBox { Width = 220, Text = cfg.EffectivePlayerName };
            pinBox = new TextBox { Width = 80, MaxLength = 4 };
            col.Controls.Add(Row(NewLabel("Your name:"), nameBox, NewLabel("   Room PIN:"), pinBox));

            hostList = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                Size = new Size(700, 200),
                Margin = new Padding(0, 6, 0, 6),
            };
            hostList.Columns.Add("Host PC", 180);
            hostList.Columns.Add("Game", 170);
            hostList.Columns.Add("Free controllers", 120);
            hostList.Columns.Add("PIN", 60);
            hostList.Columns.Add("Address", 140);
            hostList.DoubleClick += (s, e) => JoinSelected();
            col.Controls.Add(NewLabel("Hosts found on the LAN (double-click to join):", 10f, FontStyle.Bold));
            col.Controls.Add(hostList);

            ipBox = new TextBox { Width = 160 };
            col.Controls.Add(Row(
                NewButton("Join selected", (s, e) => JoinSelected()),
                NewButton("Refresh", (s, e) => discovery?.Refresh()),
                NewButton("Back", (s, e) => { StopDiscovery(); ShowPage(menuPage); })));
            col.Controls.Add(Row(NewLabel("Or host IP:"), ipBox, NewButton("Join IP", (s, e) => JoinTyped(), 100, 30)));
            col.Controls.Add(NewLabel(
                "After joining, your keyboard is the controller (Pad mode). F12 = Pad mode on/off, Ctrl+F12 = leave.",
                9f, FontStyle.Regular, Color.DimGray));
        }

        void BuildJoinedPage()
        {
            joinedPage = NewPage();
            var col = Column(joinedPage);
            joinedTitle = NewLabel("", 14f, FontStyle.Bold, Accent);
            padModeLabel = NewLabel("", 16f, FontStyle.Bold);
            keysText = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Font = new Font("Consolas", 11f),
                Size = new Size(700, 150),
                Text = cfg.KeyMap.Describe().Replace("\n", Environment.NewLine),
                BackColor = SystemColors.Window,
                TabStop = false,
            };
            col.Controls.Add(joinedTitle);
            col.Controls.Add(padModeLabel);
            col.Controls.Add(NewLabel("Your controls (set by the host's game):", 10f, FontStyle.Bold));
            col.Controls.Add(keysText);
            col.Controls.Add(NewLabel(
                $"{KeyMap.KeyName(cfg.ToggleKey)} = Pad mode on/off (off = keyboard works normally again).   " +
                $"Ctrl+{KeyMap.KeyName(cfg.ToggleKey)} = leave the game.", 10f));
            col.Controls.Add(NewButton("Leave game", (s, e) => LeaveGame(null)));
        }

        void OpenJoin(string address)
        {
            ShowPage(joinListPage);
            StartDiscovery();
            if (!string.IsNullOrEmpty(startPin) && pinBox.Text.Length == 0) pinBox.Text = startPin;
            if (!string.IsNullOrWhiteSpace(address))
            {
                ipBox.Text = address;
                JoinTyped();
            }
        }

        void StartDiscovery()
        {
            if (discovery != null) return;
            try
            {
                discovery = new HostDiscovery(cfg.Port);
                discovery.ListChanged += () => BeginInvoke((Action)RefreshHostList);
            }
            catch (SocketException ex)
            {
                Log.Error("Discovery failed", ex);
            }
        }

        void StopDiscovery()
        {
            discovery?.Dispose();
            discovery = null;
        }

        void RefreshHostList()
        {
            if (discovery == null) return;
            string selected = hostList.SelectedItems.Count > 0 ? hostList.SelectedItems[0].SubItems[4].Text : null;
            hostList.BeginUpdate();
            hostList.Items.Clear();
            foreach (var h in discovery.Snapshot())
            {
                var item = new ListViewItem(new[]
                {
                    h.HostName, h.GameName, $"{h.SlotsFree} / {h.SlotsTotal}", h.PinRequired ? "yes" : "no",
                    h.EndPoint.Address.ToString(),
                }) { Tag = h };
                hostList.Items.Add(item);
                if (h.EndPoint.Address.ToString() == selected) item.Selected = true;
            }
            hostList.EndUpdate();
        }

        void JoinSelected()
        {
            if (hostList.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Pick a host from the list first (or type the host IP).", "LanPad");
                return;
            }
            var h = (HostDiscovery.HostInfo)hostList.SelectedItems[0].Tag;
            if (h.PinRequired && pinBox.Text.Trim().Length != 4)
            {
                MessageBox.Show(this, "This host needs the 4-digit room PIN. Ask the host and type it in Room PIN.", "LanPad");
                pinBox.Focus();
                return;
            }
            JoinHost(h.EndPoint);
        }

        void JoinTyped()
        {
            if (!IPAddress.TryParse(ipBox.Text.Trim(), out var ip))
            {
                MessageBox.Show(this, "Type a valid IP address, for example 192.168.1.20", "LanPad");
                return;
            }
            JoinHost(new IPEndPoint(ip, cfg.Port));
        }

        void JoinHost(IPEndPoint host)
        {
            if (client != null) return;
            try
            {
                hook = new KeyboardHook { ToggleKey = cfg.ToggleKey, SwallowMouse = cfg.SwallowMouse };
                hook.Install();
            }
            catch (Exception ex)
            {
                hook?.Dispose();
                hook = null;
                MessageBox.Show(this, "Could not capture the keyboard:\n" + ex.Message, "LanPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : cfg.EffectivePlayerName;
            var h = hook;
            // Until the host sends its game's controls, use this PC's own [Keys] (Sports layout).
            activeKeyMap = cfg.KeyMap;
            keysText.Text = activeKeyMap.Describe().Replace("\n", Environment.NewLine);
            client = new JoinClient(host, name, pinBox.Text.Trim(), () => KeyMapper.Map(h.IsDown, activeKeyMap));
            var c = client;
            hook.KeysChanged += c.Signal;
            hook.PadModeChanged += on => UpdatePadModeLabel(true);
            hook.LeaveRequested += () => BeginInvoke((Action)(() => LeaveGame(null)));
            client.StatusChanged += s => BeginInvoke((Action)(() => OnJoinStatus(c, s)));
            client.GameChanged += () => BeginInvoke((Action)(() => OnHostGameChanged(c)));

            StopDiscovery();
            joinedTitle.Text = $"Joining {host.Address} ...";
            UpdatePadModeLabel(false);
            ShowPage(joinedPage);
        }

        static string JoinedTitle(JoinClient c) =>
            $"JOINED {c.Host.Address} as controller {c.Slot}" + (c.HostGameName.Length > 0 ? $"  -  {c.HostGameName}" : "");

        /// <summary>The host picked a (new) game: switch to its controls and show them.</summary>
        void OnHostGameChanged(JoinClient c)
        {
            if (c != client) return;
            if (c.HostKeyMap != null)
            {
                activeKeyMap = c.HostKeyMap;
                keysText.Text = activeKeyMap.Describe().Replace("\n", Environment.NewLine);
                c.Signal(); // send the state again with the new controls
            }
            if (c.Status == JoinStatus.Joined) joinedTitle.Text = JoinedTitle(c);
            if (c.HostGameName.Length > 0)
                toast.ShowMessage($"Game: {c.HostGameName}  -  controls updated", Color.LightSkyBlue, 3000);
        }

        void OnJoinStatus(JoinClient c, JoinStatus s)
        {
            if (c != client) return; // old connection
            switch (s)
            {
                case JoinStatus.Joined:
                    joinedTitle.Text = JoinedTitle(c);
                    if (viewer == null)
                    {
                        viewer = new Viewer(cfg.Viewer);
                        var local = NetUtil.LocalAddressFor(c.Host.Address);
                        var screen = Screen.PrimaryScreen.Bounds;
                        string window = $"{screen.Width / 2}x{screen.Height / 2}+{screen.Left + screen.Width / 2}+{screen.Top + screen.Height / 4}";
                        string error = "the host sent no stream address";
                        if (string.IsNullOrEmpty(c.StreamGroup) ||
                            !viewer.Start(c.StreamGroup, c.StreamPort, local, out error, cfg.Fullscreen, false, window))
                        {
                            Log.Warn("Viewer not started: " + error);
                            toast.ShowMessage("No stream viewer - you can still play by looking at the host screen", Color.Orange, 5000);
                        }
                    }
                    if (cfg.AutoPadMode)
                    {
                        hook.PadMode = true;
                        toast.ShowMessage($"Joined as controller {c.Slot}  -  PAD MODE ON ({KeyMap.KeyName(cfg.ToggleKey)})", Color.LightGreen, 4000);
                    }
                    else
                    {
                        toast.ShowMessage($"Joined as controller {c.Slot}  -  press {KeyMap.KeyName(cfg.ToggleKey)} for Pad mode", Color.LightGreen, 4000);
                    }
                    break;
                case JoinStatus.LostHost:
                    joinedTitle.Text = $"Connection to {c.Host.Address} lost - retrying...";
                    toast.ShowMessage("Connection to host lost - retrying...", Color.Orange, 4000);
                    break;
                case JoinStatus.Joining:
                    joinedTitle.Text = $"Re-joining {c.Host.Address} ...";
                    break;
                case JoinStatus.Full:
                    LeaveGame("The room is full: all controllers are taken.");
                    break;
                case JoinStatus.BadPin:
                    LeaveGame("Wrong room PIN. Ask the host for the 4-digit PIN.");
                    break;
                case JoinStatus.Kicked:
                    LeaveGame("The host closed the room or removed you from the game.");
                    break;
            }
        }

        void UpdatePadModeLabel(bool withToast)
        {
            bool on = hook != null && hook.PadMode;
            padModeLabel.Text = on
                ? $"PAD MODE: ON   (keyboard = controller, {KeyMap.KeyName(cfg.ToggleKey)} to turn off)"
                : $"PAD MODE: OFF  (keyboard works normally, {KeyMap.KeyName(cfg.ToggleKey)} to turn on)";
            padModeLabel.ForeColor = on ? Good : Bad;
            if (withToast)
                toast.ShowMessage(on ? "PAD MODE ON" : "PAD MODE OFF - keyboard is normal", on ? Color.LightGreen : Color.Orange);
        }

        void LeaveGame(string message)
        {
            bool wasJoined = client != null;
            viewer?.Stop();
            viewer = null;
            if (hook != null)
            {
                hook.PadMode = false;
                hook.Dispose();
                hook = null;
            }
            client?.Dispose();
            client = null;
            if (!wasJoined) return;
            if (!IsDisposed && !Disposing)
            {
                ShowPage(joinListPage);
                StartDiscovery();
                if (message != null) MessageBox.Show(this, message, "LanPad", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ---------------------------------------------------------------- refresh

        void RefreshUi()
        {
            if (server != null)
            {
                UpdateStream();
                streamLabel.Text = streamStatus;
                streamLabel.ForeColor = broadcaster != null && broadcaster.IsRunning ? Good : (streamStatus.Contains("FAILED") ? Bad : SystemColors.ControlText);

                var players = server.Snapshot();
                uint? selected = playerList.SelectedItems.Count > 0 ? (uint?)playerList.SelectedItems[0].Tag : null;
                playerList.BeginUpdate();
                playerList.Items.Clear();
                foreach (var p in players)
                {
                    var item = new ListViewItem(new[]
                    {
                        $"#{p.Slot}", p.Name, p.EndPoint.Address.ToString(), p.Idle ? "idle (no input)" : "connected",
                    }) { Tag = p.ClientId };
                    playerList.Items.Add(item);
                    if (selected == p.ClientId) item.Selected = true;
                }
                playerList.EndUpdate();
            }
        }
    }
}
