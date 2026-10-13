using System;
using LanPad.Protocol;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace LanPad.Host
{
    /// <summary>
    /// Virtual Xbox 360 controllers through the ViGEmBus driver. Games see them as real XInput pads.
    /// Throws Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException when the driver is not installed.
    /// </summary>
    public sealed class VirtualPads : IPadSink
    {
        readonly ViGEmClient client;
        readonly IXbox360Controller[] pads;
        readonly bool[] plugged;
        readonly object[] locks;

        public VirtualPads(int count, bool prePlug)
        {
            client = new ViGEmClient();
            pads = new IXbox360Controller[count];
            plugged = new bool[count];
            locks = new object[count];
            for (int i = 0; i < count; i++)
            {
                locks[i] = new object();
                pads[i] = client.CreateXbox360Controller();
                pads[i].AutoSubmitReport = false;
            }
            if (prePlug)
                for (int slot = 1; slot <= count; slot++) Plug(slot);
        }

        public int Capacity => pads.Length;

        public void Plug(int slot)
        {
            int i = slot - 1;
            lock (locks[i])
            {
                if (plugged[i]) return;
                pads[i].Connect();
                plugged[i] = true;
                Write(i, PadState.Neutral);
            }
            Log.Info($"Virtual controller {slot} plugged in");
        }

        public void Update(int slot, PadState s)
        {
            int i = slot - 1;
            lock (locks[i])
            {
                if (!plugged[i]) return;
                Write(i, s);
            }
        }

        void Write(int i, PadState s)
        {
            var p = pads[i];
            p.SetButtonsFull((ushort)s.Buttons);
            p.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
            p.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
            p.SetAxisValue(Xbox360Axis.LeftThumbX, s.LeftX);
            p.SetAxisValue(Xbox360Axis.LeftThumbY, s.LeftY);
            p.SetAxisValue(Xbox360Axis.RightThumbX, s.RightX);
            p.SetAxisValue(Xbox360Axis.RightThumbY, s.RightY);
            p.SubmitReport();
        }

        public void Unplug(int slot)
        {
            int i = slot - 1;
            lock (locks[i])
            {
                if (!plugged[i]) return;
                try { Write(i, PadState.Neutral); } catch (Exception) { }
                pads[i].Disconnect();
                plugged[i] = false;
            }
            Log.Info($"Virtual controller {slot} unplugged");
        }

        public void Dispose()
        {
            for (int slot = 1; slot <= pads.Length; slot++)
            {
                try { Unplug(slot); } catch (Exception ex) { Log.Warn($"Unplug {slot} failed: {ex.Message}"); }
            }
            client.Dispose();
        }
    }
}
