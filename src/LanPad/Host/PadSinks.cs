using System;
using System.Collections.Generic;
using LanPad.Protocol;

namespace LanPad.Host
{
    /// <summary>Where remote controller states go. Slots are 1-based.</summary>
    public interface IPadSink : IDisposable
    {
        int Capacity { get; }
        void Plug(int slot);
        void Update(int slot, PadState state);
        void Unplug(int slot);
    }

    /// <summary>In-memory pads for --selftest (no driver needed).</summary>
    public sealed class FakePads : IPadSink
    {
        readonly object gate = new object();
        readonly PadState[] states;
        readonly bool[] plugged;

        public FakePads(int capacity)
        {
            states = new PadState[capacity];
            plugged = new bool[capacity];
        }

        public int Capacity => states.Length;
        public List<string> Events { get; } = new List<string>();

        public void Plug(int slot)
        {
            lock (gate) { plugged[slot - 1] = true; Events.Add($"plug {slot}"); }
        }

        public void Update(int slot, PadState state)
        {
            lock (gate) { states[slot - 1] = state; }
        }

        public void Unplug(int slot)
        {
            lock (gate) { plugged[slot - 1] = false; states[slot - 1] = PadState.Neutral; Events.Add($"unplug {slot}"); }
        }

        public PadState Get(int slot) { lock (gate) return states[slot - 1]; }
        public bool IsPlugged(int slot) { lock (gate) return plugged[slot - 1]; }

        public void Dispose() { }
    }
}
