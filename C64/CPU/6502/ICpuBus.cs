

namespace C64.CPU
{

    public interface ICpuBus
    {
        /// <summary>Reads a byte from the CPU-visible address space.</summary>
        /// <param name="addr">The emulated address to access.</param>
        byte ReadByte(ulong addr);

        /// <summary>Writes a byte to the CPU-visible address space.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value to write to the bus.</param>
        void WriteByte(ulong addr, byte value);
    }
}
