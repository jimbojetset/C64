

using System.Runtime.CompilerServices;

namespace C64.CPU
{

    public class C64MemoryBus : ICpuBus
    {

        public byte[] memory { get; set; }

        private readonly List<ROM> rom = [];

        private byte[]? basicRom;

        private byte[]? kernalRom;
        private byte[]? charRom;

        private readonly byte[] ioUnderRam = new byte[0x1000];

        private bool bankingEnabled;

        public enum BankSlot
        { Basic, Kernal, Char }

        public Func<ulong, byte, bool>? OnIOWrite;

        public Func<ulong, byte, byte>? OnIORead;

        public Action<ulong>? OnIOPostRead;

        public Func<ulong, byte, byte?>? OnCartridgeRead;

        public Func<ulong, byte, bool>? OnCartridgeWrite;

        public Func<ulong, byte, bool>? OnMemoryWrite;

        public Func<ulong, byte, byte>? OnMemoryRead;

        public Action<C64MemoryBus, byte>? OnFF00Write;

        /// <summary>Allocates C64 RAM and the hidden RAM beneath the $D000 I/O window.</summary>
        /// <param name="size">The size of the emulated memory in bytes.</param>
        public C64MemoryBus(int size)
        {
            memory = new byte[size];
        }

        /// <summary>Clears the 4 KiB RAM shadowed by character ROM or I/O at $D000-$DFFF.</summary>
        public void ClearIoUnderRam()
        {
            Array.Clear(ioUnderRam, 0, ioUnderRam.Length);
        }

        /// <summary>Writes directly to physical RAM, including the shadow RAM beneath the I/O window.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRamByte(ulong addr, byte value)
        {
            addr &= 0xFFFF;
            if (addr >= 0xD800 && addr < 0xDC00)
            {

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

        /// <summary>Loads and validates a BASIC, KERNAL, or character ROM for 6510 banking.</summary>
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

        /// <summary>Returns the ROM image installed in the requested 6510 bank slot.</summary>
        public byte[]? GetBankedROM(BankSlot slot) => slot switch
        {
            BankSlot.Basic => basicRom,
            BankSlot.Kernal => kernalRom,
            BankSlot.Char => charRom,
            _ => null
        };

        /// <summary>Routes a CPU write through cartridge, I/O, ROM-protection, and RAM banking rules.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            if (addr == 0xFF00 && OnFF00Write is not null)
                OnFF00Write(this, value);

            if (!bankingEnabled)
            {
                if (OnMemoryWrite is not null && OnMemoryWrite(addr, value))
                    return;

                if (rom.Count != 0 && IsROM((int)addr)) return;

                if (addr >= 0xD000 && addr < 0xE000 && OnIOWrite is not null)
                {
                    if (OnIOWrite(addr, value)) return;
                }

                memory[addr] = value;
                return;
            }

            byte port = GetProcessorPortEffective();

            if (addr >= 0x8000 && addr < 0xD000 && OnCartridgeWrite is not null)
            {
                _ = OnCartridgeWrite(addr, port);
            }

            if (addr >= 0xD000 && addr < 0xE000)
            {
                int ioIdx = (int)(addr - 0xD000);
                if (addr >= 0xD800 && addr < 0xDC00)
                {

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

                ioUnderRam[ioIdx] = value;
                return;
            }

            if (addr >= 0xE000 && OnCartridgeWrite is not null)
            {
                _ = OnCartridgeWrite(addr, port);
            }

            memory[addr] = value;
        }

        /// <summary>Determines whether an address is in a legacy ROM range.</summary>
        /// <param name="addr">The emulated address to access.</param>
        private bool IsROM(int addr)
        {

            var list = rom;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var r = list[i];
                if (addr >= r.StartAddr && addr <= r.StartAddr + r.Length) return true;
            }
            return false;
        }

        /// <summary>Resolves a CPU read through cartridge, BASIC, KERNAL, character-ROM, I/O, and RAM mappings.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ulong addr)
        {
            if (!bankingEnabled)
            {
                byte legacy = memory[addr];
                if (OnMemoryRead is not null)
                    legacy = OnMemoryRead(addr, legacy);
                if (addr >= 0xD000 && addr < 0xE000 && OnIORead is not null)
                    legacy = OnIORead(addr, legacy);
                if (addr >= 0xD000 && addr < 0xE000 && OnIOPostRead is not null)
                    OnIOPostRead(addr);
                return legacy;
            }

            if (addr < 0x8000) return memory[addr];

            byte port = GetProcessorPortEffective();

            if (OnCartridgeRead is not null)
            {
                byte? cart = OnCartridgeRead(addr, port);
                if (cart.HasValue)
                    return cart.Value;
            }

            if (addr < 0xA000) return memory[addr];

            if (addr < 0xC000)
            {
                if (basicRom is not null && (port & 0x03) == 0x03)
                    return basicRom[addr - 0xA000];
                return memory[addr];
            }

            if (addr < 0xD000) return memory[addr];

            if (addr < 0xE000)
            {
                int loHi = port & 0x03;
                bool charen = (port & 0x04) != 0;
                if (loHi == 0)
                {

                    return ioUnderRam[addr - 0xD000];
                }
                if (charen)
                {

                    if (addr >= 0xD800 && addr < 0xDC00)
                        return memory[addr];

                    byte v = memory[addr];
                    if (OnIORead is not null) v = OnIORead(addr, v);
                    if (OnIOPostRead is not null) OnIOPostRead(addr);
                    return v;
                }

                if (charRom is not null)
                    return charRom[addr - 0xD000];
                return ioUnderRam[addr - 0xD000];
            }

            if (OnCartridgeRead is not null)
            {
                byte? cart = OnCartridgeRead(addr, port);
                if (cart.HasValue)
                    return cart.Value;
            }

            if (kernalRom is not null && (port & 0x02) != 0)
                return kernalRom[addr - 0xE000];
            return memory[addr];
        }

        /// <summary>Determines whether the I/O window is currently mapped.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Is_IO_Mapped()
        {
            byte port = GetProcessorPortEffective();
            int loHi = port & 0x03;
            bool charen = (port & 0x04) != 0;
            return loHi != 0 && charen;
        }

        /// <summary>Computes the effective 6510 processor-port value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetProcessorPortEffective()
        {

            byte ddr = memory[0x0000];
            byte data = memory[0x0001];
            return (byte)((data & ddr) | (~ddr & 0xFF));
        }

        /// <summary>Reads a little-endian 16-bit value through the current CPU memory mapping.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadWord(ulong addr)
        {

            return (ulong)(ReadByte(addr) | (ReadByte(addr + 1) << 8));
        }

        /// <summary>Reads physical RAM as seen by the VIC-II, bypassing CPU ROM and I/O banking.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadVicByte(ulong addr)
        {
            addr &= 0xFFFF;
            if (addr >= 0xD000 && addr < 0xE000)
                return ioUnderRam[addr - 0xD000];
            return memory[addr];
        }

        /// <summary>Copies a file range into RAM and optionally protects it as legacy ROM.</summary>
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

    internal class ROM
    {

        public int StartAddr { get; set; }

        public int Length { get; set; }
    }

    public class Memory : C64MemoryBus
    {
        /// <summary>Creates the flat-memory-compatible C64 bus used by the 6502 core.</summary>
        /// <param name="size">The size of the emulated memory in bytes.</param>
        public Memory(int size)
            : base(size)
        {
        }
    }
}
