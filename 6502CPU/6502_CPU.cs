using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

// https://www.masswerk.at/6502/6502_instruction_set.html

// https://www.pagetable.com/c64ref/6502/?tab=2

// https://github.com/aaronmell/6502Net/blob/master/Processor/Processor.cs

namespace _6502CPU
{
    public class _6502_CPU
    {
        public Registers registers = new Registers();

        public RAM memory = new RAM(0x10000);

        private bool running = true;
        public bool Running { get; set; }

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

                #region DE*
                case 0xCE:
                    DECA();
                    break;
                case 0xDE:
                    DECXA();
                    break;
                case 0xC6:
                    DECZP();
                    break;
                case 0xD6:
                    DECXZP();
                    break;
                case 0xCA:
                    DEX();
                    break;
                case 0x88:
                    DEY();
                    break;
                #endregion

                #region IN*
                case 0xEE:
                    INCA();
                    break;
                case 0xFE:
                    INCXA();
                    break;
                case 0xE6:
                    INCZP();
                    break;
                case 0xF6:
                    INCXZP();
                    break;
                case 0xE8:
                    INX();
                    break;
                case 0xC8:
                    INY();
                    break;
                #endregion

                #region CM*
                case 0xC9:
                    CMPI();
                    break;
                case 0xCD:
                    CMPA();
                    break;
                case 0xDD:
                    CMPXA();
                    break;
                case 0xD9:
                    CMPYA();
                    break;
                case 0xC5:
                    CMPZ();
                    break;
                case 0xD5:
                    CMPXZ();
                    break;
                case 0xC1:
                    CMPXZI();
                    break;
                case 0xD1:
                    CMPYZI();
                    break;
                #endregion

                #region CPX
                case 0xE0:
                    CPXI();
                    break;
                case 0xEC:
                    CPXA();
                    break;
                case 0xE4:
                    CPXZ();
                    break;
                #endregion

                #region CPY
                case 0xC0:
                    CPYI();
                    break;
                case 0xCC:
                    CPYA();
                    break;
                case 0xC4:
                    CPYZ();
                    break;
                #endregion

                #region ADC
                case 0x69:
                    ADCI();
                    break;
                case 0x6D:
                    ADCA();
                    break;
                case 0x7D:
                    ADCXA();
                    break;
                case 0x79:
                    ADCYA();
                    break;
                case 0x65:
                    ADCZ();
                    break;
                case 0x75:
                    ADCXZ();
                    break;
                case 0x61:
                    ADCXZI();
                    break;
                case 0x71:
                    ADCYZI();
                    break;
                #endregion

                #region SBC
                case 0xE9:
                    SBCI();
                    break;
                case 0xED:
                    SBCA();
                    break;
                case 0xFD:
                    SBCXA();
                    break;
                case 0xF9:
                    SBCYA();
                    break;
                case 0xE5:
                    SBCZ();
                    break;
                case 0xF5:
                    SBCXZ();
                    break;
                case 0xE1:
                    SBCXZI();
                    break;
                case 0xF1:
                    SBCYZI();
                    break;
                #endregion

                #region EOR
                case 0x49:
                    EORI();
                    break;
                case 0x4D:
                    EORA();
                    break;
                case 0x5D:
                    EORXA();
                    break;
                case 0x59:
                    EORYA();
                    break;
                case 0x45:
                    EORZ();
                    break;
                case 0x55:
                    EORXZ();
                    break;
                case 0x41:
                    EORXZI();
                    break;
                case 0x51:
                    EORYZI();
                    break;
                #endregion

                #region ORA
                case 0x09:
                    ORAI();
                    break;
                case 0x0D:
                    ORAA();
                    break;
                case 0x1D:
                    ORAXA();
                    break;
                case 0x19:
                    ORAYA();
                    break;
                case 0x05:
                    ORAZ();
                    break;
                case 0x15:
                    ORAXZ();
                    break;
                case 0x01:
                    ORAXZI();
                    break;
                case 0x11:
                    ORAYZI();
                    break;
                #endregion

