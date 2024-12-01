using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// https://www.masswerk.at/6502/6502_instruction_set.html

// https://www.pagetable.com/c64ref/6502/?tab=2

namespace _6502CPU
{
    public class _6502_CPU
    {
        private Registers registers = new Registers();

        private byte[] memory = new byte[0x10000];

        public _6502_CPU()
        {
            Reset();
        }

        public void Execute()
        {
            byte instruction = memory[registers.PC];
            switch (instruction)
            {
                case 0xA9:
                    LDA_IM();
                    break;
                case 0xAD:
                    LDA_AB();
                    break;
                case 0xBD:
                    LDA_ABX();
                    break;
                case 0xB9:
                    LDA_ABY();
                    break;
                case 0xA5:
                    LDA_ZP();
                    break;
                case 0xB5:
                    LDA_ZPX();
                    break;
                default:
                    Debug.WriteLine("Instruction not handled " + instruction.ToString("x2"));
                    break;
            }
            registers.PC++;
        }

        // *************************
        // Start Of Addressing Modes
        // *************************
        

        private ulong Immediate()
        {
            byte value1 = memory[registers.PC + 1];
            registers.PC += 2;
            return value1;
        }

        private ulong Absolute()
        {
            byte value1 = memory[registers.PC + 1];
            byte value2 = memory[registers.PC + 2];
            registers.PC += 3;
            return (ulong)((value2 << 8) + value1);
        }

        private ulong X_Indexed_Absolute()
        {
            ulong value1 = Absolute() + registers.X;
            registers.PC += 1;
            return value1;
        }

        private ulong Y_Indexed_Absolute()
        {
            ulong value1 = Absolute() + registers.Y;
            registers.PC += 1;
            return value1;
        }

        private ulong Zero_Page()
        {
            byte value1 = memory[registers.PC + 2];
            registers.PC += 2;
            return value1;
        }

        private ulong X_Indexed_Zero_Page()
        {
            registers.PC ++;
            return Zero_Page() + registers.X;
        }

        private ulong Y_Indexed_Zero_Page(ulong value)
        {
            ulong value1 = Zero_Page() + registers.Y;
            registers.PC++;
            return value1;
        }

        private ulong X_Indexed_Zero_Page_Indirect(ulong value)
        { // 6 cycles
            return 0;
        }

        private ulong Zero_Page_Indirect_Y_Indexed(ulong value)
        { // 5 cycles
            return 0;
        }

        private ulong Relative()
        {
            byte value1 = memory[registers.PC + 1];
            ulong value2 = 0;
            if (!registers.Flags.C)
            {
                value2 = registers.PC + 2 + value1;
                if(((registers.PC + 2) & 0xFF00) != (value2 & 0xFF00))
                { }
            }

            // incomplete !!!
            registers.PC = value2 & 0xFF;
            return value1;
        }
        // ***********************
        // End Of Addressing Modes
        // ***********************

        private void Set_FlagsNZ()
        {
            registers.Flags.Z = (registers.A == 0);
            registers.Flags.N = ((registers.A & 0x40) == 0x40);
        }

        private void LDA_IM()
        {
            registers.A = (byte)Immediate();
            Set_FlagsNZ();
        }

        private void LDA_AB()
        {
            ulong addr = Absolute();
            registers.A = memory[addr & 0xFF];
            Set_FlagsNZ();
        }

        private void LDA_ABX()
        {
            ulong addr = X_Indexed_Absolute();
            registers.A = memory[addr & 0xFFFF];
            Set_FlagsNZ();
        }

        private void LDA_ABY()
        {
            ulong addr = Y_Indexed_Absolute();
            registers.A = memory[addr & 0xFFFF];
            Set_FlagsNZ();
        }

        private void LDA_ZP()
        {
            byte addr = (byte)Zero_Page();
            registers.A = memory[addr];
            Set_FlagsNZ();
        }

        private void LDA_ZPX()
        {
            byte addr = (byte)X_Indexed_Zero_Page();
            registers.A = memory[addr + registers.X];
            Set_FlagsNZ();
        }

        public void Reset()
        {
            registers = new Registers();
            registers.Clear();
            memory = new byte[0x10000];
        }
    }
}
