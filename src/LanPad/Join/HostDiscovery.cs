using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanPad.Net;
using LanPad.Protocol;

namespace LanPad.Join
{
    /// <summary>Finds hosts on the LAN by broadcasting DISCOVER once per second.</summary>
    public sealed class HostDiscovery : IDisposable
    {
        public sealed class HostInfo
        {
            public IPEndPoint EndPoint;
            public string HostName;
            public string GameName;
            public int SlotsFree;
            public int SlotsTotal;
            public bool PinRequired;
            public DateTime LastSeen;
        }

        readonly UdpClient udp;
        readonly int port;
        readonly Thread recvThread;
        readonly Timer pinger;
        readonly object gate = new object();
        readonly Dictionary<string, HostInfo> hosts = new Dictionary<string, HostInfo>();
        volatile bool running = true;

        /// <summary>Raised on a background thread when the host list changes.</summary>
        public event Action ListChanged;

        public HostDiscovery(int port, IPAddress bindAddress = null)
        {
            this.port = port;
            udp = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, 0)) { EnableBroadcast = true };
            NetUtil.DisableUdpConnReset(udp);
            recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "LanPad discovery" };
            recvThread.Start();
            pinger = new Timer(_ => Ping(), null, 0, 1000);
        }

        public List<HostInfo> Snapshot()
        {
            lock (gate) return hosts.Values.OrderBy(h => h.HostName).ToList();
        }

        public void Refresh()
        {
            lock (gate) hosts.Clear();
            ListChanged?.Invoke();
            Ping();
        }

        void Ping()
        {
            if (!running) return;
            byte[] b = new Packet(PacketType.Discover).ToBytes();
            foreach (var target in NetUtil.BroadcastTargets())
            {
                try { udp.Send(b, b.Length, new IPEndPoint(target, port)); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }

            bool removed = false;
            lock (gate)
            {
                foreach (var key in hosts.Where(kv => DateTime.UtcNow - kv.Value.LastSeen > TimeSpan.FromSeconds(4))
                                         .Select(kv => kv.Key).ToList())
                {
                    hosts.Remove(key);
                    removed = true;
                }
            }
            if (removed) ListChanged?.Invoke();
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
                if (!Packet.TryParse(data, data.Length, out var p) || p.Type != PacketType.Offer) continue;

                // A host on this same PC answers on loopback and on its LAN address; keep one entry.
                var address = IPAddress.IsLoopback(ep.Address) ? NetUtil.PrimaryLanAddress() : ep.Address;
                var hostEp = new IPEndPoint(address, ep.Port);
                bool changed;
                lock (gate)
                {
                    string key = hostEp.ToString();
                    changed = !hosts.TryGetValue(key, out var old) || old.SlotsFree != p.SlotsFree ||
                              old.SlotsTotal != p.SlotsTotal || old.GameName != p.GameName;
                    hosts[key] = new HostInfo
                    {
                        EndPoint = hostEp,
                        HostName = p.HostName,
                        GameName = p.GameName,
                        SlotsFree = p.SlotsFree,
                        SlotsTotal = p.SlotsTotal,
                        PinRequired = p.PinRequired,
                        LastSeen = DateTime.UtcNow,
                    };
                }
                if (changed) ListChanged?.Invoke();
            }
        }

        public void Dispose()
        {
            running = false;
            pinger.Dispose();
            udp.Close();
            recvThread.Join(500);
        }
    }
}