                #region AND
                case 0x29:
                    ANDI();
                    break;
                case 0x2D:
                    ANDA();
                    break;
                case 0x3D:
                    ANDXA();
                    break;
                case 0x39:
                    ANDYA();
                    break;
                case 0x25:
                    ANDZ();
                    break;
                case 0x35:
                    ANDXZ();
                    break;
                case 0x21:
                    ANDXZI();
                    break;
                case 0x31:
                    ANDYZI();
                    break;
                #endregion

                #region BIT
                case 0x2C:
                    BITA();
                    break;
                case 0x24:
                    BITZ();
                    break;
                #endregion

                #region ASL
                case 0x0A:
                    ASLAC();
                    break;
                case 0x0E:
                    ASLA();
                    break;
                case 0x1E:
                    ASLXA();
                    break;
                case 0x06:
                    ASLZ();
                    break;
                case 0x16:
                    ASLXZ();
                    break;
                #endregion

                #region LSR
                case 0x4A:
                    LSRAC();
                    break;
                case 0x4E:
                    LSRA();
                    break;
                case 0x5E:
                    LSRXA();
                    break;
                case 0x46:
                    LSRZ();
                    break;
                case 0x56:
                    LSRXZ();
                    break;
                #endregion

                #region ROL
                case 0x2A:
                    ROLAC();
                    break;
                case 0x2E:
                    ROLA();
                    break;
                case 0x3E:
                    ROLXA();
                    break;
                case 0x26:
                    ROLZ();
                    break;
                case 0x36:
                    ROLXZ();
                    break;
                #endregion

                #region ROR

                case 0x6A:
                    RORAC();
                    break;
                case 0x6E:
                    RORA();
                    break;
                case 0x7E:
                    RORXA();
                    break;
                case 0x66:
                    RORZ();
                    break;
                case 0x76:
                    RORXZ();
                    break;
                #endregion

