using Microsoft.Win32;
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
        public Registers registers = new Registers();

        public RAM memory = new RAM(0x10000);

        private bool running = true;
        public bool Running {get; set;}

        public _6502_CPU()
        {
            Initialise();
        }

        public void Initialise()
        {
            registers = new Registers();
            registers.Clear();
            memory = new RAM(0x10000);
            registers.PC = 0x0E00;
        }
        public void Run()
        {
            running = true;
            while (running)
            {
                Execute();
            }
        }

        public void Execute()
        {
            byte instruction = GetNextInstruction();
            switch (instruction)
            {
                #region NOP
                case 0xEA:
                    //NOP
                    break;
                #endregion

                #region LD*
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
                case 0xA1:
                    LDA_ZPIX();
                    break;
                case 0xB1:
                    LDA_ZPIY();
                    break;
                case 0xA2:
                    LDX_IM();
                    break;
                case 0xAE:
                    LDX_AB();
                    break;
                case 0xBE:
                    LDX_ABY();
                    break;
                case 0xA6:
                    LDX_ZP();
                    break;
                case 0xB6:
                    LDX_ZPY();
                    break;
                case 0xA0:
                    LDY_IM();
                    break;
                case 0xAC:
                    LDY_AB();
                    break;
                case 0xBC:
                    LDY_ABX();
                    break;
                case 0xA4:
                    LDY_ZP();
                    break;
                case 0xB4:
                    LDY_ZPX();
                    break;
                #endregion

                #region ST*
                case 0x8E:
                    STX_AB();
                    break;
                case 0x86:
                    STX_ZP();
                    break;
                case 0x96:
                    STX_ZPY();
                    break;
                case 0x8C:
                    STY_AB();
                    break;
                case 0x84:
                    STY_ZP();
                    break;
                case 0x94:
                    STY_ZPX();
                    break;
                case 0x8D:
                    STA_AB();
                    break;
                case 0x9D:
                    STA_ABX();
                    break;
                case 0x99:
                    STA_ABY();
                    break;
                case 0x85:
                    STA_ZP();
                    break;
                case 0x95:
                    STA_ZPX();
                    break;
                case 0x81:
                    STA_ZPIX();
                    break;
                case 0x91:
                    STA_ZPIY();
                    break;
                #endregion

                #region T**
                case 0xAA:
                    TAX();
                    break;
                case 0xA8:
                    TAY();
                    break;
                case 0xBA:
                    TSX();
                    break;
                case 0x8A:
                    TXA();
                    break;
                case 0x9A:
                    TXS();
                    break;
                case 0x98:
                    TYA();
                    break;
                #endregion

                #region SE*
                case 0x38:
                    SEC();
                    break;
                case 0xF8:
                    SED();
                    break;
                case 0x78:
                    SEI();
                    break;
                #endregion

                #region PH*
                case 0x48:
                    PHA();
                    break;
                case 0x08:
                    PHP();
                    break;
                #endregion

                #region PL*
                case 0x68:
                    PLA();
                    break;
                case 0x28:
                    PLP();
                    break;
                #endregion

                #region CL*
                case 0x18:
                    CLC();
                    break;
                case 0xD8:
                    CLD();
                    break;
                case 0x58:
                    CLI();
                    break;
                case 0xB8:
                    CLV();
                    break;

                #endregion

                default:
                    Debug.WriteLine("Instruction not handled " + instruction.ToString("x2"));
                    break;
            }
        }

        private byte GetNextInstruction()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            return value;
        }

        private ulong GetInstructionWord()
        {
            byte value1 = GetNextInstruction();
            byte value2 = GetNextInstruction();
            ulong value3 = (ulong)((value2 << 8) | value1);
            if (value3 > 65535) value3 = value3 - 65535;
            return value3;
        }

        #region Addressing Modes
        private byte Immediate()
        {
            byte addr = GetNextInstruction();
            return addr;
        }

        private byte Absolute()
        {
            ulong value = GetInstructionWord();
            byte addr = memory.ReadByte(value & 0xFFFF);
            return addr;
        }

        private byte X_Indexed_Absolute()
        {
            ulong value = (GetInstructionWord() + registers.X);
            byte addr = memory.ReadByte(value & 0xFFFF);
            return addr;
        }

        private byte Y_Indexed_Absolute()
        {
            ulong value = (GetInstructionWord() + registers.Y);
            byte addr = memory.ReadByte(value & 0xFFFF);
            return addr;
        }

        private byte Zero_Page()
        {
            byte value = GetNextInstruction();
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte X_Indexed_Zero_Page()
        {
            byte value = (byte)((GetNextInstruction() + registers.X) & 0xFF);
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte Y_Indexed_Zero_Page()
        {
            byte value = (byte)((GetNextInstruction() + registers.Y) & 0xFF);
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private ulong X_Indexed_Zero_Page_Indirect()
        {
            byte value = (byte)(GetNextInstruction() + registers.X);
            byte value1 = memory.ReadByte(value);
            if (value == 255) value = 0; else value++;
            byte value2 = memory.ReadByte(value);
            ulong addr = (ulong)((value2 << 8) | value1);
            return addr & 0xFFFF;
        }

        private ulong Zero_Page_Indirect_Y_Indexed()
        {
            byte value = GetNextInstruction();
            byte value1 = memory.ReadByte(value);
            if (value == 255) value = 0; else value++;
            byte value2 = memory.ReadByte(value);
            ulong value3 = (ulong)((value2 << 8) | value1);
            ulong addr = (ulong)(value3 + registers.Y);
            return addr & 0xFFFF;
        }

        private void Set_FlagsNZ(byte value)
        {
            registers.Flags.Z = (value == 0);
            registers.Flags.N = ((value & (1 << 7)) != 0);
        }
        #endregion

        #region LDA
        private void LDA_IM()
        {
            registers.A = Immediate();
            Set_FlagsNZ(registers.A);
        }

        private void LDA_AB()
        {
            registers.A = Absolute();
            Set_FlagsNZ(registers.A);
        }

        private void LDA_ABX()
        {
            registers.A = X_Indexed_Absolute();
            Set_FlagsNZ(registers.A);
        }

        private void LDA_ABY()
        {
            registers.A = Y_Indexed_Absolute();
            Set_FlagsNZ(registers.A);
        }

        private void LDA_ZP()
        {
            registers.A = Zero_Page();
            Set_FlagsNZ(registers.A);
        }

        private void LDA_ZPX()
        {
            registers.A = X_Indexed_Zero_Page();
            Set_FlagsNZ(registers.A);
        }

        private void LDA_ZPIX()
        {
            ulong value = X_Indexed_Zero_Page_Indirect();
            registers.A = memory.ReadByte(value);
            Set_FlagsNZ(registers.A);
        }

        private void LDA_ZPIY()
        {
            ulong value = Zero_Page_Indirect_Y_Indexed();
            registers.A = memory.ReadByte(value);
            Set_FlagsNZ(registers.A);
        }
        #endregion

        #region LDX
        private void LDX_IM()
        {
            registers.X = Immediate();
            Set_FlagsNZ(registers.X);
        }

        private void LDX_AB()
        {
            registers.X = Absolute();
            Set_FlagsNZ(registers.X);
        }

        private void LDX_ABY()
        {
            registers.X = Y_Indexed_Absolute();
            Set_FlagsNZ(registers.X);
        }

        private void LDX_ZP()
        {
            registers.X = Zero_Page();
            Set_FlagsNZ(registers.X);
        }

        private void LDX_ZPY()
        {
            registers.X = Y_Indexed_Zero_Page();
            Set_FlagsNZ(registers.X);
        }
        #endregion

        #region LDY
        private void LDY_IM()
        {
            registers.Y = Immediate();
            Set_FlagsNZ(registers.Y);
        }

        private void LDY_AB()
        {
            registers.Y = Absolute();
            Set_FlagsNZ(registers.Y);
        }

        private void LDY_ABX()
        {
            registers.Y = X_Indexed_Absolute();
            Set_FlagsNZ(registers.Y);
        }

        private void LDY_ZP()
        {
            registers.Y = Zero_Page();
            Set_FlagsNZ(registers.Y);
        }

        private void LDY_ZPX()
        {
            registers.Y = X_Indexed_Zero_Page();
            Set_FlagsNZ(registers.Y);
        }
        #endregion

        #region STA
        private void STA_AB()
        {
            memory.WriteByte(Absolute(), registers.A);
        }

        private void STA_ABX()
        {
            memory.WriteByte(X_Indexed_Absolute(), registers.A);
        }

        private void STA_ABY()
        {
            memory.WriteByte(Y_Indexed_Absolute(), registers.A);
        }

        private void STA_ZP()
        {
            memory.WriteByte(Zero_Page(), registers.A);
        }

        private void STA_ZPX()
        {
            memory.WriteByte(X_Indexed_Zero_Page(), registers.A);
        }

        private void STA_ZPIX()
        {
            memory.WriteByte(X_Indexed_Zero_Page_Indirect(), registers.A);
        }

        private void STA_ZPIY()
        {
            memory.WriteByte(Zero_Page_Indirect_Y_Indexed(), registers.A);
        }
        #endregion

        #region STX
        private void STX_AB()
        {
            memory.WriteByte(Absolute(), registers.X);
        }

        private void STX_ZP()
        {
            memory.WriteByte(Zero_Page(), registers.X);
        }

        private void STX_ZPY()
        {
            memory.WriteByte(Y_Indexed_Zero_Page(), registers.X);
        }
        #endregion

        #region STY
        private void STY_AB()
        {
            memory.WriteByte(Absolute(), registers.X);
        }

        private void STY_ZP()
        {
            memory.WriteByte(Zero_Page(), registers.X);
        }

        private void STY_ZPX()
        {
            memory.WriteByte(X_Indexed_Zero_Page(), registers.X);
        }
        #endregion

        #region T**
        private void TAX()
        {
            registers.X = registers.A;
            Set_FlagsNZ(registers.X);
        }

        private void TAY()
        {
            registers.Y = registers.A;
            Set_FlagsNZ(registers.Y);
        }

        private void TSX()
        {
            registers.X = registers.S;
            Set_FlagsNZ(registers.X);
        }

        private void TXA()
        {
            registers.A = registers.X;
            Set_FlagsNZ(registers.A);
        }

        private void TXS()
        {
            registers.S = registers.X;
        }

        private void TYA()
        {
            registers.A = registers.Y;
            Set_FlagsNZ(registers.A);
        }
        #endregion

        #region SE*
        private void SEC()
        {
            registers.Flags.C = true;
        }

        private void SED()
        {
            registers.Flags.D = true;
        }

        private void SEI()
        {
            registers.Flags.I = true;
        }
        #endregion

        #region PH*
        private void PHA()
        {
            memory.WriteByte(registers.S, registers.A);
            registers.S--;
        }

        private void PHP()
        {
            memory.WriteByte(registers.S, registers.P);
            registers.S--;
        }
        #endregion

        #region PL*
        private void PLA()
        {
            registers.S++;
            registers.A = memory.ReadByte((ushort)(registers.S | 0x0100));
            Set_FlagsNZ(registers.A);
        }

        private void PLP()
        {
            registers.S++;
            byte value = memory.ReadByte((ushort)(registers.S | 0x0100));
            registers.Flags.SetFlagsFromByte(value, 0xCF); //ignore bits 5 & 6
        }
        #endregion

        #region CL*
        private void CLC()
        {
            registers.Flags.C = false;
        }
        private void CLD()
        {
            registers.Flags.D = false;
        }
        private void CLI()
        {
            registers.Flags.I = false;
        }
        private void CLV()
        {
            registers.Flags.V = false;
        }
        #endregion
    }
}
