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

        /// <summary>Marks a control whose width follows its column (lists, cards; wrap width for labels).</summary>
        static readonly object Fill = new object();

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
        Label hostTitle, pinLabel;
        DotLabel padsLabel, streamLabel;
        DarkListView playerList;
        List<GameProfile> games = new List<GameProfile>();
        GameProfile currentGame;
        DarkComboBox gameBox;
        Label gameNotes, gameInfo;
        bool suppressGameChange;

        // Join
        HostDiscovery discovery;
        KeyboardHook hook;
        JoinClient client;
        Viewer viewer;
        TextBox nameBox, pinBox, ipBox, keysText;
        DarkListView hostList;
        Label joinedTitle;
        Pill padModeLabel;
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
            Icon = Theme.AppIcon;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Font(10f);
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            ClientSize = new Size(820, 680);
            MinimumSize = new Size(800, 640);
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
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
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 16), Visible = false };
            Controls.Add(p);
            return p;
        }

        static Label NewLabel(string text, float size = 10f, FontStyle style = FontStyle.Regular, Color? color = null) =>
            new Label
            {
                Text = text,
                AutoSize = true,
                Font = Theme.Font(size, style),
                ForeColor = color ?? Theme.Text,
                Margin = new Padding(0, 4, 0, 4),
                MaximumSize = new Size(740, 0),
            };

        /// <summary>Label that wraps to the full width of its column.</summary>
        static Label Para(string text, float size = 9.5f, Color? color = null)
        {
            var l = NewLabel(text, size, FontStyle.Regular, color ?? Theme.Muted);
            l.Tag = Fill;
            return l;
        }

        /// <summary>Small upper-case heading above a part of the page.</summary>
        static Label Section(string text, int top = 14) =>
            new Label
            {
                Text = text.ToUpperInvariant(),
                AutoSize = true,
                Font = Theme.Font(8.5f, FontStyle.Bold),
                ForeColor = Theme.Muted,
                Margin = new Padding(0, top, 0, 6),
            };

        static FlatButton NewButton(string text, EventHandler onClick, ButtonKind kind = ButtonKind.Secondary, int width = 150, int height = 38)
        {
            var b = new FlatButton(text, kind) { Size = new Size(width, height), Margin = new Padding(0, 6, 10, 6) };
            b.Click += onClick;
            return b;
        }

        static TextBox NewInput(int width, int maxLength = 0)
        {
            var t = new TextBox
            {
                Width = width,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.SurfaceHi,
                ForeColor = Theme.Text,
                Font = Theme.Font(11f),
                Margin = new Padding(0, 2, 16, 4),
            };
            if (maxLength > 0) t.MaxLength = maxLength;
            return t;
        }

        /// <summary>Caption above an input.</summary>
        static FlowLayoutPanel Field(string caption, Control input) =>
            Stack(new Label
            {
                Text = caption.ToUpperInvariant(),
                AutoSize = true,
                Font = Theme.Font(8f, FontStyle.Bold),
                ForeColor = Theme.Muted,
                Margin = new Padding(0, 0, 0, 3),
            }, input);

        /// <summary>Page column; children tagged <see cref="Fill"/> follow its width.</summary>
        static FlowLayoutPanel Column(Panel parent)
        {
            var f = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
            };
            f.Layout += (s, e) => FitToWidth(f);
            Theme.DarkScrollBars(f);
            parent.Controls.Add(f);
            return f;
        }

        static void FitToWidth(FlowLayoutPanel f)
        {
            int width = f.ClientSize.Width - f.Padding.Horizontal;
            foreach (Control c in f.Controls)
            {
                if (c.Tag != Fill) continue;
                int w = Math.Max(100, width - c.Margin.Horizontal);
                if (c is Label) { if (c.MaximumSize.Width != w) c.MaximumSize = new Size(w, 0); }
                else if (c.Width != w) c.Width = w;
            }
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        static FlowLayoutPanel Stack(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        /// <summary>Logo + title + line under it, at the top of a page.</summary>
        static Control PageHeader(Label title, Label subtitle)
        {
            var logo = new LogoBox(52) { Margin = new Padding(0, 0, 14, 0) };
            title.Margin = new Padding(0, 0, 0, 0);
            subtitle.Margin = new Padding(1, 0, 0, 0);
            var row = Row(logo, Stack(title, subtitle));
            row.Margin = new Padding(0, 0, 0, 10);
            return row;
        }

        /// <summary>Rounded card holding a column of controls; labels in it wrap to its width.</summary>
        static CardPanel Card(params Control[] controls)
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
            var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            inner.Controls.AddRange(controls);
            inner.Layout += (s, e) =>
            {
                int w = Math.Max(80, inner.ClientSize.Width);
                foreach (Control c in inner.Controls)
                    if (c is Label && c.MaximumSize.Width != w) c.MaximumSize = new Size(w, 0);
            };
            card.Controls.Add(inner);
            return card;
        }

        // ---------------------------------------------------------------- menu page

        void BuildMenuPage()
        {
            menuPage = NewPage();
            var col = Column(menuPage);

            var logo = new LogoBox(84) { Margin = new Padding(0, 4, 18, 0) };
            var title = NewLabel("LanPad", 30f, FontStyle.Bold);
            title.Margin = new Padding(0, 0, 0, 0);
            string version = Application.ProductVersion.Split('+')[0];
            var tagline = NewLabel($"Local multiplayer over LAN   ·   v{version}", 11f, FontStyle.Regular, Theme.Muted);
            tagline.Margin = new Padding(3, 0, 0, 0);
            var hero = Row(logo, Stack(title, tagline));
            hero.Margin = new Padding(0, 6, 0, 14);
            col.Controls.Add(hero);

            col.Controls.Add(Para("Play local-multiplayer games (like NBA 2K14) together on different PCs. The host PC runs the game, " +
                                  "every other PC's keyboard becomes an Xbox controller on the host, and the host's screen is " +
                                  "streamed to every joined PC.", 10.5f));

            var host = new CardButton("Host game", "This PC runs the game. Players join with the room PIN.",
                Theme.Glyph.Screen, Theme.Accent) { Size = new Size(356, 196), Margin = new Padding(0, 18, 14, 8) };
            var join = new CardButton("Join game", "Play on another PC's game. Your keyboard becomes a controller.",
                Theme.Glyph.Gamepad, Theme.Violet) { Size = new Size(356, 196), Margin = new Padding(0, 18, 0, 8) };
            host.Click += (s, e) => StartHost();
            join.Click += (s, e) => OpenJoin(null);
            col.Controls.Add(Row(host, join));
            col.Layout += (s, e) =>
            {
                int w = (col.ClientSize.Width - col.Padding.Horizontal - host.Margin.Right) / 2;
                if (w > 200 && host.Width != w) host.Width = join.Width = w;
            };

            col.Controls.Add(Section("How it works", 18));
            col.Controls.Add(Para("1.  Host: HOST GAME, pick the game, Launch game, tell players the room PIN.\n" +
                                  "2.  Players: JOIN GAME, type the PIN, double-click the host.\n" +
                                  "3.  In the game each joined PC is one Xbox controller. F12 = Pad mode on/off, Ctrl+F12 = leave.",
                                  10f, Theme.Text));

            if (warnings.Count > 0)
            {
                col.Controls.Add(Section("Settings warnings (lanpad.ini)", 18));
                col.Controls.Add(Para("- " + string.Join("\n- ", warnings), 9.5f, Theme.Bad));
            }
            var paths = Para($"Settings: {AppPaths.ConfigFile}\nLog: {Log.FilePath}", 8.5f, Theme.Blend(Theme.Muted, Theme.Back, 0.25f));
            paths.Margin = new Padding(0, 18, 0, 0);
            col.Controls.Add(paths);
        }

        // ---------------------------------------------------------------- host

        void BuildHostPage()
        {
            hostPage = NewPage();
            var col = Column(hostPage);
            hostTitle = NewLabel("", 10.5f, FontStyle.Regular, Theme.Muted);
            col.Controls.Add(PageHeader(NewLabel("Hosting", 18f, FontStyle.Bold), hostTitle));

            // room PIN + controllers / stream status side by side
            var pinCaption = NewLabel("ROOM PIN", 8.5f, FontStyle.Bold, Theme.Muted);
            pinCaption.Margin = new Padding(0, 0, 0, 0);
            pinLabel = NewLabel("", 30f, FontStyle.Bold);
            pinLabel.Margin = new Padding(0, 0, 0, 0);
            var pinHint = NewLabel("tell the players", 9f, FontStyle.Regular, Theme.Muted);
            pinHint.Margin = new Padding(0);
            padsLabel = new DotLabel { Font = Theme.Font(10f), ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 8) };
            streamLabel = new DotLabel { Font = Theme.Font(10f), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 4) };
            var statusCaption = NewLabel("STATUS", 8.5f, FontStyle.Bold, Theme.Muted);
            statusCaption.Margin = new Padding(0);
            var cards = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 124, Margin = new Padding(0, 0, 0, 4), Tag = Fill };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            cards.Controls.Add(Card(pinCaption, pinLabel, pinHint), 0, 0);
            var status = Card(statusCaption, padsLabel, streamLabel);
            status.Margin = new Padding(0);
            cards.Controls.Add(status, 1, 0);
            col.Controls.Add(cards);

            gameBox = new DarkComboBox
            {
                Width = 420,
                MaxDropDownItems = 20,
                Margin = new Padding(0, 7, 10, 6),
            };
            gameBox.SelectedIndexChanged += (s, e) =>
            {
                if (!suppressGameChange && gameBox.SelectedItem is GameProfile g) ApplyGame(g, true);
            };
            gameNotes = Para("", 10f, Theme.Text);
            gameInfo = Para("", 9f);

            col.Controls.Add(Section("Game"));
            col.Controls.Add(Row(gameBox,
                NewButton("Browse exe...", (s, e) => BrowseGame(), ButtonKind.Secondary, 126),
                NewButton("Launch game", (s, e) => LaunchGame(), ButtonKind.Primary, 136)));
            col.Controls.Add(gameNotes);
            col.Controls.Add(gameInfo);

            playerList = new DarkListView
            {
                Height = 132,
                Margin = new Padding(0, 0, 0, 4),
                Tag = Fill,
                EmptyText = "No players yet - they click JOIN GAME on their PC and type the room PIN.",
            };
            playerList.Columns.Add("Controller", 110);
            playerList.Columns.Add("Player", 220);
            playerList.Columns.Add("PC address", 160);
            playerList.Columns.Add("Status", 160);
            col.Controls.Add(Section("Joined players"));
            col.Controls.Add(playerList);
            col.Controls.Add(Row(
                NewButton("Kick selected", (s, e) => KickSelected()),
                NewButton("Stop hosting", (s, e) => StopHost(true), ButtonKind.Danger)));
            col.Controls.Add(Para(
                "Tip: set the game to windowed / borderless 1920x1080 so the stream works. Each joined player is one Xbox " +
                "controller in the game; you (host) use this keyboard. \u2714 = game found on this PC.", 9f));
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
            gameInfo.Text = $"{g.Layout} controls for joined PCs   ·   up to {maxRemote} PC(s) can join   ·   " +
                            (g.Installed ? "found: " + g.Exe : "not found on this PC - use Browse exe...");
            gameInfo.ForeColor = g.Installed ? Theme.Muted : Theme.Warn;
            if (pads != null)
                padsLabel.Text = $"{pads.Capacity} virtual Xbox 360 controllers ready  ·  up to {maxRemote} players can join this game";

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

            hostTitle.Text = $"on {cfg.EffectiveHostName}   ·   IP {lan}";
            pinLabel.Text = pin.Length > 0 ? pin : "off";
            padsLabel.Text = $"{pads.Capacity} virtual Xbox 360 controllers ready  ·  up to {server.MaxPlayers} players can join";
            padsLabel.DotColor = Theme.Good;
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
            col.Controls.Add(PageHeader(NewLabel("Join a game", 18f, FontStyle.Bold),
                NewLabel("Type the room PIN, then double-click the host.", 10.5f, FontStyle.Regular, Theme.Muted)));

            nameBox = NewInput(220);
            nameBox.Text = cfg.EffectivePlayerName;
            pinBox = NewInput(90, 4);
            ipBox = NewInput(160);
            ipBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; JoinTyped(); } };
            var joinIp = NewButton("Join IP", (s, e) => JoinTyped(), ButtonKind.Secondary, 96, ipBox.Height + 2);
            joinIp.Margin = new Padding(0, 1, 0, 0);
            var fields = Row(Field("Your name", nameBox), Field("Room PIN", pinBox), Field("Or host IP", Row(ipBox, joinIp)));
            fields.Margin = new Padding(0, 0, 0, 0);
            var card = Card(fields);
            card.Dock = DockStyle.None;
            card.Height = 78;
            card.Margin = new Padding(0, 0, 0, 4);
            card.Tag = Fill;
            col.Controls.Add(card);

            hostList = new DarkListView
            {
                Height = 290,
                Margin = new Padding(0, 0, 0, 4),
                Tag = Fill,
                EmptyText = "Looking for hosts on the LAN...\nThe host clicks HOST GAME on their PC. Not showing up? Type the host's IP above.",
            };
            hostList.Columns.Add("Host PC", 170);
            hostList.Columns.Add("Game", 190);
            hostList.Columns.Add("Free pads", 100);
            hostList.Columns.Add("PIN", 70);
            hostList.Columns.Add("Address", 130);
            hostList.DoubleClick += (s, e) => JoinSelected();
            hostList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; JoinSelected(); } };
            col.Controls.Add(Section("Hosts on the LAN  ·  double-click to join"));
            col.Controls.Add(hostList);

            col.Controls.Add(Row(
                NewButton("Join selected", (s, e) => JoinSelected(), ButtonKind.Primary),
                NewButton("Refresh", (s, e) => discovery?.Refresh()),
                NewButton("Back", (s, e) => { StopDiscovery(); ShowPage(menuPage); }, ButtonKind.Ghost, 110)));
            col.Controls.Add(Para(
                "After joining, your keyboard is the controller (Pad mode). F12 = Pad mode on/off, Ctrl+F12 = leave.", 9f));
        }

        void BuildJoinedPage()
        {
            joinedPage = NewPage();
            var col = Column(joinedPage);
            joinedTitle = NewLabel("", 16f, FontStyle.Bold);
            joinedTitle.MaximumSize = new Size(640, 0);
            col.Controls.Add(PageHeader(joinedTitle,
                NewLabel("Your keyboard is a controller on the host PC.", 10.5f, FontStyle.Regular, Theme.Muted)));
            padModeLabel = new Pill { Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(0, 6, 0, 4) };
            keysText = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Font = new Font("Consolas", 11f),
                Dock = DockStyle.Fill,
                Text = cfg.KeyMap.Describe().Replace("\n", Environment.NewLine),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                TabStop = false,
            };
            // the text box has no padding of its own: put it on a card
            var keysCard = new CardPanel { Height = 172, Padding = new Padding(16, 14, 12, 10), Margin = new Padding(0, 0, 0, 8), Tag = Fill };
            keysCard.Controls.Add(keysText);

            col.Controls.Add(padModeLabel);
            col.Controls.Add(Section("Your controls  ·  set by the host's game", 18));
            col.Controls.Add(keysCard);
            col.Controls.Add(Para(
                $"{KeyMap.KeyName(cfg.ToggleKey)} = Pad mode on/off (off = keyboard works normally again).   " +
                $"Ctrl+{KeyMap.KeyName(cfg.ToggleKey)} = leave the game.", 10f, Theme.Text));
            col.Controls.Add(NewButton("Leave game", (s, e) => LeaveGame(null), ButtonKind.Danger));
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
                }) { Tag = h, UseItemStyleForSubItems = false };
                item.SubItems[2].ForeColor = h.SlotsFree > 0 ? Theme.Good : Theme.Bad;
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
            padModeLabel.Fill = on ? Theme.Blend(Theme.Good, Theme.Back, 0.25f) : Theme.Blend(Theme.Bad, Theme.Back, 0.2f);
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
                streamLabel.DotColor = broadcaster != null && broadcaster.IsRunning ? Theme.Good
                    : streamStatus.Contains("FAILED") || streamStatus.Contains("missing") ? Theme.Bad
                    : streamBusy ? Theme.Warn : Theme.Muted;

                var players = server.Snapshot();
                uint? selected = playerList.SelectedItems.Count > 0 ? (uint?)playerList.SelectedItems[0].Tag : null;
                playerList.BeginUpdate();
                playerList.Items.Clear();
                foreach (var p in players)
                {
                    var item = new ListViewItem(new[]
                    {
                        $"Pad {p.Slot}", p.Name, p.EndPoint.Address.ToString(), p.Idle ? "idle (no input)" : "connected",
                    }) { Tag = p.ClientId, UseItemStyleForSubItems = false };
                    item.SubItems[3].ForeColor = p.Idle ? Theme.Warn : Theme.Good;
                    playerList.Items.Add(item);
                    if (selected == p.ClientId) item.Selected = true;
                }
                playerList.EndUpdate();
            }
        }
    }
}
