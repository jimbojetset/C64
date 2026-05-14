using System;
using System.Runtime.CompilerServices;

namespace _6502CPU
{
    public class Memory
    {
        // The flat 64K address space. After banking is enabled, this
        // buffer always holds the RAM-under-ROM; reads in banked-out
        // regions return RAM from here, ROMs are kept separately below.
        public byte[] memory { get; set; }

        // Legacy ROM-write protection ranges (used only when no banked
        // ROM has been registered via LoadBankedROM). Kept so older test
        // harnesses that called Load(..., readOnly: true) keep behaving.
        private readonly List<ROM> rom = new List<ROM>();

        // ----- Banked ROM images for the 6510 -----
        // The CPU's processor port at $01 chooses which of BASIC ROM,
        // KERNAL ROM, character ROM or I/O is visible in the three
        // banking windows ($A000-$BFFF, $D000-$DFFF, $E000-$FFFF).
        // Storing each ROM in its own buffer lets writes to those
        // addresses fall through to RAM-under-ROM in memory[] without
        // corrupting the ROM image. Reads then select between ROM and
        // RAM based on the current bank state.
        private byte[]? basicRom;   // $A000-$BFFF (8 KiB)
        private byte[]? kernalRom;  // $E000-$FFFF (8 KiB)
        private byte[]? charRom;    // $D000-$DFFF (4 KiB)

        // RAM that lives underneath the $D000-$DFFF I/O/CHAR window.
        // Keep it separate from memory[] so writes while I/O is banked out
        // do not clobber live VIC/CIA/SID register bytes.
        private readonly byte[] ioUnderRam = new byte[0x1000];

        // True once at least one banked ROM has been registered. While
        // false, the legacy ReadByte/WriteByte path is used (memory[]
        // mirrors ROMs, writes to ROM ranges blocked).
        private bool bankingEnabled;

        // Slot identifiers for LoadBankedROM.
        public enum BankSlot { Basic, Kernal, Char }

        // Optional write hook for the I/O range $D000-$DFFF. Returning true
        // tells WriteByte to suppress the actual store (useful for ACK
        // semantics on registers like VIC's $D019, or for keeping a real
        // hardware register separate from a CPU-visible compare register).
        // Returning false means the write proceeds normally.
        public Func<ulong, byte, bool>? OnIOWrite;

        // Optional read hook for the I/O range $D000-$DFFF. When set,
        // ReadByte uses the hook's return value as the CPU-visible byte.
        public Func<ulong, byte, byte>? OnIORead;

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

        public void ClearIoUnderRam()
        {
            Array.Clear(ioUnderRam, 0, ioUnderRam.Length);
        }

        // Writes directly to underlying RAM regardless of current banking.
        // Used by loaders so bytes destined for $D000-$DFFF land in RAM
        // beneath I/O/CHAR mapping, matching C64 load behavior.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRamByte(ulong addr, byte value)
        {
            addr &= 0xFFFF;
            if (addr >= 0xD800 && addr < 0xDC00)
            {
                // Color RAM is a dedicated nibble RAM in the I/O window.
                // Keep a stable backing byte in memory[] for renderer fetches.
                memory[addr] = value;
                return;
            }
            if (addr >= 0xD000 && addr < 0xE000)
            {
                ioUnderRam[addr - 0xD000] = value;
                return;
            }

            memory[addr] = value;
        }

        // Loads a ROM into its own buffer and enables 6510-style banking.
        // The ROM no longer lives inside memory[] - writes to its address
        // range now land on RAM-under-ROM, and reads return the ROM only
        // when the corresponding bit in the $01 processor port selects it.
        public void LoadBankedROM(string filePath, BankSlot slot)
        {
            byte[] data = File.ReadAllBytes(filePath);
            switch (slot)
            {
                case BankSlot.Basic:
                    if (data.Length != 0x2000)
                        throw new InvalidDataException($"BASIC ROM must be 8 KiB (got {data.Length}).");
                    basicRom = data;
                    break;
                case BankSlot.Kernal:
                    if (data.Length != 0x2000)
                        throw new InvalidDataException($"KERNAL ROM must be 8 KiB (got {data.Length}).");
                    kernalRom = data;
                    break;
                case BankSlot.Char:
                    if (data.Length != 0x1000)
                        throw new InvalidDataException($"Character ROM must be 4 KiB (got {data.Length}).");
                    charRom = data;
                    break;
            }
            bankingEnabled = true;
        }

