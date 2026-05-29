
namespace _6507CPU
{
    public class Flags
    {
        private static bool c = false;
        public bool C { get { return c; } set { c = value; } } // Carry
        private static bool z = false;
        public bool Z { get { return z; } set { z = value; } } // Zero
        private static bool i = false;
        public bool I { get { return i; } set { i = value; } } // Interrupt Disable
        private static bool d = false;
        public bool D { get { return d; } set { d = value; } } // Decimal
        private static bool b = false;
        public bool B { get { return b; } set { b = value; } } // Break
        private static bool v = false;
        public bool V { get { return v; } set { v = value; } } // Overflow
        private static bool n = false;
        public bool N { get { return n; } set { n = value; } } // Negative
        private static bool t = false;
        public bool T { get { return t; } set { t = value; } } // Test Flag Not Used By CPU

        public Flags()
        {

        }

        public void Clear()
        {
            C = Z = I = D = B = V = N = false;
        }

        public void SetFlagsFromByte(byte flags, byte bits = 0b11111111)
        {
            // 7 6 5 4 3 2 1 0
            // N V   B D I Z C
            if ((bits & 0b00000001) == 0x01) c = ((flags & 0x01) == 0x01);
            if ((bits & 0b00000010) == 0x02) z = ((flags & 0x02) == 0x02);
            if ((bits & 0b00000100) == 0x04) i = ((flags & 0x04) == 0x04);
            if ((bits & 0b00001000) == 0x08) d = ((flags & 0x08) == 0x08);
            if ((bits & 0b00010000) == 0x10) b = ((flags & 0x10) == 0x10);
            if ((bits & 0b00100000) == 0x20) t = ((flags & 0x20) == 0x20);
            if ((bits & 0b01000000) == 0x40) v = ((flags & 0x40) == 0x40);
            if ((bits & 0b10000000) == 0x80) n = ((flags & 0x80) == 0x80);
        }

        public byte GetFlagsAsByte()
        {
            byte value = new byte();
            //                NV BDIZC
            if (c) value += 0b00000001;
            if (z) value += 0b00000010;
            if (i) value += 0b00000100;
            if (d) value += 0b00001000;
            if (b) value += 0b00010000;
            if (t) value += 0b00100000;
            if (v) value += 0b01000000;
            if (n) value += 0b10000000;
            return value;
        }
    }
}
