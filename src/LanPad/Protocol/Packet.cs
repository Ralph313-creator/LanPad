using System;
using System.IO;
using System.Text;

namespace LanPad.Protocol
{
    public enum PacketType : byte
    {
        Discover = 1,   // join -> broadcast: who is hosting?
        Offer = 2,      // host -> join: I am hosting (name, game, free slots)
        Join = 3,       // join -> host: let me in (name, pin)
        Welcome = 4,    // host -> join: you got slot N, stream is at group:port
        Full = 5,       // host -> join: no free controller
        BadPin = 6,     // host -> join: wrong room PIN
        State = 7,      // join -> host: controller state (on change + heartbeat)
        Leave = 8,      // join -> host: I am leaving
        Kick = 9,       // host -> join: you were removed by the host
        Alive = 10,     // host -> join: heartbeat (1 per second)
        NotJoined = 11, // host -> join: I don't know you (host restarted), please join again
    }

    /// <summary>
    /// One LanPad UDP packet. Layout (little-endian):
    /// magic u32 "LPAD" | version u8 | type u8 | clientId u32 | payload (depends on type).
    /// </summary>
    public sealed class Packet
    {
        public const uint Magic = 0x4441504C; // bytes on the wire: 'L' 'P' 'A' 'D'
        public const byte Version = 2;        // 2: Welcome/Alive carry the game name and its key map
        public const int MaxStringBytes = 64;

        public PacketType Type;
        public uint ClientId;

        // Offer
        public string HostName = "";
        public string GameName = "";
        public byte SlotsFree;
        public byte SlotsTotal;
        public bool PinRequired;

        // Join
        public string PlayerName = "";
        public string Pin = "";

        // Offer / Welcome / Alive
        public byte Slot;
        public string StreamGroup = "";
        public ushort StreamPort;

        // Welcome / Alive: the host's current game (GameName) and its controls (KeyMap.ToBytes())
        public byte[] KeyMap = new byte[0];

        // State
        public uint Seq;
        public PadState Pad;

        public Packet() { }

        public Packet(PacketType type, uint clientId = 0)
        {
            Type = type;
            ClientId = clientId;
        }

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream(64))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write((byte)Type);
                w.Write(ClientId);
                switch (Type)
                {
                    case PacketType.Offer:
                        WriteString(w, HostName);
                        WriteString(w, GameName);
                        w.Write(SlotsFree);
                        w.Write(SlotsTotal);
                        w.Write(PinRequired);
                        WriteString(w, StreamGroup);
                        w.Write(StreamPort);
                        break;
                    case PacketType.Join:
                        WriteString(w, PlayerName);
                        WriteString(w, Pin);
                        break;
                    case PacketType.Welcome:
                    case PacketType.Alive:
                        w.Write(Slot);
                        WriteString(w, StreamGroup);
                        w.Write(StreamPort);
                        WriteString(w, GameName);
                        w.Write((ushort)(KeyMap?.Length ?? 0));
                        if (KeyMap != null) w.Write(KeyMap);
                        break;
                    case PacketType.State:
                        w.Write(Seq);
                        w.Write((ushort)Pad.Buttons);
                        w.Write(Pad.LeftTrigger);
                        w.Write(Pad.RightTrigger);
                        w.Write(Pad.LeftX);
                        w.Write(Pad.LeftY);
                        w.Write(Pad.RightX);
                        w.Write(Pad.RightY);
                        break;
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static bool TryParse(byte[] data, int length, out Packet packet)
        {
            packet = null;
            if (data == null || length < 10) return false;
            try
            {
                using (var ms = new MemoryStream(data, 0, length, false))
                using (var r = new BinaryReader(ms))
                {
                    if (r.ReadUInt32() != Magic) return false;
                    if (r.ReadByte() != Version) return false;
                    var p = new Packet { Type = (PacketType)r.ReadByte(), ClientId = r.ReadUInt32() };
                    switch (p.Type)
                    {
                        case PacketType.Offer:
                            p.HostName = ReadString(r);
                            p.GameName = ReadString(r);
                            p.SlotsFree = r.ReadByte();
                            p.SlotsTotal = r.ReadByte();
                            p.PinRequired = r.ReadBoolean();
                            p.StreamGroup = ReadString(r);
                            p.StreamPort = r.ReadUInt16();
                            break;
                        case PacketType.Join:
                            p.PlayerName = ReadString(r);
                            p.Pin = ReadString(r);
                            break;
                        case PacketType.Welcome:
                        case PacketType.Alive:
                            p.Slot = r.ReadByte();
                            p.StreamGroup = ReadString(r);
                            p.StreamPort = r.ReadUInt16();
                            p.GameName = ReadString(r);
                            int mapLength = r.ReadUInt16();
                            p.KeyMap = r.ReadBytes(mapLength);
                            if (p.KeyMap.Length != mapLength) throw new EndOfStreamException();
                            break;
                        case PacketType.State:
                            p.Seq = r.ReadUInt32();
                            p.Pad.Buttons = (PadButtons)r.ReadUInt16();
                            p.Pad.LeftTrigger = r.ReadByte();
                            p.Pad.RightTrigger = r.ReadByte();
                            p.Pad.LeftX = r.ReadInt16();
                            p.Pad.LeftY = r.ReadInt16();
                            p.Pad.RightX = r.ReadInt16();
                            p.Pad.RightY = r.ReadInt16();
                            break;
                        case PacketType.Discover:
                        case PacketType.Full:
                        case PacketType.BadPin:
                        case PacketType.Leave:
                        case PacketType.Kick:
                        case PacketType.NotJoined:
                            break;
                        default:
                            return false;
                    }
                    packet = p;
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        static void WriteString(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(b.Length, MaxStringBytes);
            w.Write((byte)n);
            w.Write(b, 0, n);
        }

        static string ReadString(BinaryReader r)
        {
            int n = r.ReadByte();
            byte[] b = r.ReadBytes(n);
            if (b.Length != n) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(b);
        }
    }
}
