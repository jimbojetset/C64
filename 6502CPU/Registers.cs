using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _6502CPU
{
    public class Registers
    {
        public ulong PC { get; set; }
        public ulong P { get; set; }
        public ulong S { get; set; }
        public byte A { get; set; }
        public byte X { get; set; }
        public byte Y { get; set; }
        public Flags FLAGS = new Flags();

        public Registers()
        {
        
        }
    }
}
