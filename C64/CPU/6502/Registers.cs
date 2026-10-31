

namespace C64.CPU
{

    public class Registers
    {

        public ulong PC { get; set; }

        public byte S { get; set; }

        public byte P
        { get { return Flags.GetFlagsAsByte(); } set { Flags.SetFlagsFromByte(value); } }

        public byte A { get; set; }

        public byte X { get; set; }

        public byte Y { get; set; }

        public Flags Flags = new();

        /// <summary>Creates a 6502 register set with a fresh processor-status register.</summary>
        public Registers()
        {
            Clear();
        }

        /// <summary>Clears A, X, Y, PC, and S while restoring the processor-status reset value.</summary>
        public void Clear()
        {
            PC = S = A = X = Y = 0;
            Flags.Clear();
            Flags.I = true;
        }
    }
}