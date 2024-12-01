using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// https://www.masswerk.at/6502/6502_instruction_set.html

// https://www.pagetable.com/c64ref/6502/?tab=2

// https://github.com/JamesRandall/6502-Emulator/blob/master/Emulator6502/Components/OpcodeExecuterBase.cs

namespace _6502CPU
{
    public class _6502_CPU
    {
        private Registers registers = new Registers();

        private RAM memory;

        public _6502_CPU()
        {
            Reset();
        }

        public void Execute()
        {
            byte instruction = memory.ReadByte(registers.PC);
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
            byte addr = memory.ReadByte(registers.PC + 1);
            registers.PC += 2;
            return addr;
        }

        private ulong Absolute()
        {
            ulong addr = memory.ReadWord(registers.PC + 1);
            registers.PC += 3;
            return addr;
        }

        private ulong X_Indexed_Absolute()
        {
            ulong addr = Absolute() + registers.X;
            return addr;
        }

        private ulong Y_Indexed_Absolute()
        {
            ulong addr = Absolute() + registers.Y;
            return addr;
        }

        private ulong Zero_Page()
        {
            byte value = memory.ReadByte(registers.PC + 1);
            byte addr = memory.ReadByte(value);
            registers.PC += 2;
            return addr;
        }

        private ulong X_Indexed_Zero_Page()
        {
            registers.PC ++;
            return Zero_Page() + registers.X;
        }

        private ulong Y_Indexed_Zero_Page()
        {
            ulong value1 = Zero_Page() + registers.Y;
            registers.PC++;
            return value1;
        }

        private ulong X_Indexed_Zero_Page_Indirect()
        { 
            ulong value1 = (ulong)(memory[registers.PC + 1] + registers.X);
            ulong value2 = memory[value1];



            return value2;
        }

        private ulong Zero_Page_Indirect_Y_Indexed(ulong value)
        { // 5 cycles
            return 0;
        }

        private void Set_FlagsNZ(byte value)
        {
            registers.Flags.Z = (value == 0);
            registers.Flags.N = ((value & 0x40) == 0x40);
        }

        // ***********************
        // End Of Addressing Modes
        // ***********************

        private void LDA_IM()
        {
            registers.AC = (byte)Immediate();
            Set_FlagsNZ(registers.AC);
        }

        private void LDA_AB()
        {
            ulong addr = Absolute();
            registers.AC = memory[addr & 0xFF];
            Set_FlagsNZ(registers.AC);
        }

        private void LDA_ABX()
        {
            ulong addr = X_Indexed_Absolute();
            registers.AC = memory[addr & 0xFFFF];
            Set_FlagsNZ(registers.AC);
        }

        private void LDA_ABY()
        {
            ulong addr = Y_Indexed_Absolute();
            registers.AC = memory[addr & 0xFFFF];
            Set_FlagsNZ(registers.AC);
        }

        private void LDA_ZP()
        {
            byte addr = (byte)Zero_Page();
            registers.AC = memory[addr];
            Set_FlagsNZ(registers.AC);
        }

        private void LDA_ZPX()
        {
            byte addr = (byte)X_Indexed_Zero_Page();
            registers.AC = memory[addr + registers.X];
            Set_FlagsNZ(registers.AC);
        }

        public void Reset()
        {
            registers = new Registers();
            registers.Clear();
            memory = new RAM(0x10000);
        }
    }
}
