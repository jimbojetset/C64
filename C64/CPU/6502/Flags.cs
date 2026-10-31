

using System.Runtime.CompilerServices;

namespace C64.CPU
{

    public class Flags
    {

        private const byte FLAG_C = 0x01;

        private const byte FLAG_Z = 0x02;
        private const byte FLAG_I = 0x04;
        private const byte FLAG_D = 0x08;

        private const byte FLAG_V = 0x40;
        private const byte FLAG_N = 0x80;

        private byte p;

        public bool C { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_C) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_C) : (byte)(p & ~FLAG_C); }
        public bool Z { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_Z) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_Z) : (byte)(p & ~FLAG_Z); }
        public bool I { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_I) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_I) : (byte)(p & ~FLAG_I); }
        public bool D { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_D) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_D) : (byte)(p & ~FLAG_D); }

        public bool V { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_V) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_V) : (byte)(p & ~FLAG_V); }
        public bool N { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_N) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_N) : (byte)(p & ~FLAG_N); }

        /// <summary>Creates a processor-status register with the unused flag held high.</summary>
        public Flags()
        {
        }

        /// <summary>Restores the 6502 processor flags to their reset defaults.</summary>
        public void Clear()
        {
            p = (byte)(p & 0x20);
        }

        /// <summary>Copies the selected bits from a packed processor-status byte into the individual flags.</summary>
        /// <param name="flags">The packed processor status bits to apply.</param>
        /// <param name="bits">The status bits that are allowed to change.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFlagsFromByte(byte flags, byte bits = 0xFF)
        {
            p = (byte)((p & (byte)~bits) | (flags & bits));
        }

        /// <summary>Returns the processor status flags as a byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetFlagsAsByte() => p;
    }
}