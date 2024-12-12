using System;

namespace _6502CPU
{
    public class RAM
    {
        private byte[] Memory { get; set; }

        public RAM(int size)
        {
            Memory = new byte[size];
            bool flipflop = true;
            for (int x = 0; x < size; x++)
            {
                if(x%64== 0) { flipflop = !flipflop; }
                if(flipflop) 
                    Memory[x] = 0xFF;
                else Memory[x] = 0x00;
            }
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

        public void Load(string filePath, int startAddr, int length)
        {
            Array.Copy(File.ReadAllBytes(filePath), 0, Memory, startAddr, length);
        }
    }
}
