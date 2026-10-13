using System;

namespace LanPad.Protocol
{
    /// <summary>Xbox 360 button bits, same values as XInput's XINPUT_GAMEPAD wButtons.</summary>
    [Flags]
    public enum PadButtons : ushort
    {
        None = 0,
        DPadUp = 0x0001,
        DPadDown = 0x0002,
        DPadLeft = 0x0004,
        DPadRight = 0x0008,
        Start = 0x0010,
        Back = 0x0020,
        LeftThumb = 0x0040,
        RightThumb = 0x0080,
        LeftShoulder = 0x0100,
        RightShoulder = 0x0200,
        Guide = 0x0400,
        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000,
    }

    /// <summary>Full state of one virtual Xbox 360 controller.</summary>
    public struct PadState : IEquatable<PadState>
    {
        public PadButtons Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;

        public static readonly PadState Neutral = default(PadState);

        public bool IsNeutral => Equals(Neutral);

        public bool Equals(PadState o) =>
            Buttons == o.Buttons && LeftTrigger == o.LeftTrigger && RightTrigger == o.RightTrigger &&
            LeftX == o.LeftX && LeftY == o.LeftY && RightX == o.RightX && RightY == o.RightY;

        public override bool Equals(object obj) => obj is PadState p && Equals(p);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Buttons;
                h = h * 31 + LeftTrigger;
                h = h * 31 + RightTrigger;
                h = h * 31 + LeftX;
                h = h * 31 + LeftY;
                h = h * 31 + RightX;
                h = h * 31 + RightY;
                return h;
            }
        }

        public static bool operator ==(PadState a, PadState b) => a.Equals(b);
        public static bool operator !=(PadState a, PadState b) => !a.Equals(b);

        public override string ToString() =>
            $"buttons={(ushort)Buttons:X4} LT={LeftTrigger} RT={RightTrigger} L=({LeftX},{LeftY}) R=({RightX},{RightY})";
    }
}
