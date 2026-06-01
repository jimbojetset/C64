// ============================================================================
// Project:     C64
// File:        CPU_6510.cs
// Description: C64-facing 6510 CPU wrapper over the reusable 6502 core.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

namespace C64.CPU
{
    /// <summary>
    /// C64-facing 6510 CPU wrapper. The 6510-specific processor port and C64 banking behavior live in C64MemoryBus.
    /// </summary>
    public class CPU_6510 : CPU_6502
    {
        /// <summary>Initializes a new CPU_6510 instance.</summary>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6510(int freq = 1000000)
            : base(freq)
        {
        }

        /// <summary>Initializes a new CPU_6510 instance with an external CPU bus.</summary>
        /// <param name="bus">The CPU-visible bus implementation.</param>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6510(ICpuBus bus, int freq = 1000000)
            : base(bus, freq)
        {
        }
    }
}
