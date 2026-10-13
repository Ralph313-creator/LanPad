using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanPad.Net;
using LanPad.Protocol;

namespace LanPad.Join
{
    public enum JoinStatus { Joining, Joined, LostHost, Full, BadPin, Kicked, Stopped }

    /// <summary>
    /// Connection from a Join PC to the host. Sends JOIN until welcomed, then sends the
    /// controller state immediately when it changes, plus a heartbeat every 50 ms.
    /// </summary>
    public sealed class JoinClient : IDisposable
    {
        static readonly Random Rng = new Random();

        readonly UdpClient udp;
        readonly IPEndPoint host;
        readonly string playerName;
        readonly string pin;
        readonly Func<PadState> readState;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread sendThread;
        readonly Thread recvThread;
        volatile bool running = true;
        uint seq;
        DateTime lastFromHost = DateTime.UtcNow;
        volatile JoinStatus status = JoinStatus.Joining;

        public uint ClientId { get; }
        public IPEndPoint Host => host;
        public int Slot { get; private set; }
        public string StreamGroup { get; private set; } = "";
        public int StreamPort { get; private set; }
        public JoinStatus Status => status;

        /// <summary>Raised on a background thread when the status changes.</summary>
        public event Action<JoinStatus> StatusChanged;

        byte[] lastKeyMapBytes = new byte[0];

        /// <summary>Game the host picked, and the controls it sent (null until the first Welcome).</summary>
        public string HostGameName { get; private set; } = "";
        public KeyMap HostKeyMap { get; private set; }

        /// <summary>Raised on a background thread when the host's game or controls change.</summary>
        public event Action GameChanged;

        void UpdateGame(Packet p)
        {
            bool mapChanged = p.KeyMap != null && p.KeyMap.Length > 0 && !p.KeyMap.SequenceEqual(lastKeyMapBytes);
            bool nameChanged = (p.GameName ?? "") != HostGameName;
            if (!mapChanged && !nameChanged) return;
            if (mapChanged)
            {
                var map = KeyMap.FromBytes(p.KeyMap);
                if (map == null)
                {
                    Log.Warn("Host sent controls this LanPad version cannot read; keeping the current ones");
                }
                else
                {
                    HostKeyMap = map;
                    lastKeyMapBytes = p.KeyMap;
                }
            }
            HostGameName = p.GameName ?? "";
            Log.Info($"Host game: {HostGameName}");
            GameChanged?.Invoke();
        }

        public JoinClient(IPEndPoint host, string playerName, string pin, Func<PadState> readState, IPAddress bindAddress = null)
        {
            this.host = host;
            this.playerName = playerName ?? "";
            this.pin = pin ?? "";
            this.readState = readState;
            lock (Rng) ClientId = (uint)Rng.Next(1, int.MaxValue) ^ ((uint)Environment.TickCount << 1);

            udp = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, 0));
            NetUtil.DisableUdpConnReset(udp);
            recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "LanPad join receive" };
            sendThread = new Thread(SendLoop) { IsBackground = true, Name = "LanPad join send", Priority = ThreadPriority.AboveNormal };
            recvThread.Start();
            sendThread.Start();
        }

        /// <summary>Call when keys changed, so the new state is sent right away.</summary>
        public void Signal() => wake.Set();

        /// <summary>Extra copies of each change, <see cref="ResendMs"/> apart, so one lost UDP packet costs 10 ms, not a 50 ms heartbeat.</summary>
        public int ExtraSends { get; set; }

        const int HeartbeatMs = 50, ResendMs = 10;

        void SetStatus(JoinStatus s)
        {
            if (status == s) return;
            status = s;
            Log.Info($"Join status: {s}");
            StatusChanged?.Invoke(s);
        }

        void SendLoop()
        {
            using (Tweaks.GameThread())
                SendLoopBody();
        }

        void SendLoopBody()
        {
            int resendLeft = 0;
            while (running)
            {
                var st = status;
                if (st == JoinStatus.Full || st == JoinStatus.BadPin || st == JoinStatus.Kicked || st == JoinStatus.Stopped)
                {
                    wake.WaitOne(200);
                    continue;
                }

                if (st == JoinStatus.Joined && DateTime.UtcNow - lastFromHost > TimeSpan.FromSeconds(3))
                {
                    SetStatus(JoinStatus.LostHost);
                    st = JoinStatus.LostHost;
                }

                if (st == JoinStatus.Joining || st == JoinStatus.LostHost)
                {
                    Send(new Packet(PacketType.Join, ClientId) { PlayerName = playerName, Pin = pin });
                    wake.WaitOne(500);
                    continue;
                }

                // Joined: wait for a key change, the next extra copy (10 ms) or the heartbeat (50 ms),
                // then send the current state. Every send has a new Seq; the host just applies the latest.
                bool changed = wake.WaitOne(resendLeft > 0 ? ResendMs : HeartbeatMs);
                if (!running) break;
                if (changed) resendLeft = ExtraSends;
                else if (resendLeft > 0) resendLeft--;
                var state = readState();
                Send(new Packet(PacketType.State, ClientId) { Seq = ++seq, Pad = state });
            }
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
                if (!Packet.TryParse(data, data.Length, out var p) || p.ClientId != ClientId) continue;

                switch (p.Type)
                {
                    case PacketType.Welcome:
                    case PacketType.Alive:
                        lastFromHost = DateTime.UtcNow;
                        Slot = p.Slot;
                        if (!string.IsNullOrEmpty(p.StreamGroup)) StreamGroup = p.StreamGroup;
                        StreamPort = p.StreamPort;
                        UpdateGame(p);
                        if (status == JoinStatus.Joining || status == JoinStatus.LostHost)
                        {
                            SetStatus(JoinStatus.Joined);
                            wake.Set(); // send the current state now
                        }
                        break;
                    case PacketType.NotJoined:
                        // Host restarted and forgot us: join again automatically.
                        lastFromHost = DateTime.UtcNow;
                        if (status == JoinStatus.Joined) SetStatus(JoinStatus.Joining);
                        wake.Set();
                        break;
                    case PacketType.Full:
                        SetStatus(JoinStatus.Full);
                        break;
                    case PacketType.BadPin:
                        SetStatus(JoinStatus.BadPin);
                        break;
                    case PacketType.Kick:
                        SetStatus(JoinStatus.Kicked);
                        break;
                }
            }
        }

        void Send(Packet p)
        {
            try
            {
                byte[] b = p.ToBytes();
                udp.Send(b, b.Length, host);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (!running) return;
            if (status == JoinStatus.Joined)
                for (int i = 0; i < 3; i++) Send(new Packet(PacketType.Leave, ClientId));
            running = false;
            SetStatus(JoinStatus.Stopped);
            wake.Set();
            udp.Close();
            sendThread.Join(500);
            recvThread.Join(500);
        }
    }
}
