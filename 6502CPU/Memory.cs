using System;
using System.Runtime.CompilerServices;

namespace _6502CPU
{
    public class Memory
    {
        public byte[] memory { get; set; }

        // Shadow copies of each ROM region as originally loaded. Used by
        // RestoreRoms() so a hard-reset can put the ROM bytes back after a
        // running program has overwritten them (which it's allowed to do -
        // see the WriteByte comment).
        private readonly List<RomRegion> roms = new List<RomRegion>();

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

        // On a real C64, RAM exists at every address $0000-$FFFF. ROMs
        // (BASIC at $A000, CHAR at $D000, KERNAL at $E000) only overlay on
        // READS, and only when the CPU port at $0001 says they're banked
        // in. Writes ALWAYS reach the underlying RAM regardless of bank
        // state. We don't model the bank register, so we approximate:
        // writes go straight to memory[], even into ROM-loaded regions.
        // RestoreRoms() puts the original ROM bytes back on hard reset so
        // the KERNAL still boots cleanly after a program has trashed it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            // Hook only fires inside the I/O range to keep RAM writes fast.
            if (addr >= 0xD000 && addr < 0xE000 && OnIOWrite is not null)
            {
                if (OnIOWrite(addr, value)) return;
            }

            memory[addr] = value;
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

        // Loads a binary file into memory at startAddr. When isRom is true
        // a shadow copy of the bytes is kept so a later RestoreRoms() can
        // re-stamp them back over whatever the running program wrote.
        public void Load(string filePath, int startAddr, int length, bool isRom)
        {
            byte[] data = File.ReadAllBytes(filePath);
            Array.Copy(data, 0, memory, startAddr, length);
            if (isRom)
            {
                byte[] shadow = new byte[length];
                Array.Copy(data, 0, shadow, 0, length);
                roms.Add(new RomRegion { StartAddr = startAddr, Bytes = shadow });
            }
        }

        // Re-stamps each registered ROM region back into memory[], undoing
        // any writes a running program may have done there. Call from a
        // hard reset before re-running the KERNAL reset routine.
        public void RestoreRoms()
        {
            for (int i = 0; i < roms.Count; i++)
            {
                var r = roms[i];
                Array.Copy(r.Bytes, 0, memory, r.StartAddr, r.Bytes.Length);
            }
        }
    }

    internal class RomRegion
    {
        public int StartAddr;
        public byte[] Bytes = Array.Empty<byte>();
    }
}
