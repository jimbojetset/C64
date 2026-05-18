namespace C64.CPU
{

    /// <summary>
    /// Holds the CPU register set and maps the processor status byte through the structured flag model.
    /// </summary>
    public class Registers
    {

        /// <summary>Gets or sets the CPU program counter.</summary>
        public ulong PC { get; set; } // Program Counter

        /// <summary>Gets or sets the CPU stack pointer.</summary>
        public byte S { get; set; } // Stack Pointer
        public byte P { get { return Flags.GetFlagsAsByte(); } set { Flags.SetFlagsFromByte(value); } }// Processor Status

        /// <summary>Gets or sets the CPU accumulator.</summary>
        public byte A { get; set; } // Accumulator

        /// <summary>Gets or sets the CPU X register.</summary>
        public byte X { get; set; } // X Index Register

        /// <summary>Gets or sets the CPU Y register.</summary>
        public byte Y { get; set; } // Y Index Register
        public Flags Flags = new Flags();

        /// <summary>Initializes a new Registers instance.</summary>
        public Registers()
        {
            Clear();
        }

        /// <summary>Clears this instance to its reset state.</summary>
        public void Clear()
        {
            PC = S = A = X = Y = 0;
            Flags.Clear();
            Flags.I = true;
        }
    }
}
