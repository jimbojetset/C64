// Copyright (c) 2025 James Booth
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;

namespace _6502CPU
{
    public class Memory
    {
        public byte[] memory { get; set; }
        readonly List<ROM> rom = new List<ROM>();

        public Memory(int size)
        {
            memory = new byte[size];
        }

        public void WriteByte(ulong addr, byte value)
        {
            if(!IsROM((int)addr))
                memory[addr] = value;
        }

        private bool IsROM(int addr) // A D E
        {
            foreach (var rom in rom)
                if (addr >= rom.StartAddr && addr <= rom.StartAddr + rom.Length) return true;
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
                rom.Add(new ROM() { StartAddr = startAddr, Length = length});
        }
    }

    internal class ROM
    {
        public int StartAddr { get; set; }
        public int Length { get; set; }
    }
}
