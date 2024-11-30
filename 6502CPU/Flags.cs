using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _6502CPU
{
    public class Flags
    {
        public bool C { get; set; }
        public bool Z { get; set; }
        public bool I { get; set; }
        public bool D { get; set; }
        public bool B { get; set; }
        public bool V { get; set; }
        public bool N { get; set; }

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
            C = ((flags & 0x1) == 0x01);
            Z = ((flags & 0x2) == 0x02);
            I = ((flags & 0x4) == 0x04);
            D = ((flags & 0x8) == 0x08);
            B = ((flags & 0x10) == 0x10);
            V = ((flags & 0x40) == 0x40);
            N = ((flags & 0x80) == 0x80);
        }

        public byte GetFlagsAsByte()
        {
            byte value = new byte();
            //                NV BDIZC
            if (C) value += 0b00000001;
            if (Z) value += 0b00000010;
            if (I) value += 0b00000100;
            if (D) value += 0b00001000;
            if (B) value += 0b00010000;
            if (V) value += 0b01000000;
            if (N) value += 0b10000000;
            return value;
        }
    }
}
