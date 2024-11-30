using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _6502CPU
{
    public class Registers
    {
        public ulong PC { get; set; } // Program Counter
        public byte P { get; set; } // Processor Status
        public byte S { get; set; } // Stack Pointer
        public byte A { get; set; } // Accumulator
        public byte X { get; set; } // X Index Register
        public byte Y { get; set; } // Y Index Register
        public Flags Flags = new Flags();

        public Registers()
        {
        
        }

        public void Clear()
        {
            PC = P = S = A = X = Y = 0;
            Flags.Clear();
        }
    }
}
