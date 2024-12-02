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

        public _6502_CPU()
        {
            Initialise();
        }

        public void Initialise()
        {
            registers = new Registers();
            registers.Clear();
            memory = new RAM(0x10000);
            registers.PC = 0xE00;
            registers.S = 0x1FF;
        }

        public void Execute(bool test = true)
        {

            while (test)
            {
                byte instruction = GetNextByte();
                switch (instruction)
                {
                    #region LDA
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
                    #endregion

                    #region LDX
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
                    #endregion

                    #region LDY
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

                    #region STX
                    case 0x8E:
                        STX_AB();
                        break;
                    case 0x86:
                        STX_ZP();
                        break;
                    case 0x96:
                        STX_ZPY();
                        break;
                    #endregion

                    #region STY
                    case 0x8C:
                        STY_AB();
                        break;
                    case 0x84:
                        STY_ZP();
                        break;
                    case 0x94:
                        STY_ZPX();
                        break;
                    #endregion

                    #region STA
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

                    default:
                        Debug.WriteLine("Instruction not handled " + instruction.ToString("x2"));
                        break;
                }
                test = false;
            }
        }

        #region Addressing Modes
        private byte Immediate()
        {
            byte addr = GetNextByte();
            return addr;
        }

        private byte Absolute()
        {
            ulong value = GetNextWord();
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte X_Indexed_Absolute()
        {
            ulong value = GetNextWord() + registers.X;
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte Y_Indexed_Absolute()
        {
            ulong value = GetNextWord() + registers.Y;
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte Zero_Page()
        {
            byte value = GetNextByte();
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte X_Indexed_Zero_Page()
        {
            byte value = (byte)((GetNextByte() + registers.X) & 0xFF);
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private byte Y_Indexed_Zero_Page()
        {
            byte value = (byte)((GetNextByte() + registers.Y) & 0xFF);
            byte addr = memory.ReadByte(value);
            return addr;
        }

        private ulong X_Indexed_Zero_Page_Indirect()
        {
            byte a = (byte)(memory.ReadByte(registers.PC) + registers.X);
            byte lo = memory.ReadByte(a);
            byte hi = memory.ReadByte((byte)(a + 1));
            ulong addr = (ulong)((hi << 8) | lo);
            registers.PC++;
            return addr;
        }

        private ulong Zero_Page_Indirect_Y_Indexed()
        {
            byte a = memory.ReadByte(registers.PC);
            ulong addr = (ulong)(memory.ReadWord(a) + registers.Y);
            registers.PC++;
            return addr;
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
            //memory.WriteByte(X_Indexed_Zero_Page_Indirect(), registers.A);
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

        private byte GetNextByte()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            return value;
        }

        private ulong GetNextWord()
        {
            byte value1 = GetNextByte();
            byte value2 = GetNextByte();
            return (ulong)((value2 << 8) | value1);
        }

    }
}
