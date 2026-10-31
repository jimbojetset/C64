

namespace C64.CPU
{

    public class CPU_6510 : CPU_6502
    {
        /// <summary>Creates a 6510-compatible CPU core with an internal bus at the requested clock rate.</summary>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6510(int freq = 1000000)
            : base(freq)
        {
        }

        /// <summary>Creates a 6510-compatible CPU core connected to the supplied C64 memory bus.</summary>
        /// <param name="bus">The CPU-visible bus implementation.</param>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6510(ICpuBus bus, int freq = 1000000)
            : base(bus, freq)
        {
        }
    }
}
