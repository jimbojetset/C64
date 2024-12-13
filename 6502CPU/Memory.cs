using System;

namespace _6502CPU
{
    public class Memory
    {
        private byte[] memory { get; set; }
        List<ROM> rom = new List<ROM>();

        public Memory(int size)
        {
            memory = new byte[size];
        }

        public void WriteByte(ulong addr, byte value)
        {
            if(!IsReadOnly((int)addr))
                memory[addr] = value;
        }

        private bool IsReadOnly(int addr) // A D E
        {
            foreach (var rom in rom)
                if (addr >= rom.start && addr <= rom.start + rom.len) return true;
            return false;
        }

        public byte ReadByte(ulong addr)
        {
            return memory[addr];
        }

        public ulong ReadWord(ulong addr)
        {
            byte value1 = memory[addr];
            byte value2 = memory[addr + 1];
            ulong value3 = (ulong)((value2 << 8) | value1);
            return value3;
        }

        public void Load(string filePath, int startAddr, int length, bool readOnly)
        {
            Array.Copy(File.ReadAllBytes(filePath), 0, memory, startAddr, length);
            if (readOnly)
                rom.Add(new ROM() { start = startAddr, len = length});
        }
    }

    internal class ROM
    {
        public int start { get; set; }
        public int len { get; set; }
    }
}