                #region BRANCH
                case 0x90:
                    BCC();
                    break;
                case 0xB0:
                    BCS();
                    break;
                case 0xF0:
                    BEQ();
                    break;
                case 0x30:
                    BMI();
                    break;
                case 0xd0:
                    BNE();
                    break;
                case 0x10:
                    BPL();
                    break;
                case 0x50:
                    BVC();
                    break;
                case 0x70:
                    BVS();
                    break;
                case 0x00:
                    BRK();
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
        private ulong Absolute()
        {
            ulong addr = GetInstructionWord();
            return addr & 0xFFFF;
        }
        private ulong X_Indexed_Absolute()
        {
            ulong addr = (GetInstructionWord() + registers.X);
            return addr & 0xFFFF;
        }
        private ulong Y_Indexed_Absolute()
        {
            ulong addr = (GetInstructionWord() + registers.Y);
            return addr & 0xFFFF;
        }
        private byte Zero_Page()
        {
            byte addr = GetNextInstruction();
            return addr;
        }
        private byte X_Indexed_Zero_Page()
        {
            byte addr = (byte)((GetNextInstruction() + registers.X) & 0xFF);
            return addr;
        }
        private byte Y_Indexed_Zero_Page()
        {
            byte addr = (byte)((GetNextInstruction() + registers.Y) & 0xFF);
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

        #region LD*
        private void LDA_IM()
        {
            registers.A = Immediate();
            Set_FlagsNZ(registers.A);
        }
        private void LDA_AB()
        {
            registers.A = memory.ReadByte(Absolute());
            Set_FlagsNZ(registers.A);
        }
        private void LDA_ABX()
        {
            registers.A = memory.ReadByte(X_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
        }
        private void LDA_ABY()
        {
            registers.A = memory.ReadByte(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
        }
        private void LDA_ZP()
        {
            registers.A = memory.ReadByte(Zero_Page());
            Set_FlagsNZ(registers.A);
        }
        private void LDA_ZPX()
        {
            registers.A = memory.ReadByte(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.A);
        }
        private void LDA_ZPIX()
        {
            registers.A = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            Set_FlagsNZ(registers.A);
        }
        private void LDA_ZPIY()
        {
            registers.A = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            Set_FlagsNZ(registers.A);
        }
        private void LDX_IM()
        {
            registers.X = Immediate();
            Set_FlagsNZ(registers.X);
        }
        private void LDX_AB()
        {
            registers.X = memory.ReadByte(Absolute());
            Set_FlagsNZ(registers.X);
        }
        private void LDX_ABY()
        {
            registers.X = memory.ReadByte(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.X);
        }
        private void LDX_ZP()
        {
            registers.X = memory.ReadByte(Zero_Page());
            Set_FlagsNZ(registers.X);
        }
        private void LDX_ZPY()
        {
            registers.X = memory.ReadByte(Y_Indexed_Zero_Page());
            Set_FlagsNZ(registers.X);
        }
        private void LDY_IM()
        {
            registers.Y = Immediate();
            Set_FlagsNZ(registers.Y);
        }
        private void LDY_AB()
        {
            registers.Y = memory.ReadByte(Absolute());
            Set_FlagsNZ(registers.Y);
        }
        private void LDY_ABX()
        {
            registers.Y = memory.ReadByte(X_Indexed_Absolute());
            Set_FlagsNZ(registers.Y);
        }
        private void LDY_ZP()
        {
            registers.Y = memory.ReadByte(Zero_Page());
            Set_FlagsNZ(registers.Y);
        }
        private void LDY_ZPX()
        {
            registers.Y = memory.ReadByte(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.Y);
        }
        #endregion

        #region ST*
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

        #region DE*
        private void DECA()
        {
            ulong addr = Absolute();
            byte value1 = memory.ReadByte(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            memory.WriteByte(addr, value2);
            Set_FlagsNZ(value2);
        }
        private void DECXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value1 = memory.ReadByte(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            memory.WriteByte(addr, value2);
            Set_FlagsNZ(value2);
        }
        private void DECZP()
        {
            ulong addr = Zero_Page();
            byte value1 = memory.ReadByte(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            memory.WriteByte(addr, value2);
            Set_FlagsNZ(value2);
        }
        private void DECXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = memory.ReadByte(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            memory.WriteByte(addr, value2);
            Set_FlagsNZ(value2);
        }
        private void DEX()
        {
            byte value1 = registers.X;
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            registers.X = value2;
            Set_FlagsNZ(value2);
        }
        private void DEY()
        {
            byte value1 = registers.Y;
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            if (value2 < 0) value2 = (byte)(0xFF - value2);
            registers.Y = value2;
            Set_FlagsNZ(value2);
        }
        #endregion

        #region IN*
        private void INCA()
        {
            ulong addr = Absolute();
            byte value1 = memory.ReadByte(addr);
            value1++;
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INCXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value1 = memory.ReadByte(addr);
            value1++;
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INCZP()
        {
            ulong addr = Zero_Page();
            byte value1 = memory.ReadByte(addr);
            value1++;
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INCXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = memory.ReadByte(addr);
            value1++;
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INX()
        {
            byte value1 = (byte)(registers.X + 1);
            if (value1 < 0) value1 = (byte)(0xFF - value1);
            registers.X = value1;
            Set_FlagsNZ(value1);
        }
        private void INY()
        {
            byte value1 = (byte)(registers.Y + 1);
            if (value1 < 0) value1 = (byte)(0xFF - value1);
            registers.Y = value1;
            Set_FlagsNZ(value1);
        }
        #endregion

        #region CM*
        private void CMPI()
        {
            byte addr = Immediate();
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPYA()
        {
            byte addr = memory.ReadByte(Y_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPXZI()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        private void CMPYZI()
        {
            byte addr = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
        }
        #endregion

        #region CPX
        private void CPXI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
        }
        private void CPXA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
        }
        private void CPXZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
        }
        #endregion

        #region CPY
        private void CPYI()
        {
            byte addr = Immediate();
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
        }
        private void CPYA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
        }
        private void CPYZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
        }
        #endregion

        #region ADC
        private void ADCI()
        {
            byte value = Immediate();
            ADC(value);
        }
        private void ADCA()
        {
            byte value = memory.ReadByte(Absolute());
            ADC(value);
        }
        private void ADCXA()
        {
            byte value = memory.ReadByte(X_Indexed_Absolute());
            ADC(value);
        }
        private void ADCYA()
        {
            byte value = memory.ReadByte(Y_Indexed_Absolute());
            ADC(value);
        }
        private void ADCZ()
        {
            byte value = memory.ReadByte(Zero_Page());
            ADC(value);
        }
        private void ADCXZ()
        {
            byte value = memory.ReadByte(X_Indexed_Zero_Page());
            ADC(value);
        }
        private void ADCXZI()
        {
            byte value = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            ADC(value);
        }
        private void ADCYZI()
        {
            byte value = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            ADC(value);
        }
        private void ADC(byte value)
        {
            if (registers.Flags.D)
            {
                int low = (registers.A & 0xF) + (value & 0xF) + (registers.Flags.C ? 0x1 : 0);
                bool halfCarry = (low > 0x9);
                int high = (registers.A & 0xF0) + (value & 0xF0) + (halfCarry ? 0x10 : 0);
                registers.Flags.C = (high > 0x9F);
                byte binary = (byte)((low & 0xF) + (high & 0xF0));
                Set_FlagsNZ(binary);
                registers.Flags.V = ((registers.A ^ binary) & (value ^ binary) & 0x80) != 0;
                if (halfCarry)
                    low += 0x6;
                if (registers.Flags.C)
                    high += 0x60;
                registers.A = (byte)((low & 0xF) + (high & 0xF0));
            }
            else
            {
                int carry = registers.Flags.C ? 1 : 0;
                int value2 = registers.A + value + carry;
                registers.Flags.V = (((registers.A ^ value2) & 0x80) != 0) && (((registers.A ^ value) & 0x80) == 0);
                registers.Flags.C = value2 > 0xFF;
                Set_FlagsNZ((byte)value2);
                registers.A = (byte)(value2);
            }
        }
        #endregion

        #region SBC
        private void SBCI()
        {
            byte value = Immediate();
            SBC(value);
        }
        private void SBCA()
        {
            byte value = memory.ReadByte(Absolute());
            SBC(value);
        }
        private void SBCXA()
        {
            byte value = memory.ReadByte(X_Indexed_Absolute());
            SBC(value);
        }
        private void SBCYA()
        {
            byte value = memory.ReadByte(Y_Indexed_Absolute());
            SBC(value);
        }
        private void SBCZ()
        {
            byte value = memory.ReadByte(Zero_Page());
            SBC(value);
        }
        private void SBCXZ()
        {
            byte value = memory.ReadByte(X_Indexed_Zero_Page());
            SBC(value);
        }
        private void SBCXZI()
        {
            byte value = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            SBC(value);
        }
        private void SBCYZI()
        {
            byte value = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            SBC(value);
        }
        private void SBC(byte value)
        {
            if (registers.Flags.D)
            {
                int low = 0xF + (registers.A & 0xF) - (value & 0xF) + (registers.Flags.C ? 0x1 : 0);
                bool halfCarry = (low > 0xF);
                int high = 0xF0 + (registers.A & 0xF0) - (value & 0xF0) + (halfCarry ? 0x10 : 0);
                registers.Flags.C = (high > 0xFF);
                byte binary = (byte)((low & 0xF) + (high & 0xF0));
                Set_FlagsNZ(binary);
                registers.Flags.V = ((registers.A ^ binary) & (~value ^ binary) & 0x80) != 0;
                if (!halfCarry)
                    low -= 0x6;
                if (!registers.Flags.C)
                    high -= 0x60;
                registers.A = (byte)((low & 0xF) + (high & 0xF0));
            }
            else
            {
                int carry = registers.Flags.C ? 1 : 0;
                int value2 = 0xFF + registers.A - value + carry;
                registers.Flags.V = ((registers.A ^ value2) & (~value ^ value2) & 0x80) != 0;
                registers.Flags.C = value2 > 0xFF;
                Set_FlagsNZ((byte)value2);
                registers.A = (byte)(value2);
            }
        }
        #endregion

        #region EOR
        private void EORI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORYA()
        {
            byte addr = memory.ReadByte(Y_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORXZI()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void EORYZI()
        {
            byte addr = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        #endregion

        #region ORA
        private void ORAI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAYA()
        {
            byte addr = memory.ReadByte(Y_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAXZI()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ORAYZI()
        {
            byte addr = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        #endregion

        #region AND
        private void ANDI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDYA()
        {
            byte addr = memory.ReadByte(Y_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDXZI()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        private void ANDYZI()
        {
            byte addr = memory.ReadByte(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
        }
        #endregion

        #region BIT
        private void BITA()
        {
            byte addr = memory.ReadByte(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
        }
        private void BITZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
        }
        #endregion

        #region ASL
        private void ASLAC()
        {
            byte addr = registers.A;
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-0 ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            registers.A = (byte)value;
        }
        private void ASLA()
        {
            byte addr = memory.ReadByte(Absolute());
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-0 ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void ASLXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-0 ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void ASLZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-0 ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void ASLXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-0 ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        #endregion

        #region LSR
        private void LSRAC()
        {
            byte addr = registers.A;
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-0 ^ value) & (1 << 7));
            registers.Flags.Z = (value == 0);
            registers.A = (byte)value;
        }
        private void LSRA()
        {
            byte addr = memory.ReadByte(Absolute());
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-0 ^ value) & (1 << 7));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void LSRXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-0 ^ value) & (1 << 7));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void LSRZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-0 ^ value) & (1 << 7));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void LSRXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-0 ^ value) & (1 << 7));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        #endregion

        #region ROL
        private void ROLAC()
        {
            byte addr = registers.A;
            int carry = registers.Flags.C == true ? 1 : 0;
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-carry ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            registers.A = (byte)value;
        }
        private void ROLA()
        {
            byte addr = memory.ReadByte(Absolute());
            int carry = registers.Flags.C == true ? 1 : 0;
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-carry ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void ROLXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            int carry = registers.Flags.C == true ? 1 : 0;
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-carry ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void ROLZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            int carry = registers.Flags.C == true ? 1 : 0;
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-carry ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void ROLXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            int carry = registers.Flags.C == true ? 1 : 0;
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            byte value = (byte)(addr << 1);
            value ^= (byte)((-carry ^ value) & (1 << 0));
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        #endregion

        #region ROR
        private void RORAC()
        {
            byte addr = registers.A;
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-carry ^ value) & (1 << 7));
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            registers.Flags.N = carry == 1;
            registers.Flags.Z = (value == 0);
            registers.A = (byte)value;
        }
        private void RORA()
        {
            byte addr = memory.ReadByte(Absolute());
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-carry ^ value) & (1 << 7));
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            registers.Flags.N = carry == 1;
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr,value);
        }
        private void RORXA()
        {
            byte addr = memory.ReadByte(X_Indexed_Absolute());
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-carry ^ value) & (1 << 7));
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            registers.Flags.N = carry == 1;
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void RORZ()
        {
            byte addr = memory.ReadByte(Zero_Page());
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-carry ^ value) & (1 << 7));
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            registers.Flags.N = carry == 1;
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        private void RORXZ()
        {
            byte addr = memory.ReadByte(X_Indexed_Zero_Page());
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)(addr >> 1);
            value ^= (byte)((-carry ^ value) & (1 << 7));
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            registers.Flags.N = carry == 1;
            registers.Flags.Z = (value == 0);
            memory.WriteByte(addr, value);
        }
        #endregion

        #region BRANCH
        private void BCC()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (!registers.Flags.C)
                BranchTo(value);
        }

        private void BCS()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (registers.Flags.C)
                BranchTo(value);
        }

        private void BEQ()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (registers.Flags.Z)
                BranchTo(value);
        }

        private void BMI()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (registers.Flags.N)
                BranchTo(value);
        }

        private void BNE()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (!registers.Flags.Z)
                BranchTo(value);
        }

        private void BPL()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (!registers.Flags.N)
                BranchTo(value);
        }

        private void BVC()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (!registers.Flags.V)
                BranchTo(value);
        }

        private void BVS()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.PC++;
            if (registers.Flags.V)
                BranchTo(value);
        }

        private void BRK()
        {
            // not implemented yet
        }

        private void BranchTo(ulong value)
        {
            if ((value & 0x80) == 0)
                registers.PC = (registers.PC + value) & 0xFFFF;
            else
            {
                int x = (byte)(~(value - 0x01)) * -1;
                int y = (int)registers.PC;
                y += x;
                registers.PC = (ulong)y & 0xFFFF;
            }
        }
        #endregion

    }
}
