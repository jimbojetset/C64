// ============================================================================
// Project:     C64
// File:        Memory.cs
// Description: C64 memory map implementation with 6510 ROM banking,
//              RAM-under-ROM behavior, I/O hooks, color RAM, and VIC-visible
//              reads.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Runtime.CompilerServices;

namespace C64.CPU
{
    /// <summary>
    /// Models the C64 address space, including RAM-under-ROM, 6510 banking, I/O hooks, color RAM behavior, and VIC-visible reads.
    /// </summary>
    public class Memory
    {
        // The flat 64K address space. After banking is enabled, this
        // buffer always holds the RAM-under-ROM; reads in banked-out
        // regions return RAM from here, ROMs are kept separately below.

        /// <summary>Gets or sets the backing 64 KiB memory array.</summary>
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

        /// <summary>Defines values for Bank Slot.</summary>
        public enum BankSlot
        { Basic, Kernal, Char }

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

        /// <summary>Initializes a new Memory instance.</summary>
        /// <param name="size">The size of the emulated memory in bytes.</param>
        public Memory(int size)
        {
            memory = new byte[size];
        }

        /// <summary>Clears io under ram.</summary>
        public void ClearIoUnderRam()
        {
            Array.Clear(ioUnderRam, 0, ioUnderRam.Length);
        }

        // Writes directly to underlying RAM regardless of current banking.
        // Used by loaders so bytes destined for $D000-$DFFF land in RAM
        // beneath I/O/CHAR mapping, matching C64 load behavior.

        /// <summary>Writes ram byte.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRamByte(ulong addr, byte value)
        {
            addr &= 0xFFFF;
            if (addr >= 0xD800 && addr < 0xDC00)
            {
                // Raw RAM write (loader/debug) should target RAM-under-I/O.
                // Do not mirror into color RAM; that is a separate nibble RAM.
                ioUnderRam[addr - 0xD000] = value;
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

        /// <summary>Loads banked rom.</summary>
        /// <param name="filePath">The path of the file to load.</param>
        /// <param name="slot">The ROM bank slot that receives the loaded image.</param>
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

        /// <summary>Gets banked rom.</summary>
        public byte[]? GetBankedROM(BankSlot slot) => slot switch
        {
            BankSlot.Basic => basicRom,
            BankSlot.Kernal => kernalRom,
            BankSlot.Char => charRom,
            _ => null
        };

        /// <summary>Writes byte.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
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
                    // $D800-$DBFF is color RAM when I/O is mapped, but must behave
                    // as normal RAM-under-I/O when I/O is banked out.
                    if (Is_IO_Mapped())
                    {
                        memory[addr] = value;
                    }
                    else
                    {
                        ioUnderRam[ioIdx] = value;
                    }
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

        /// <summary>Determines whether an address is in a legacy ROM range.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Reads byte.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value produced by the operation.</returns>
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
                byte port = GetProcessorPortEffective();
                if (basicRom is not null && (port & 0x03) == 0x03)
                    return basicRom[addr - 0xA000];
                return memory[addr];
            }

            // $C000-$CFFF is plain RAM, never banked.
            if (addr < 0xD000) return memory[addr];

            // $D000-$DFFF: I/O, CHAR ROM, or RAM. See PLA truth table.
            if (addr < 0xE000)
            {
                byte port = GetProcessorPortEffective();
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
                    if (addr >= 0xD800 && addr < 0xDC00)
                        return memory[addr];

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
            byte p2 = GetProcessorPortEffective();
            if (kernalRom is not null && (p2 & 0x02) != 0)
                return kernalRom[addr - 0xE000];
            return memory[addr];
        }

        // True when the $D000-$DFFF window currently maps the I/O chips
        // (the only configuration in which OnIOWrite must fire).

        /// <summary>Determines whether the I/O window is currently mapped.</summary>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Is_IO_Mapped()
        {
            byte port = GetProcessorPortEffective();
            int loHi = port & 0x03;
            bool charen = (port & 0x04) != 0;
            return loHi != 0 && charen;
        }

        /// <summary>Computes the effective 6510 processor-port value.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetProcessorPortEffective()
        {
            // 6510 processor port: $0000 is DDR, $0001 is data register.
            // Output bits come from data register; input bits read high due to pull-ups.
            byte ddr = memory[0x0000];
            byte data = memory[0x0001];
            return (byte)((data & ddr) | (~ddr & 0xFF));
        }

        /// <summary>Reads word.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The numeric value produced by the operation.</returns>
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

        /// <summary>Reads vic byte.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value produced by the operation.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadVicByte(ulong addr)
        {
            addr &= 0xFFFF;
            if (addr >= 0xD000 && addr < 0xE000)
                return ioUnderRam[addr - 0xD000];
            return memory[addr];
        }

        /// <summary>Loads data from disk.</summary>
        /// <param name="filePath">The path of the file to load.</param>
        /// <param name="startAddr">The first emulated address to fill.</param>
        /// <param name="length">The number of bytes to load.</param>
        /// <param name="readOnly">Whether the loaded range should be marked read-only.</param>
        public void Load(string filePath, int startAddr, int length, bool readOnly)
        {
            Array.Copy(File.ReadAllBytes(filePath), 0, memory, startAddr, length);
            if (readOnly)
                rom.Add(new ROM() { StartAddr = startAddr, Length = length });
        }
    }

    /// <summary>
    /// Describes a legacy read-only memory range used by the pre-banked memory path.
    /// </summary>
    internal class ROM
    {
        /// <summary>Gets or sets the ROM start address.</summary>
        public int StartAddr { get; set; }

        /// <summary>Gets or sets the length value.</summary>
        public int Length { get; set; }
    }
}