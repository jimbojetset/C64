using System;
using System.Runtime.CompilerServices;

namespace _6502CPU
{
    public class Memory
    {
        public byte[] memory { get; set; }
        private readonly List<ROM> rom = new List<ROM>();

        // Optional write hook for the I/O range $D000-$DFFF. Returning true
        // tells WriteByte to suppress the actual store (useful for ACK
        // semantics on registers like VIC's $D019, or for keeping a real
        // hardware register separate from a CPU-visible compare register).
        // Returning false means the write proceeds normally.
        public Func<ulong, byte, bool>? OnIOWrite;

        // Optional post-read hook for the I/O range $D000-$DFFF. Called
        // AFTER ReadByte has captured the value to return, so the hook may
        // freely mutate memory[addr]. Models real-hardware "read clears the
        // latch" behaviour for things like the VIC collision registers
        // ($D01E / $D01F).
        public Action<ulong>? OnIOPostRead;

        public Memory(int size)
        {
            memory = new byte[size];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            // ROM protection still wins over everything else.
            if (rom.Count != 0 && IsROM((int)addr)) return;

            // Hook only fires inside the I/O range to keep RAM writes fast.
            if (addr >= 0xD000 && addr < 0xE000 && OnIOWrite is not null)
            {
                if (OnIOWrite(addr, value)) return;
            }

            memory[addr] = value;
        }

        private bool IsROM(int addr)
        {
            // Use indexed for-loop to avoid enumerator allocation on each call.
            var list = rom;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var r = list[i];
                if (addr >= r.StartAddr && addr <= r.StartAddr + r.Length) return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ulong addr)
        {
            byte v = memory[addr];
            if (addr >= 0xD000 && addr < 0xE000 && OnIOPostRead is not null)
                OnIOPostRead(addr);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadWord(ulong addr)
        {
            byte[] m = memory;
            return (ulong)(m[addr] | (m[addr + 1] << 8));
        }

        public void Load(string filePath, int startAddr, int length, bool readOnly)
        {
            Array.Copy(File.ReadAllBytes(filePath), 0, memory, startAddr, length);
            if (readOnly)
                rom.Add(new ROM() { StartAddr = startAddr, Length = length });
        }
    }

    internal class ROM
    {
        public int StartAddr { get; set; }
        public int Length { get; set; }
    }
}
