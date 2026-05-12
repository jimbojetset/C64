namespace _6502CPU
{
    public class Registers
    {
        public ulong PC { get; set; } // Program Counter
        public byte S { get; set; } // Stack Pointer
        public byte P { get { return Flags.GetFlagsAsByte(); } set { Flags.SetFlagsFromByte(value); } }// Processor Status
        public byte A { get; set; } // Accumulator
        public byte X { get; set; } // X Index Register
        public byte Y { get; set; } // Y Index Register
        public Flags Flags = new Flags();

        public Registers()
        {
            Clear();
        }

        public void Clear()
        {
            PC = S = A = X = Y = 0;
            Flags.Clear();
            // 6502 reset semantics: I-flag is set so interrupts are masked
            // until the boot ROM explicitly does CLI. Clearing all flags
            // would leave I=0 and any IRQ injected before the reset routine
            // gets a chance to SEI would dispatch via an uninitialised RAM
            // vector and BRK-loop forever.
            Flags.I = true;
        }
    }
}
