
using System.Runtime.CompilerServices;

namespace C64.CPU
{
    public class Flags
    {
        // Bit layout of the 6502 status register:
        // 7 6 5 4 3 2 1 0
        // N V T B D I Z C   (T = unused-by-CPU "Test" flag in bit 5)
        private const byte FLAG_C = 0x01;
        private const byte FLAG_Z = 0x02;
        private const byte FLAG_I = 0x04;
        private const byte FLAG_D = 0x08;
        private const byte FLAG_B = 0x10;
        private const byte FLAG_T = 0x20;
        private const byte FLAG_V = 0x40;
        private const byte FLAG_N = 0x80;

        private byte p;

        public bool C { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_C) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_C) : (byte)(p & ~FLAG_C); } // Carry
        public bool Z { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_Z) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_Z) : (byte)(p & ~FLAG_Z); } // Zero
        public bool I { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_I) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_I) : (byte)(p & ~FLAG_I); } // Interrupt Disable
        public bool D { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_D) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_D) : (byte)(p & ~FLAG_D); } // Decimal
        public bool B { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_B) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_B) : (byte)(p & ~FLAG_B); } // Break
        public bool V { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_V) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_V) : (byte)(p & ~FLAG_V); } // Overflow
        public bool N { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_N) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_N) : (byte)(p & ~FLAG_N); } // Negative
        public bool T { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (p & FLAG_T) != 0; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => p = value ? (byte)(p | FLAG_T) : (byte)(p & ~FLAG_T); } // Test Flag Not Used By CPU

        public Flags()
        {
        }

        public void Clear()
        {
            p = (byte)(p & FLAG_T);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFlagsFromByte(byte flags, byte bits = 0xFF)
        {
            p = (byte)((p & (byte)~bits) | (flags & bits));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetFlagsAsByte() => p;
    }
}
