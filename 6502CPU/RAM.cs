using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _6502CPU
{
    public class RAM
    {
        private byte[] Memory { get; set; }

        public RAM(int size)
        {
            Memory = new byte[size];
        }

        public void WriteByte(ulong addr, byte value)
        {
            Memory[addr] = value;
        }

        public byte ReadByte(ulong addr)
        {
            return Memory[addr];
        }

        public ulong ReadWord(ulong addr)
        {
            byte value1 = Memory[addr];
            byte value2 = Memory[addr + 1];
            ulong value3 = (ulong)((value2 << 8) | value1);
            return value3;
        }


    }
}
