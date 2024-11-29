using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
                case 0xA5:
                    LDA_ZP();
                    break;
                case 0xB5:
                    LDA_ZPX();
                    break;
                case 0xAD:
                    LDA_AB();
                    break;
                case 0xBD:
                    LDA_ABX();
                    break;
                default:
                    Debug.WriteLine("Instruction not handled " + instruction.ToString("x2"));
                    break;
            }
            registers.PC++;
        }

        private void LDA_IM()
        {
            byte value = memory[registers.PC + 1];
            registers.A = value;
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
            registers.PC++;
        }

        private void LDA_AB()
        {
            byte value1 = memory[registers.PC + 1];
            byte value2 = memory[registers.PC + 2];
            ulong addr = (ulong)((value2 << 8) + value1);
            registers.A = memory[addr];
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
            registers.PC += 2;
        }

        private void LDA_ABX()
        {
            byte value1 = memory[registers.PC + 1];
            byte value2 = memory[registers.PC + 2];
            ulong value3 = (ulong)((value2 << 8) + value1);
            ulong addr = value3 + registers.X;
            registers.FLAGS.C = (addr > 0xFFFF);
            registers.A = memory[addr & 0xFFFF];
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
            registers.PC += 2;
        }

        private void LDA_ABY()
        {
            byte value1 = memory[registers.PC + 1];
            byte value2 = memory[registers.PC + 2];
            ulong value3 = (ulong)((value2 << 8) + value1);
            ulong addr = value3 + registers.Y;
            registers.FLAGS.C = (addr > 0xFFFF);
            registers.A = memory[addr & 0xFFFF];
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
            registers.PC += 2;
        }

        private void LDA_ZP()
        {
            byte addr = memory[registers.PC + 1];
            registers.A = memory[addr];
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
            registers.PC++;
        }

        private void LDA_ZPX()
        {
            byte addr = memory[registers.PC + 1];
            registers.A = memory[addr + registers.X];
            registers.FLAGS.Z = (registers.A == 0);
            registers.FLAGS.N = ((registers.A & 0x40) == 0x40);
            registers.PC++;
        }

        public void Reset()
        {
            registers = new Registers();
            registers.FLAGS.SetFlagsFromByte(0x0);
            registers.PC = 0xFFFC;
            registers.SP = 0x0100;
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
