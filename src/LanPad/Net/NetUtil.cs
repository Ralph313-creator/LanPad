using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanPad.Net
{
    public static class NetUtil
    {
        const int SIO_UDP_CONNRESET = -1744830452;

        /// <summary>
        /// Windows reports "connection reset" on a UDP socket when an earlier packet got an ICMP
        /// port-unreachable (e.g. the host is not running yet). Turn that off so Receive keeps working.
        /// </summary>
        public static void DisableUdpConnReset(UdpClient udp)
        {
            try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); }
            catch (SocketException) { }
            catch (PlatformNotSupportedException) { }
        }

        /// <summary>IPv4 addresses of all network adapters that are up (no loopback).</summary>
        public static List<(IPAddress Address, IPAddress Mask, bool HasGateway)> LocalIPv4()
        {
            var result = new List<(IPAddress, IPAddress, bool)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                         !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.Address.ToString().StartsWith("169.254.")) continue; // no DHCP lease
                    result.Add((ua.Address, ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), gw));
                }
            }
            return result;
        }

        /// <summary>The LAN address other PCs most likely reach us on (adapter with a gateway first).</summary>
        public static IPAddress PrimaryLanAddress()
        {
            var all = LocalIPv4();
            var best = all.FirstOrDefault(a => a.HasGateway);
            if (best.Address != null) return best.Address;
            return all.Count > 0 ? all[0].Address : IPAddress.Loopback;
        }

        /// <summary>Local address Windows would use to send to <paramref name="remote"/>.</summary>
        public static IPAddress LocalAddressFor(IPAddress remote)
        {
            if (IPAddress.IsLoopback(remote)) return PrimaryLanAddress();
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(remote, 9); // UDP connect sends nothing, it only picks a route
                    return ((IPEndPoint)s.LocalEndPoint).Address;
                }
            }
            catch (SocketException)
            {
                return PrimaryLanAddress();
            }
        }

        /// <summary>Directed broadcast of every adapter, plus 255.255.255.255 and loopback (same-PC testing).</summary>
        public static List<IPAddress> BroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            foreach (var (addr, mask, _) in LocalIPv4())
            {
                byte[] a = addr.GetAddressBytes(), m = mask.GetAddressBytes(), b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                var bc = new IPAddress(b);
                if (!list.Contains(bc)) list.Add(bc);
            }
            return list;
        }

        /// <summary>Each host streams on its own multicast group: 239.255.77.(last number of its IP).</summary>
        public static IPAddress MulticastGroupFor(IPAddress hostAddress)
        {
            byte last = hostAddress.GetAddressBytes()[3];
            return new IPAddress(new byte[] { 239, 255, 77, last });
        }
    }
}
