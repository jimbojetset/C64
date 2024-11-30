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
            registers.PC += 1;
            return Absolute() + registers.X;
        }

        private ulong Y_Indexed_Absolute()
        {
            registers.PC += 1;
            return Absolute() + registers.Y;
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
            registers.PC++;
            return Zero_Page() + registers.Y;
        }

        private ulong X_Indexed_Zero_Page_Indirect(ulong value)
        { // 6 cycles
            return 0;
        }

        private ulong Zero_Page_Indirect_Y_Indexed(ulong value)
        { // 5 cycles
            return 0;
        }     // Addressing Modes

        private void LDA_Set_FlagsZN()
        {
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
        }

        private void LDA_IM()
        {
            registers.A = (byte)Immediate();
            LDA_Set_FlagsZN();
        }

        private void LDA_AB()
        {
            ulong addr = Absolute();
            registers.A = memory[addr & 0xFF];
            LDA_Set_FlagsZN();
        }

        private void LDA_ABX()
        {
            ulong addr = X_Indexed_Absolute();
            if (addr > 0xFFFF) registers.P = 1;
            registers.A = memory[addr & 0xFFFF];
            LDA_Set_FlagsZN();
        }

        private void LDA_ABY()
        {
            ulong addr = Y_Indexed_Absolute();
            if(addr > 0xFFFF) registers.P = 1;
            registers.A = memory[addr & 0xFFFF];
            LDA_Set_FlagsZN();
        }

        private void LDA_ZP()
        {
            byte addr = (byte)Zero_Page();
            registers.A = memory[addr];
            LDA_Set_FlagsZN();
        }

        private void LDA_ZPX()
        {
            byte addr = (byte)X_Indexed_Zero_Page();
            registers.A = memory[addr + registers.X];
            LDA_Set_FlagsZN();
        }

        public void Reset()
        {
            registers = new Registers();
            registers.FLAGS.SetFlagsFromByte(0x0);
            registers.PC = 0xFFFC;
            registers.P = 0x0100;
            registers.A = registers.X = registers.Y = 0;
            memory = new byte[0x10000];
            memory[0xFFFC] = 0xBD;
            memory[0xFFFD] = 0xEE;
            memory[0xFFFE] = 0xFF;
            memory[0xED] = 0x68;
            registers.X = 0xFF;

        }

    }
}
