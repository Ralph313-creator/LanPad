using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanPad.Net;
using LanPad.Protocol;

namespace LanPad.Host
{
    /// <summary>
    /// UDP server on the host PC. Answers discovery, gives each joining PC a controller slot,
    /// and feeds the received controller states into the virtual pads.
    /// </summary>
    public sealed class HostServer : IDisposable
    {
        public sealed class Player
        {
            public uint ClientId;
            public int Slot;
            public string Name;
            public IPEndPoint EndPoint;
            public DateTime JoinedAt;
            public DateTime LastSeen;
            public uint LastSeq;
            public bool HasSeq;
            public bool Idle;

            public Player Clone() => (Player)MemberwiseClone();
        }

        /// <summary>No packets for this long: release all buttons (no stuck sprint).</summary>
        public static TimeSpan IdleAfter = TimeSpan.FromSeconds(1);

        /// <summary>No packets for this long: remove the player and free the slot.</summary>
        public static TimeSpan DropAfter = TimeSpan.FromSeconds(10);

        readonly IPadSink pads;
        readonly UdpClient udp;
        readonly object gate = new object();
        readonly Dictionary<uint, Player> players = new Dictionary<uint, Player>();
        volatile int maxPlayers;
        volatile string gameName;
        volatile byte[] keyMapBytes = new byte[0];
        readonly bool prePlugged;
        readonly Thread recvThread;
        readonly Timer housekeeping;
        volatile bool running = true;
        DateTime lastAlive = DateTime.MinValue;

        public string HostName { get; }
        public string GameName => gameName;
        /// <summary>Empty = no PIN needed.</summary>
        public string RoomPin { get; }
        public int Port { get; }
        public string StreamGroup { get; set; } = "";
        public int StreamPort { get; set; }

        /// <summary>Raised (on a background thread) when a player joins or leaves.</summary>
        public event Action PlayersChanged;

