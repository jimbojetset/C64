namespace C64.CPU
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
            Flags.I = true;
        }
    }
}
