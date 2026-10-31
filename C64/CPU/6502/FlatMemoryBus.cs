

using System.Runtime.CompilerServices;

namespace C64.CPU
{

    public class FlatMemoryBus : ICpuBus
    {

        public byte[] Memory { get; }

        public Func<ulong, byte, bool>? OnWrite;

        public Func<ulong, byte, byte>? OnRead;

        /// <summary>Allocates a flat byte-addressable bus, normally the full 6502 64 KiB address space.</summary>
        /// <param name="size">The bus size in bytes. Defaults to the 6502 64 KiB address space.</param>
        public FlatMemoryBus(int size = 0x10000)
        {
            Memory = new byte[size];
        }

        /// <summary>Reads a byte from the CPU-visible address space.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ulong addr)
        {
            addr %= (ulong)Memory.Length;
            byte value = Memory[addr];
            return OnRead is null ? value : OnRead(addr, value);
        }

        /// <summary>Writes a byte to the CPU-visible address space.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value to write to the bus.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ulong addr, byte value)
        {
            addr %= (ulong)Memory.Length;
            if (OnWrite is not null && OnWrite(addr, value))
                return;

            Memory[addr] = value;
        }

        /// <summary>Reads a little-endian 16-bit word from the bus.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadWord(ulong addr)
        {
            return (ulong)(ReadByte(addr) | (ReadByte((addr + 1) & 0xFFFF) << 8));
        }

        /// <summary>Copies a byte sequence into bus memory from the supplied start address.</summary>
        /// <param name="startAddr">The first emulated address to fill.</param>
        /// <param name="data">The bytes to copy into memory.</param>
        public void Load(ulong startAddr, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
                Memory[(startAddr + (ulong)i) % (ulong)Memory.Length] = data[i];
        }
    }
}