        public HostServer(IPadSink pads, int port, int maxPlayers, bool prePlugged,
                          string hostName, string gameName, string roomPin, IPAddress bindAddress = null)
        {
            this.pads = pads;
            this.maxPlayers = Math.Max(1, Math.Min(maxPlayers, pads.Capacity));
            this.prePlugged = prePlugged;
            HostName = hostName;
            this.gameName = gameName ?? "";
            RoomPin = roomPin ?? "";
            Port = port;

            udp = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, port)); // SocketException if the port is in use
            NetUtil.DisableUdpConnReset(udp);
            recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "LanPad host receive" };
            recvThread.Start();
            housekeeping = new Timer(_ => Housekeeping(), null, 250, 250);
            Log.Info($"Host listening on UDP {port}, max {this.maxPlayers} remote players, PIN {(RoomPin.Length > 0 ? "on" : "off")}");
        }

        public int MaxPlayers => maxPlayers;

        /// <summary>
        /// Switches the game: its name, the controls every joined PC should use, and how many PCs may join.
        /// Players already joined stay; joined PCs get the new controls within a moment.
        /// </summary>
        public void SetGame(string name, byte[] keyMap, int maxRemotePlayers)
        {
            gameName = name ?? "";
            keyMapBytes = keyMap ?? new byte[0];
            maxPlayers = Math.Max(1, Math.Min(maxRemotePlayers, pads.Capacity));
            Log.Info($"Game set to {gameName} (up to {maxPlayers} joined players)");
            foreach (var pl in Snapshot()) Send(MakeWelcome(PacketType.Alive, pl), pl.EndPoint);
        }

        public int PlayerCount
        {
            get { lock (gate) return players.Count; }
        }

        public List<Player> Snapshot()
        {
            lock (gate) return players.Values.OrderBy(p => p.Slot).Select(p => p.Clone()).ToList();
        }

        void RecvLoop()
        {
            while (running)
            {
                IPEndPoint ep = null;
                byte[] data;
                try
                {
                    data = udp.Receive(ref ep);
                }
                catch (SocketException)
                {
                    if (!running) break;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                if (!Packet.TryParse(data, data.Length, out var p)) continue;
                try
                {
                    Handle(p, ep);
                }
                catch (Exception ex)
                {
                    Log.Error($"Host: error handling {p.Type} from {ep}", ex);
                }
            }
        }

        void Handle(Packet p, IPEndPoint ep)
        {
            switch (p.Type)
            {
                case PacketType.Discover:
                    Send(MakeOffer(), ep);
                    break;
                case PacketType.Join:
                    HandleJoin(p, ep);
                    break;
                case PacketType.State:
                    HandleState(p, ep);
                    break;
                case PacketType.Leave:
                    Remove(p.ClientId, "left the game", false);
                    break;
            }
        }

        Packet MakeOffer()
        {
            int count = PlayerCount;
            return new Packet(PacketType.Offer)
            {
                HostName = HostName,
                GameName = GameName,
                SlotsTotal = (byte)maxPlayers,
                SlotsFree = (byte)Math.Max(0, maxPlayers - count),
                PinRequired = RoomPin.Length > 0,
                StreamGroup = StreamGroup,
                StreamPort = (ushort)StreamPort,
            };
        }

        Packet MakeWelcome(PacketType type, Player player) => new Packet(type, player.ClientId)
        {
            Slot = (byte)player.Slot,
            StreamGroup = StreamGroup,
            StreamPort = (ushort)StreamPort,
            GameName = gameName,
            KeyMap = keyMapBytes,
        };

        void HandleJoin(Packet p, IPEndPoint ep)
        {
            if (RoomPin.Length > 0 && p.Pin != RoomPin)
            {
                Send(new Packet(PacketType.BadPin, p.ClientId), ep);
                Log.Warn($"Join from {ep} ({p.PlayerName}) refused: wrong PIN");
                return;
            }

            Player player;
            bool added = false;
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                if (players.TryGetValue(p.ClientId, out player))
                {
                    player.EndPoint = ep;
                    player.LastSeen = now;
                }
                else
                {
                    int slot = FreeSlot();
                    if (slot == 0)
                    {
                        player = null;
                    }
                    else
                    {
                        player = new Player
                        {
                            ClientId = p.ClientId,
                            Slot = slot,
                            Name = CleanName(p.PlayerName, ep),
                            EndPoint = ep,
                            JoinedAt = now,
                            LastSeen = now,
                        };
                        players[p.ClientId] = player;
                        added = true;
                    }
                }
            }

            if (player == null)
            {
                Send(new Packet(PacketType.Full, p.ClientId), ep);
                Log.Warn($"Join from {ep} ({p.PlayerName}) refused: room full");
                return;
            }

            if (added)
            {
                if (prePlugged) pads.Update(player.Slot, PadState.Neutral);
                else pads.Plug(player.Slot);
                Log.Info($"{player.Name} ({ep.Address}) joined as controller {player.Slot}");
            }
            Send(MakeWelcome(PacketType.Welcome, player), ep);
            if (added) PlayersChanged?.Invoke();
        }

        void HandleState(Packet p, IPEndPoint ep)
        {
            int slot = 0;
            lock (gate)
            {
                if (players.TryGetValue(p.ClientId, out var player))
                {
                    // Ignore late or duplicated packets (UDP can reorder). Signed compare handles wrap-around.
                    if (player.HasSeq && (int)(p.Seq - player.LastSeq) <= 0) return;
                    player.LastSeq = p.Seq;
                    player.HasSeq = true;
                    player.LastSeen = DateTime.UtcNow;
                    player.EndPoint = ep;
                    player.Idle = false;
                    slot = player.Slot;
                }
            }
            if (slot == 0)
            {
                Send(new Packet(PacketType.NotJoined, p.ClientId), ep);
                return;
            }
            pads.Update(slot, p.Pad);
        }

        int FreeSlot()
        {
            for (int s = 1; s <= maxPlayers; s++)
                if (!players.Values.Any(pl => pl.Slot == s)) return s;
            return 0;
        }

        void Housekeeping()
        {
            if (!running) return;
            DateTime now = DateTime.UtcNow;
            var idle = new List<int>();
            var dropped = new List<Player>();
            var alive = new List<Player>();
            bool sendAlive = now - lastAlive >= TimeSpan.FromSeconds(1);

            lock (gate)
            {
                foreach (var pl in players.Values)
                {
                    TimeSpan silent = now - pl.LastSeen;
                    if (silent > DropAfter) dropped.Add(pl);
                    else
                    {
                        if (silent > IdleAfter && !pl.Idle)
                        {
                            pl.Idle = true;
                            idle.Add(pl.Slot);
                        }
                        if (sendAlive) alive.Add(pl.Clone());
                    }
                }
                foreach (var d in dropped) players.Remove(d.ClientId);
            }
            if (sendAlive) lastAlive = now;

            foreach (int slot in idle)
            {
                pads.Update(slot, PadState.Neutral);
                Log.Info($"Controller {slot}: no input from player for {IdleAfter.TotalSeconds:0}s, buttons released");
            }
            foreach (var d in dropped)
            {
                ReleaseSlot(d.Slot);
                Log.Info($"{d.Name} timed out (no packets for {DropAfter.TotalSeconds:0}s), controller {d.Slot} is free");
            }
            foreach (var a in alive) Send(MakeWelcome(PacketType.Alive, a), a.EndPoint);
            if (dropped.Count > 0) PlayersChanged?.Invoke();
        }

        void ReleaseSlot(int slot)
        {
            if (prePlugged) pads.Update(slot, PadState.Neutral);
            else pads.Unplug(slot);
        }

        /// <summary>Removes a player. If <paramref name="tellClient"/> the client gets a Kick message.</summary>
        public void Remove(uint clientId, string reason, bool tellClient)
        {
            Player pl;
            lock (gate)
            {
                if (!players.TryGetValue(clientId, out pl)) return;
                players.Remove(clientId);
            }
            if (tellClient)
                for (int i = 0; i < 3; i++) Send(new Packet(PacketType.Kick, clientId), pl.EndPoint);
            ReleaseSlot(pl.Slot);
            Log.Info($"{pl.Name} {reason}, controller {pl.Slot} is free");
            PlayersChanged?.Invoke();
        }

        public void Kick(uint clientId) => Remove(clientId, "was kicked by the host", true);

        void Send(Packet p, IPEndPoint ep)
        {
            if (!running || ep == null) return;
            try
            {
                byte[] b = p.ToBytes();
                udp.Send(b, b.Length, ep);
            }
            catch (SocketException ex)
            {
                Log.Warn($"Host: send {p.Type} to {ep} failed: {ex.Message}");
            }
            catch (ObjectDisposedException) { }
        }

        static string CleanName(string name, IPEndPoint ep)
        {
            string n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (n.Length == 0) n = ep.Address.ToString();
            return n.Length > 24 ? n.Substring(0, 24) : n;
        }

        public void Dispose()
        {
            if (!running) return;
            // Tell everyone the room is closed.
            foreach (var pl in Snapshot())
                for (int i = 0; i < 2; i++) Send(new Packet(PacketType.Kick, pl.ClientId), pl.EndPoint);
            running = false;
            housekeeping.Dispose();
            udp.Close();
            recvThread.Join(500);
            foreach (var pl in Snapshot()) ReleaseSlot(pl.Slot);
            lock (gate) players.Clear();
            Log.Info("Host stopped");
        }
    }
}
