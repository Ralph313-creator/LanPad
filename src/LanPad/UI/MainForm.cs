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
using LanPad.Protocol;
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

        Panel menuPage, hostPage, joinListPage, joinedPage, settingsPage;
        Label joinedHotkeys;
        FlatButton awayButton;
        volatile bool away; // joined player switched to their own PC (stream hidden, Pad mode off, controller released)
        bool padBeforeAway;

        // smooth-play tweaks (Tweaks) for the running host / join session
        IDisposable hostTweaks, joinTweaks;

        // Settings page
        DarkComboBox fpsBox, bitrateBox, encoderBox, viewerBox, resendBox;
        ToggleSwitch highPrioritySwitch, noThrottlingSwitch, keepAwakeSwitch, autoPadSwitch, fullscreenSwitch, swallowMouseSwitch;
        KeyButton padKeyButton, leaveKeyButton, switchKeyButton;
        Label settingsStatus;

        // Host
        VirtualPads pads;
        HostServer server;
        StreamBroadcaster broadcaster;
        bool streamBusy;
        DateTime streamRetryAfter = DateTime.MinValue;
        string streamStatus = "Stream: waiting for players";
        Label hostTitle, pinLabel;
        DotLabel padsLabel, streamLabel;
        int hostControllers; // real Xbox controllers on the host PC when hosting started
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
        LocalGamepad gamepad; // Xbox controller plugged into this PC, sent along with the keyboard
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
            BuildSettingsPage();
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
            if (cfg.CheckUpdates) _ = CheckForUpdate(false);
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
            foreach (var p in new[] { menuPage, hostPage, joinListPage, joinedPage, settingsPage })
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
                else if (c.AutoSize) // grows with its content in height only
                {
                    if (c.MinimumSize.Width != w) { c.MinimumSize = new Size(w, 0); c.MaximumSize = new Size(w, 0); }
                }
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
            var credit = NewLabel("Created by Raptech Solution", 9.5f, FontStyle.Bold, Theme.Accent);
            credit.Margin = new Padding(4, 2, 0, 0);
            var hero = Row(logo, Stack(title, tagline, credit));
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

            var settings = NewButton("⚙  Settings", (s, e) => OpenSettings(), ButtonKind.Secondary, 150);
            settings.Margin = new Padding(0, 16, 10, 0);
            updateButton = NewButton("Check for updates", (s, e) => UpdateClicked(), ButtonKind.Ghost, 200);
            updateButton.Margin = new Padding(0, 16, 10, 0);
            updateStatus = NewLabel("", 9.5f, FontStyle.Regular, Theme.Muted);
            updateStatus.Margin = new Padding(0, 26, 0, 0);
            col.Controls.Add(Row(settings, updateButton, updateStatus));

            if (warnings.Count > 0)
            {
                col.Controls.Add(Section("Settings warnings (lanpad.ini)", 18));
                col.Controls.Add(Para("- " + string.Join("\n- ", warnings), 9.5f, Theme.Bad));
            }
            var paths = Para($"Settings: {AppPaths.ConfigFile}\nLog: {Log.FilePath}", 8.5f, Theme.Blend(Theme.Muted, Theme.Back, 0.25f));
            paths.Margin = new Padding(0, 18, 0, 0);
            col.Controls.Add(paths);
        }

        // ---------------------------------------------------------------- updates (GitHub releases)

        FlatButton updateButton;
        Label updateStatus;
        Updater.UpdateInfo pendingUpdate;
        bool updateBusy;

        void ShowUpdateStatus(string text, Color color)
        {
            updateStatus.Text = text;
            updateStatus.ForeColor = color;
        }

        async Task CheckForUpdate(bool userAsked)
        {
            if (updateBusy) return;
            updateBusy = true;
            if (userAsked) ShowUpdateStatus("Checking...", Theme.Muted);
            var info = await Updater.CheckAsync();
            updateBusy = false;
            if (IsDisposed) return;
            if (info != null && Updater.IsNewer(info.Version, Updater.Current))
            {
                pendingUpdate = info;
                updateButton.Text = $"Update to v{info.Version}";
                updateButton.Kind = ButtonKind.Primary;
                updateButton.Invalidate();
                ShowUpdateStatus("A new version is available", Theme.Good);
            }
            else if (userAsked)
                ShowUpdateStatus(info == null ? "Could not reach GitHub (no internet?)" : $"✔ v{Updater.Current} is the latest",
                                 info == null ? Theme.Warn : Theme.Good);
        }

        async void UpdateClicked()
        {
            if (pendingUpdate == null) { await CheckForUpdate(true); return; }
            if (updateBusy) return;
            if ((server != null || client != null) &&
                MessageBox.Show(this, "Updating closes LanPad and ends the current game for everyone. Update now?", "LanPad update",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            updateBusy = true;
            updateButton.Enabled = false;
            try
            {
                var progress = new Progress<int>(p => ShowUpdateStatus($"Downloading v{pendingUpdate.Version}... {p}%", Theme.Muted));
                string setup = await Updater.DownloadAsync(pendingUpdate, progress);
                ShowUpdateStatus("Installing... LanPad restarts by itself", Theme.Muted);
                if (Updater.RunSetup(setup)) { Close(); return; }
                ShowUpdateStatus("Update cancelled (the setup needs admin)", Theme.Warn);
            }
            catch (Exception ex)
            {
                Log.Error("Update failed", ex);
                ShowUpdateStatus("Update failed: " + ex.Message, Theme.Bad);
            }
            finally
            {
                updateBusy = false;
                if (!IsDisposed) updateButton.Enabled = true;
            }
        }

        // ---------------------------------------------------------------- settings

        /// <summary>Item of a settings drop-down: shown text + the value saved to lanpad.ini.</summary>
        sealed class Choice
        {
            public readonly string Text;
            public readonly object Value;
            public Choice(object value, string text) { Value = value; Text = text; }
            public override string ToString() => Text;
        }

        static DarkComboBox ChoiceBox(int width, params Choice[] choices)
        {
            var box = new DarkComboBox { Width = width, Margin = new Padding(0, 2, 16, 4) };
            box.Items.AddRange(choices);
            return box;
        }

        /// <summary>Selects the choice with <paramref name="value"/>; a value not in the list is added as "custom".</summary>
        static void SelectChoice(ComboBox box, object value, string customText)
        {
            foreach (Choice c in box.Items)
                if (Equals(c.Value, value)) { box.SelectedItem = c; return; }
            var extra = new Choice(value, customText);
            box.Items.Add(extra);
            box.SelectedItem = extra;
        }

        static T Value<T>(ComboBox box, T fallback) => box.SelectedItem is Choice c && c.Value is T v ? v : fallback;

        /// <summary>Switch + title + one line of explanation; clicking the text flips the switch too.</summary>
        static Control ToggleRow(string title, string detail, out ToggleSwitch toggle)
        {
            var sw = new ToggleSwitch { Margin = new Padding(0, 4, 14, 0) };
            var t = NewLabel(title, 10.5f, FontStyle.Bold);
            var d = NewLabel(detail, 9f, FontStyle.Regular, Theme.Muted);
            t.Margin = d.Margin = new Padding(0);
            d.MaximumSize = new Size(600, 0);
            foreach (var l in new[] { t, d })
            {
                l.Cursor = Cursors.Hand;
                l.Click += (s, e) => sw.Checked = !sw.Checked;
            }
            toggle = sw;
            var row = Row(sw, Stack(t, d));
            row.Margin = new Padding(0, 4, 0, 10);
            return row;
        }

        /// <summary>Card that grows with its content; its width follows the column.</summary>
        static CardPanel AutoCard(params Control[] controls)
        {
            // docked, so the card's padding applies and the card's height follows the content
            var inner = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            inner.Controls.AddRange(controls);
            var card = new CardPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4), Tag = Fill };
            card.Padding = new Padding(18, 14, 18, 8);
            card.Controls.Add(inner);
            return card;
        }

        void BuildSettingsPage()
        {
            settingsPage = NewPage();
            var col = Column(settingsPage);
            col.Controls.Add(PageHeader(NewLabel("Settings", 18f, FontStyle.Bold),
                NewLabel("Tweaks for smooth gameplay. Saved in lanpad.ini on this PC.", 10.5f, FontStyle.Regular, Theme.Muted)));

            fpsBox = ChoiceBox(150,
                new Choice(30, "30 fps"), new Choice(60, "60 fps (default)"), new Choice(90, "90 fps"), new Choice(120, "120 fps"));
            bitrateBox = ChoiceBox(170,
                new Choice(5000, "5 Mbps (Wi-Fi)"), new Choice(8000, "8 Mbps"), new Choice(10000, "10 Mbps"),
                new Choice(15000, "15 Mbps (default)"), new Choice(20000, "20 Mbps"), new Choice(30000, "30 Mbps"),
                new Choice(50000, "50 Mbps (sharpest)"));
            encoderBox = ChoiceBox(260,
                new Choice("auto", "Auto (AMD hardware, else CPU)"), new Choice("amf", "AMD hardware only"),
                new Choice("x264", "CPU - x264, 720p 30 fps"));
            col.Controls.Add(Section("Stream quality  ·  host PC"));
            col.Controls.Add(AutoCard(
                Row(Field("Frame rate", fpsBox), Field("Bitrate", bitrateBox), Field("Encoder", encoderBox)),
                Hint("Picture stutters or breaks up? Lower the bitrate first, then the frame rate. " +
                     "Changes apply the next time the stream starts.")));

            resendBox = ChoiceBox(230,
                new Choice(0, "Off"), new Choice(1, "1 extra copy"), new Choice(2, "2 extra copies (default)"),
                new Choice(3, "3 extra copies"), new Choice(4, "4 extra copies (bad Wi-Fi)"));
            col.Controls.Add(Section("Smooth gameplay  ·  host and joined PCs"));
            col.Controls.Add(AutoCard(
                ToggleRow("High priority",
                    "LanPad, the stream encoder and the viewer get the CPU first, so a busy PC does not delay input or the picture.",
                    out highPrioritySwitch),
                ToggleRow("No background slow-down",
                    "Stops Windows 11 from slowing LanPad, ffmpeg and mpv when they are behind the game, and uses a 1 ms timer.",
                    out noThrottlingSwitch),
                ToggleRow("Keep PC awake",
                    "No sleep, screen saver or screen off during a game.",
                    out keepAwakeSwitch),
                Field("Controller packet repeat", resendBox),
                Hint("Each button press is sent again 10 ms later, so a lost network packet does not drop or delay it.")));

            viewerBox = ChoiceBox(200, new Choice("mpv", "mpv (lowest delay)"), new Choice("ffplay", "ffplay"));
            col.Controls.Add(Section("Joined PC"));
            col.Controls.Add(AutoCard(
                Field("Stream viewer", viewerBox),
                ToggleRow("Pad mode on after joining", "Your keyboard becomes the controller right away (the Pad mode hotkey switches it).", out autoPadSwitch),
                ToggleRow("Full screen stream", "Off = the host's screen opens in a window (for testing).", out fullscreenSwitch),
                ToggleRow("Block mouse clicks in Pad mode", "Stops accidental clicks on the stream window.", out swallowMouseSwitch)));

            padKeyButton = HotkeyButton();
            switchKeyButton = HotkeyButton();
            leaveKeyButton = HotkeyButton();
            // two rows: the three captions do not fit next to each other
            Control HotkeyField(string caption, KeyButton b)
            {
                var f = Field(caption, b);
                f.MinimumSize = new Size(260, 0);
                return f;
            }
            var secondRow = Row(HotkeyField("Leave the game", leaveKeyButton));
            secondRow.Margin = new Padding(0, 10, 0, 4);
            col.Controls.Add(Section("Hotkeys  ·  joined PC"));
            col.Controls.Add(AutoCard(
                Row(HotkeyField("Pad mode on / off", padKeyButton), HotkeyField("Switch to my PC / back to the game", switchKeyButton)),
                secondRow,
                Hint("Click a box, then press the key - with Ctrl, Shift or Alt if you like (e.g. Ctrl+F12). Esc = cancel. " +
                     "They work in Pad mode too. A key without Ctrl / Shift / Alt stops working as a controller button.")));

            settingsStatus = NewLabel("", 9.5f, FontStyle.Regular, Theme.Good);
            settingsStatus.Margin = new Padding(0, 6, 0, 0);
            settingsStatus.MaximumSize = new Size(280, 0); // wraps next to the buttons instead of widening the page
            col.Controls.Add(Row(
                NewButton("Save", (s, e) => SaveSettings(), ButtonKind.Primary, 130),
                NewButton("Reset to defaults", (s, e) => { FillSettings(new LanPadConfig()); ShowSettingsStatus("Defaults loaded - click Save to keep them.", Theme.Warn); }, ButtonKind.Secondary, 170),
                NewButton("Back", (s, e) => ShowPage(menuPage), ButtonKind.Ghost, 110),
                settingsStatus));
        }

        KeyButton HotkeyButton()
        {
            var b = new KeyButton { HotkeyMode = true, Size = new Size(170, 30), Margin = new Padding(0, 2, 16, 4) };
            b.KeyChosen += (button, key) =>
            {
                var others = new[] { padKeyButton, leaveKeyButton, switchKeyButton }.Where(x => x != button && x != null);
                if (others.Any(x => x.Value.Length == 1 && x.Value[0] == key))
                {
                    ShowSettingsStatus($"{Hk(key)} is already another hotkey - pick a different one.", Theme.Bad);
                    return;
                }
                button.Value = new[] { key };
                bool plain = (key & Keys.Modifiers) == 0;
                var used = plain ? Enum.GetValues(typeof(PadAction)).Cast<PadAction>().Where(a => cfg.KeyMap.Get(a).Contains(key)).ToList()
                                 : new List<PadAction>();
                if (used.Count > 0)
                    ShowSettingsStatus($"{Hk(key)} is also the {string.Join(", ", used)} button (it will only be the hotkey). Not saved yet.", Theme.Warn);
                else
                    ShowSettingsStatus("Not saved yet", Theme.Muted);
            };
            b.Cleared += button => ShowSettingsStatus("A hotkey cannot be empty.", Theme.Warn);
            return b;
        }

        static Label Hint(string text)
        {
            var l = NewLabel(text, 9f, FontStyle.Regular, Theme.Muted);
            l.MaximumSize = new Size(680, 0);
            l.Margin = new Padding(0, 2, 0, 8);
            return l;
        }

        void OpenSettings()
        {
            FillSettings(cfg);
            settingsStatus.Text = "";
            ShowPage(settingsPage);
        }

        void FillSettings(LanPadConfig c)
        {
            SelectChoice(fpsBox, c.Fps, $"{c.Fps} fps (custom)");
            SelectChoice(bitrateBox, c.BitrateKbps, $"{c.BitrateKbps / 1000.0:0.#} Mbps (custom)");
            SelectChoice(encoderBox, c.Encoder, c.Encoder);
            SelectChoice(viewerBox, c.Viewer, c.Viewer);
            SelectChoice(resendBox, c.InputResend, $"{c.InputResend} extra copies");
            highPrioritySwitch.Checked = c.HighPriority;
            noThrottlingSwitch.Checked = c.NoThrottling;
            keepAwakeSwitch.Checked = c.KeepAwake;
            autoPadSwitch.Checked = c.AutoPadMode;
            fullscreenSwitch.Checked = c.Fullscreen;
            swallowMouseSwitch.Checked = c.SwallowMouse;
            padKeyButton.Value = new[] { c.PadKey };
            leaveKeyButton.Value = new[] { c.LeaveKey };
            switchKeyButton.Value = new[] { c.SwitchKey };
        }

        void SaveSettings()
        {
            cfg.Fps = Value(fpsBox, cfg.Fps);
            cfg.BitrateKbps = Value(bitrateBox, cfg.BitrateKbps);
            cfg.Encoder = Value(encoderBox, cfg.Encoder);
            cfg.Viewer = Value(viewerBox, cfg.Viewer);
            cfg.InputResend = Value(resendBox, cfg.InputResend);
            cfg.HighPriority = highPrioritySwitch.Checked;
            cfg.NoThrottling = noThrottlingSwitch.Checked;
            cfg.KeepAwake = keepAwakeSwitch.Checked;
            cfg.AutoPadMode = autoPadSwitch.Checked;
            cfg.Fullscreen = fullscreenSwitch.Checked;
            cfg.SwallowMouse = swallowMouseSwitch.Checked;
            cfg.PadKey = padKeyButton.Value[0];
            cfg.LeaveKey = leaveKeyButton.Value[0];
            cfg.SwitchKey = switchKeyButton.Value[0];
            if (hook != null)
            {
                hook.PadKey = cfg.PadKey;
                hook.LeaveKey = cfg.LeaveKey;
                hook.SwitchKey = cfg.SwitchKey;
            }
            joinedHotkeys.Text = HotkeysText();
            Tweaks.Configure(cfg); // used from the next host / join session on
            if (client != null) client.ExtraSends = cfg.InputResend;
            if (hook != null) hook.SwallowMouse = cfg.SwallowMouse;
            try
            {
                cfg.SaveSettings(AppPaths.ConfigFile);
                Log.Info($"Settings saved: {cfg.Fps} fps, {cfg.BitrateKbps} kbps, encoder {cfg.Encoder}, viewer {cfg.Viewer}, " +
                         $"high priority {cfg.HighPriority}, no throttling {cfg.NoThrottling}, keep awake {cfg.KeepAwake}, resend {cfg.InputResend}");
                ShowSettingsStatus("✔ Saved", Theme.Good);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not save settings: " + ex.Message);
                ShowSettingsStatus("Could not save lanpad.ini: " + ex.Message, Theme.Bad);
            }
        }

        void ShowSettingsStatus(string text, Color color)
        {
            settingsStatus.Text = text;
            settingsStatus.ForeColor = color;
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
            int maxRemote = Math.Max(1, Math.Min(pads?.Capacity ?? cfg.MaxPlayers, g.Players - (cfg.HostPlays ? 1 : 0)));
            server?.SetGame(g.Name, keys.ToBytes(), maxRemote);

            gameNotes.Text = string.IsNullOrWhiteSpace(g.Notes) ? DefaultNotes(g.Layout) : g.Notes;
            gameInfo.Text = $"{g.Layout} controls for joined PCs   ·   up to {maxRemote} PC(s) can join   ·   " +
                            (g.Installed ? "found: " + g.Exe : "not found on this PC - use Browse exe...");
            gameInfo.ForeColor = g.Installed ? Theme.Muted : Theme.Warn;
            if (pads != null) padsLabel.Text = PadsText(maxRemote) + " this game";

            if (remember)
            {
                cfg.Game = g.Name;
                try { IniFile.SetValue(AppPaths.ConfigFile, "Host", "Game", g.Name); }
                catch (Exception ex) { Log.Warn("Could not save the selected game: " + ex.Message); }
            }
        }

        string PadsText(int canJoin) =>
            $"{pads.Capacity} virtual Xbox 360 controllers ready" +
            (hostControllers > 0 ? $" (+{hostControllers} on this PC)" : "") +
            $"  ·  up to {canJoin} players can join";

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

            // Windows allows 4 Xbox controllers: real ones already on this PC (the host's own) keep their slot
            hostControllers = LocalGamepad.CountConnected();
            int padCount = Math.Min(cfg.MaxPlayers, 4 - hostControllers);
            if (padCount < 1)
            {
                MessageBox.Show(this,
                    "4 Xbox controllers are already connected to this PC, the most Windows allows.\n\n" +
                    "Unplug one so a joined PC can get a controller, then host again.",
                    "LanPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (hostControllers > 0)
                Log.Info($"{hostControllers} Xbox controller(s) connected to this PC keep their slot: {padCount} virtual controllers");

            try
            {
                pads = new VirtualPads(padCount, cfg.PrePlugPads);
            }
            catch (VigemBusNotFoundException)
            {
                MessageBox.Show(this,
                    "The ViGEmBus driver (virtual Xbox controllers) is not installed on this PC.\n\n" +
                    "Install it once (as admin) with install-image.ps1 in the LanPad folder, then try again.",
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
                server = new HostServer(pads, cfg.Port, padCount, cfg.PrePlugPads, cfg.EffectiveHostName, cfg.Game, pin);
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
            padsLabel.Text = PadsText(server.MaxPlayers);
            padsLabel.DotColor = Theme.Good;
            hostTweaks = Tweaks.BeginSession();
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
            hostTweaks?.Dispose();
            hostTweaks = null;
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
            joinedHotkeys = Para(HotkeysText(), 10f, Theme.Text);
            col.Controls.Add(joinedHotkeys);
            awayButton = NewButton("", (s, e) => ToggleAway(), ButtonKind.Primary, 200);
            col.Controls.Add(Row(awayButton, NewButton("Leave game", (s, e) => LeaveGame(null), ButtonKind.Danger)));
            UpdateAwayButton();
        }

        string HotkeysText() =>
            $"{Hk(cfg.PadKey)} = Pad mode on/off (off = keyboard works normally again).   " +
            $"{Hk(cfg.SwitchKey)} = switch to your own PC and back.   " +
            $"{Hk(cfg.LeaveKey)} = leave the game.   " +
            "An Xbox controller plugged into this PC works too (also with Pad mode off).";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint from, uint to, bool attach);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        /// <summary>
        /// Windows does not let a background program take the foreground (the hotkey comes through the hook, not
        /// as a key press to LanPad), so join the foreground window's input queue for a moment first.
        /// </summary>
        void BringToForeground()
        {
            IntPtr fg = GetForegroundWindow();
            uint other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = other != 0 && other != me && AttachThreadInput(other, me, true);
            try
            {
                SetForegroundWindow(Handle);
                Activate();
                BringToFront();
            }
            finally
            {
                if (attached) AttachThreadInput(other, me, false);
            }
        }

        void UpdateAwayButton() => awayButton.Text = away ? "▶  Back to the game" : "Switch to my PC";

        /// <summary>
        /// The switch hotkey (Alt+F12) / button while joined: "on my PC" minimizes the stream, turns Pad mode off
        /// (keyboard and mouse work normally, the host's controller is released) and shows LanPad; again = back to
        /// the game with Pad mode as it was. The player stays joined and keeps the controller.
        /// </summary>
        void ToggleAway()
        {
            if (client == null || hook == null) return;
            away = !away;
            if (away)
            {
                padBeforeAway = hook.PadMode;
                hook.PadMode = false; // also releases every button on the host
                viewer?.SetAway(true);
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                BringToForeground();
                toast.ShowMessage($"Your PC  -  {Hk(cfg.SwitchKey)} = back to the game", Color.LightSkyBlue, 4000);
                Log.Info("Switched to this PC (stream hidden, Pad mode off)");
            }
            else
            {
                viewer?.SetAway(false);
                hook.PadMode = padBeforeAway;
                toast.ShowMessage("Back in the game" + (padBeforeAway ? "  -  PAD MODE ON" : ""), Color.LightGreen, 2500);
                Log.Info("Back to the game");
            }
            UpdateAwayButton();
            UpdatePadModeLabel(false);
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
                hook = new KeyboardHook
                {
                    PadKey = cfg.PadKey, LeaveKey = cfg.LeaveKey, SwitchKey = cfg.SwitchKey,
                    SwallowMouse = cfg.SwallowMouse,
                };
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
            joinedHotkeys.Text = HotkeysText(); // the hotkeys may have changed in Settings
            // LanPad runs the keyboard hook: high priority keeps key presses on time while mpv decodes
            joinTweaks = Tweaks.BeginSession();
            gamepad = new LocalGamepad();
            var g = gamepad;
            // on your own PC (away) the controller is released too, like the keyboard
            client = new JoinClient(host, name, pinBox.Text.Trim(),
                () => away ? PadState.Neutral : LocalGamepad.Merge(KeyMapper.Map(h.IsDown, activeKeyMap), g.State))
            {
                ExtraSends = cfg.InputResend,
            };
            var c = client;
            hook.KeysChanged += c.Signal;
            g.Changed += c.Signal;
            hook.PadModeChanged += on => UpdatePadModeLabel(true);
            hook.LeaveRequested += () => BeginInvoke((Action)(() => LeaveGame(null)));
            hook.SwitchRequested += () => BeginInvoke((Action)ToggleAway);
            // Pad mode key while on your PC = straight back into the game with Pad mode on
            hook.PadModeChanged += on =>
            {
                if (on && away) BeginInvoke((Action)(() => { if (away) { padBeforeAway = true; ToggleAway(); } }));
            };
            client.StatusChanged += s => BeginInvoke((Action)(() => OnJoinStatus(c, s)));
            client.GameChanged += () => BeginInvoke((Action)(() => OnHostGameChanged(c)));

            StopDiscovery();
            joinedTitle.Text = $"Joining {host.Address} ...";
            UpdatePadModeLabel(false);
            ShowPage(joinedPage);
        }

        static string Hk(Keys hotkey) => KeyMap.HotkeyText(hotkey);

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
                        toast.ShowMessage($"Joined as controller {c.Slot}  -  PAD MODE ON ({Hk(cfg.PadKey)})", Color.LightGreen, 4000);
                    }
                    else
                    {
                        toast.ShowMessage($"Joined as controller {c.Slot}  -  press {Hk(cfg.PadKey)} for Pad mode", Color.LightGreen, 4000);
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
            padModeLabel.Text = away ? $"ON YOUR PC   (stream hidden, {Hk(cfg.SwitchKey)} = back to the game)"
                : on ? $"PAD MODE: ON   (keyboard = controller, {Hk(cfg.PadKey)} to turn off)"
                : $"PAD MODE: OFF  (keyboard works normally, {Hk(cfg.PadKey)} to turn on)";
            padModeLabel.Fill = away ? Theme.Blend(Theme.Accent, Theme.Back, 0.3f)
                : on ? Theme.Blend(Theme.Good, Theme.Back, 0.25f) : Theme.Blend(Theme.Bad, Theme.Back, 0.2f);
            if (withToast)
                toast.ShowMessage(on ? "PAD MODE ON" : "PAD MODE OFF - keyboard is normal", on ? Color.LightGreen : Color.Orange);
        }

        void LeaveGame(string message)
        {
            bool wasJoined = client != null;
            away = false;
            if (awayButton != null) UpdateAwayButton();
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
            gamepad?.Dispose();
            gamepad = null;
            joinTweaks?.Dispose();
            joinTweaks = null;
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
