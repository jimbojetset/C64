using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _6502CPU
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

        public void SetFlagsFromByte(byte flags)
        {
            // 7 6 5 4 3 2 1 0
            // N V   B D I Z C
            c = ((flags & 0x1) == 0x01);
            z = ((flags & 0x2) == 0x02);
            i = ((flags & 0x4) == 0x04);
            d = ((flags & 0x8) == 0x08);
            b = ((flags & 0x10) == 0x10);
            t = ((flags & 0x20) == 0x20);
            v = ((flags & 0x40) == 0x40);
            n = ((flags & 0x80) == 0x80);
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