        // Direct access to a banked ROM image (e.g. so the boot code can
        // patch out RAMTAS in the KERNAL). Returns null if the slot has
        // not been loaded yet.
        public byte[]? GetBankedROM(BankSlot slot) => slot switch
        {
            BankSlot.Basic => basicRom,
            BankSlot.Kernal => kernalRom,
            BankSlot.Char => charRom,
            _ => null
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            if (!bankingEnabled)
            {
                // Legacy behaviour: ROM ranges are write-protected via the
                // rom list, and memory[] mirrors ROM bytes.
                if (rom.Count != 0 && IsROM((int)addr)) return;

                if (addr >= 0xD000 && addr < 0xE000 && OnIOWrite is not null)
                {
                    if (OnIOWrite(addr, value)) return;
                }

                memory[addr] = value;
                return;
            }

            // 6510 banked-write path. RAM exists underneath every ROM,
            // so writes to $A000-$BFFF / $E000-$FFFF always succeed (the
            // RAM byte is what reads see when the ROM is banked out).
            // Writes in $D000-$DFFF go to I/O if I/O is mapped, or to
            // RAM underneath if ROM/RAM is selected there.
            if (addr >= 0xD000 && addr < 0xE000)
            {
                int ioIdx = (int)(addr - 0xD000);
                if (addr >= 0xD800 && addr < 0xDC00)
                {
                    // Color RAM should not disappear when CHAREN/LORAM/HIRAM change.
                    memory[addr] = value;
                    return;
                }
                if (Is_IO_Mapped())
                {
                    if (OnIOWrite is not null && OnIOWrite(addr, value)) return;
                    memory[addr] = value;
                    return;
                }
                // CHAR ROM or RAM selected at $D000-$DFFF: writes go to
                // RAM underneath (CHAR ROM is read-only on real hardware).
                ioUnderRam[ioIdx] = value;
                return;
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
            if (!bankingEnabled)
            {
                byte legacy = memory[addr];
                if (addr >= 0xD000 && addr < 0xE000 && OnIORead is not null)
                    legacy = OnIORead(addr, legacy);
                if (addr >= 0xD000 && addr < 0xE000 && OnIOPostRead is not null)
                    OnIOPostRead(addr);
                return legacy;
            }

            // Hot paths first: zero page / stack / low RAM dominate.
            if (addr < 0xA000) return memory[addr];

            // $A000-$BFFF: BASIC ROM if both LORAM and HIRAM set.
            if (addr < 0xC000)
            {
                byte port = memory[0x0001];
                if (basicRom is not null && (port & 0x03) == 0x03)
                    return basicRom[addr - 0xA000];
                return memory[addr];
            }

            // $C000-$CFFF is plain RAM, never banked.
            if (addr < 0xD000) return memory[addr];

            // $D000-$DFFF: I/O, CHAR ROM, or RAM. See PLA truth table.
            if (addr < 0xE000)
            {
                if (addr >= 0xD800 && addr < 0xDC00)
                {
                    // Color RAM remains CPU-visible independently of CHAREN mapping.
                    return memory[addr];
                }
                byte port = memory[0x0001];
                int loHi = port & 0x03;            // LORAM | HIRAM
                bool charen = (port & 0x04) != 0;
                if (loHi == 0)
                {
                    // Both LORAM and HIRAM clear: RAM mapped.
                    return ioUnderRam[addr - 0xD000];
                }
                if (charen)
                {
                    // I/O mapped (the usual KERNAL configuration).
                    byte v = memory[addr];
                    if (OnIORead is not null) v = OnIORead(addr, v);
                    if (OnIOPostRead is not null) OnIOPostRead(addr);
                    return v;
                }
                // CHAREN=0: CHAR ROM visible (used while copying char
                // bitmaps into RAM).
                if (charRom is not null)
                    return charRom[addr - 0xD000];
                return ioUnderRam[addr - 0xD000];
            }

            // $E000-$FFFF: KERNAL ROM if HIRAM set.
            byte p2 = memory[0x0001];
            if (kernalRom is not null && (p2 & 0x02) != 0)
                return kernalRom[addr - 0xE000];
            return memory[addr];
        }

        // True when the $D000-$DFFF window currently maps the I/O chips
        // (the only configuration in which OnIOWrite must fire).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Is_IO_Mapped()
        {
            byte port = memory[0x0001];
            int loHi = port & 0x03;
            bool charen = (port & 0x04) != 0;
            return loHi != 0 && charen;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadWord(ulong addr)
        {
            // Honour banking on word reads (e.g. IRQ / NMI / RESET vector
            // fetches at $FFFA / $FFFC / $FFFE).
            return (ulong)(ReadByte(addr) | (ReadByte(addr + 1) << 8));
        }

        // VIC-II memory view: always sees RAM in the selected 16 KiB bank,
        // except for the character ROM shadow handled in Display.cs.
        // In particular, $D000-$DFFF must read RAM-under-I/O, not CPU I/O regs.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadVicByte(ulong addr)
        {
            addr &= 0xFFFF;
            if (addr >= 0xD800 && addr < 0xDC00)
                return memory[addr];
            if (addr >= 0xD000 && addr < 0xE000)
                return ioUnderRam[addr - 0xD000];
            return memory[addr];
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
