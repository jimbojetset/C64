using System;

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

        public void Load(string filePath, int startAddr, int length)
        {
            Array.Copy(File.ReadAllBytes(filePath), 0, Memory, startAddr, length);
        }
    }
}
