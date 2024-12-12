using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace _6502CPU
{
    public class _6502_CPU
    {
        public Registers registers = new Registers();

        public RAM memory = new RAM(0x10000);

        private bool running = true;
        public bool Running { get; set; }

        private byte nextOpcode;

        private int cyclecount = 0;

        private byte lastOpcode;

        public bool TriggerNmi { get; set; }

        public bool TriggerIRQ { get; private set; }

        private bool _previousInterrupt;

        private bool _interrupt;

        public _6502_CPU()
        {
            Initialise();
        }

        public void Initialise()
        {
            registers = new Registers();
            registers.Clear();
            memory = new RAM(0x10000);
            memory.WriteByte(0x01, 0x1F);
            registers.S = 0xFF;
            registers.Flags.I = true;
            TriggerNmi = false;
            TriggerIRQ = false;
            registers.Y = 0x0A;
            registers.P = 0xA5;
        }

        public void Run()
        {
            registers.PC = memory.ReadWord(0xFFFC);
            running = true;
            int D012Ctr = 0;
            while (running)
            {
                cyclecount++;
                nextOpcode = GetNextInstruction();
                Execute(nextOpcode);
                if (_previousInterrupt)
                {
                    if (TriggerNmi)
                    {
                        ProcessNMI();
                        TriggerNmi = false;
                    }
                    else if (TriggerIRQ)
                    {
                        ProcessIRQ();
                        TriggerIRQ = false;
                    }
                }
                _previousInterrupt = _interrupt;
                _interrupt = TriggerNmi || (TriggerIRQ && !registers.Flags.I);
                memory.WriteByte(0xD012, (byte)D012Ctr);
                D012Ctr++;
                if (D012Ctr > 256) D012Ctr = 0;
            }
        }

        public void InterruptRequest()
        {
            TriggerIRQ = true;
        }

        public void Execute(byte opcode)
        {        
            switch (opcode)
            {
                #region Documented Opcodes

                #region NOP
                case 0xEA:
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

                #region J**
                case 0x4C:
                    JMPA();
                    break;
                case 0x6C:
                    JMPAI();
                    break;
                case 0x20:
                    JSRA();
                    break;
                #endregion

                #region RT*
                case 0x40:
                    RTI();
                    break;
                case 0x60:
                    RTS();
                    break;
                #endregion

                #endregion

                default:
                    Debug.WriteLine("Instruction not handled " + opcode.ToString("x2"));
                    break;
            }
            lastOpcode = opcode;
        }

        public byte GetNextInstruction()
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
            return value3 & 0xFFFF;
        }

        private void StackPush(byte value)
        {
            memory.WriteByte((ulong)(registers.S + 0x100), value);
            registers.S--;
        }

        private byte StackPop()
        {
            registers.S++;
            return memory.ReadByte((ulong)(registers.S + 0x100));
        }

        private void ProcessNMI()
        {
            registers.PC--;
            Break(false, 0xFFFA);
            nextOpcode = GetNextInstruction();
            Execute(nextOpcode);
        }

        private void ProcessIRQ()
        {
            if (registers.Flags.I)
                return;
            registers.PC--;
            Break(false, 0xFFFE);
            nextOpcode = GetNextInstruction();
            Execute(nextOpcode);
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
        private ulong AbsoluteIndirect()
        {
            ulong addr = Absolute();
            byte lo;
            byte hi;
            if ((addr & 0x00FF) == 0xFF)
            {
                lo = memory.ReadByte((addr & 0xFF00) + 0xFF);
                hi = memory.ReadByte((addr & 0xFF00));
            }
            else
            {
                lo = memory.ReadByte(addr);
                hi = memory.ReadByte((addr + 1));
            }
            ulong value = (ulong)((hi << 8) | lo);
            return value & 0xFFFF;
        }
        private ulong X_Indexed_Absolute()
        {
            ulong addr = (Absolute() + registers.X);
            return addr & 0xFFFF;
        }
        private ulong Y_Indexed_Absolute()
        {
            ulong addr = (Absolute() + registers.Y);
            return addr & 0xFFFF;
        }
        private byte Zero_Page()
        {
            byte addr = GetNextInstruction();
            return addr;
        }
        private byte X_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.X) & 0xFF);
            return addr;
        }
        private byte Y_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.Y) & 0xFF);
            return addr;
        }
        private ulong X_Indexed_Zero_Page_Indirect()
        {
            byte value = (byte)(GetNextInstruction() + registers.X);

            byte value1 = memory.ReadByte(value);
            byte value2 = (byte)(memory.ReadByte(value += 1) & 0xFF);
            ulong addr = (ulong)((value2 << 8) | value1);
            return addr & 0xFFFF;
        }
        private ulong Zero_Page_Indirect_Y_Indexed()
        {
            byte value = GetNextInstruction();
            byte value1 = memory.ReadByte(value);
            byte value2 = (byte)(memory.ReadByte(value += 1) & 0xFF);
            ulong value3 = (ulong)((value2 << 8) | value1);
            ulong addr = value3 + registers.Y;
            return addr & 0xFFFF;
        }
        private void Set_FlagsNZ(byte value)
        {
            registers.Flags.Z = (value == 0);
            registers.Flags.N = ((value & (1 << 7)) != 0);
        }
        #endregion

        #region Documented Opcodes

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
            memory.WriteByte(Absolute(), registers.Y);
        }
        private void STY_ZP()
        {
            memory.WriteByte(Zero_Page(), registers.Y);
        }
        private void STY_ZPX()
        {
            memory.WriteByte(X_Indexed_Zero_Page(), registers.Y);
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
            StackPush(registers.A);
        }
        private void PHP()
        {
            byte addr = registers.Flags.GetFlagsAsByte();
            addr = (byte)(addr | (1 << 4));
            addr = (byte)(addr | (1 << 5));
            StackPush(addr);
        }
        #endregion

        #region PL*
        private void PLA()
        {
            registers.A = StackPop();
            Set_FlagsNZ(registers.A);
        }
        private void PLP()
        {
            byte value = StackPop();
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
            value1 = (byte)(value1 & 0xFF);
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INCXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value1 = memory.ReadByte(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INCZP()
        {
            ulong addr = Zero_Page();
            byte value1 = memory.ReadByte(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
            memory.WriteByte(addr, value1);
            Set_FlagsNZ(value1);
        }
        private void INCXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = memory.ReadByte(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
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
            int carry = registers.Flags.C ? 1 : 0;

            if (registers.Flags.D)
            {
                int low = (registers.A & 0xF) + (value & 0xF) + (registers.Flags.C ? 0x1 : 0);
                bool halfCarry = (low > 0x9);
                int high = (registers.A & 0xF0) + (value & 0xF0) + (halfCarry ? 0x10 : 0);
                registers.Flags.C = (high > 0x9F);
                byte value2 = (byte)((low & 0xF) + (high & 0xF0));
                if (halfCarry)
                    low += 0x6;
                if (registers.Flags.C)
                    high += 0x60;
                registers.Flags.V = ((registers.A ^ value2) & (value ^ value2) & 0x80) != 0;
                registers.A = (byte)((low & 0xF) + (high & 0xF0));
                Set_FlagsNZ(value2);
            }
            else
            {
                int value2 = registers.A + value + carry;
                registers.Flags.V = (((registers.A ^ value2) & 0x80) != 0) && (((registers.A ^ value) & 0x80) == 0);
                registers.Flags.C = value2 > 0xFF;
                registers.A = (byte)(value2);
                Set_FlagsNZ(registers.A);
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
            int carry = registers.Flags.C ? 1 : 0;
            if (registers.Flags.D)
            {
                int low = 0xF + (registers.A & 0xF) - (value & 0xF) + (registers.Flags.C ? 0x1 : 0);
                bool halfCarry = (low > 0xF);
                int high = 0xF0 + (registers.A & 0xF0) - (value & 0xF0) + (halfCarry ? 0x10 : 0);
                registers.Flags.C = (high > 0xFF);
                byte binary = (byte)((low & 0xF) + (high & 0xF0));
                if (!halfCarry)
                    low -= 0x6;
                if (!registers.Flags.C)
                    high -= 0x60;
                registers.Flags.V = ((registers.A ^ binary) & (~value ^ binary) & 0x80) != 0;
                registers.A = (byte)((low & 0xF) + (high & 0xF0));
                Set_FlagsNZ(binary);
           }
            else
            {
                int value2 = 0xFF + registers.A - value + carry;
                registers.Flags.V = ((registers.A ^ value2) & (~value ^ value2) & 0x80) != 0;
                registers.Flags.C = value2 > 0xFF;
                registers.A = (byte)(value2);
                Set_FlagsNZ(registers.A);
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
            ulong addr = Absolute();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        private void ASLXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        private void ASLZ()
        {
            ulong addr = Zero_Page();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        private void ASLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
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
            ulong addr = Absolute();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        private void LSRXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        private void LSRZ()
        {
            ulong addr = Zero_Page();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        private void LSRXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = memory.ReadByte(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            memory.WriteByte(addr, value2);
        }
        #endregion

        #region ROL
        private void ROLAC()
        {
            byte addr = registers.A;
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value = (byte)((addr << 1) + carry);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            Set_FlagsNZ(value);
            registers.A = (byte)value;
        }
        private void ROLA()
        {
            ulong addr = Absolute();
            byte value = memory.ReadByte(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        private void ROLXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value = memory.ReadByte(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        private void ROLZ()
        {
            ulong addr = Zero_Page();
            byte value = memory.ReadByte(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        private void ROLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = memory.ReadByte(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        #endregion

        #region ROR
        private void RORAC()
        {
            byte addr = registers.A;
            byte value = (byte)(addr >> 1);
            if (registers.Flags.C)
                value += 0x80;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            Set_FlagsNZ(value);
            registers.A = (byte)value;
        }
        private void RORA()
        {
            ulong addr = Absolute();
            byte value = memory.ReadByte(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        private void RORXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value = memory.ReadByte(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        private void RORZ()
        {
            ulong addr = Zero_Page();
            byte value = memory.ReadByte(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        private void RORXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = memory.ReadByte(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            memory.WriteByte(addr, value2);
        }
        #endregion

        #region BRANCH
        private void BCC()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (!registers.Flags.C)
                Branch(value);
        }
        private void BCS()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (registers.Flags.C)
                Branch(value);
        }
        private void BEQ()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (registers.Flags.Z)
                Branch(value);
        }
        private void BMI()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (registers.Flags.N)
                Branch(value);
        }
        private void BNE()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (!registers.Flags.Z)
                Branch(value);
        }
        private void BPL()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (!registers.Flags.N)
                Branch(value);
        }
        private void BVC()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (!registers.Flags.V)
                Branch(value);
        }
        private void BVS()
        {
            byte value = memory.ReadByte(registers.PC);
            registers.IncPC();
            if (registers.Flags.V)
                Branch(value);
        }
        private void BRK()
        {
            Break(true, 0xFFFE);
        }
        private void Branch(ulong value)
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
        private void Break(bool isBreak, ulong vector)
        {
            registers.IncPC();
            StackPush((byte)(((registers.PC) >> 8) & 0xFF));
            StackPush((byte)((registers.PC) & 0xFF));
            if (isBreak)
            {
                registers.Flags.B = false;
                StackPush((byte)(registers.Flags.GetFlagsAsByte() | 0x10));
            }
            else
            {
                registers.Flags.B = true;
                StackPush((byte)(registers.Flags.GetFlagsAsByte()));
            }
            registers.Flags.I = true;
            registers.PC = (ulong)((memory.ReadByte(vector + 1) << 8) | memory.ReadByte(vector));
        }
        #endregion

        #region J**
        private void JMPA()
        {
            ulong value = Absolute();
            registers.PC = value;
        }
        private void JMPAI()
        {
            ulong addr = AbsoluteIndirect();
            registers.PC = addr;
        }
        private void JSRA()
        {
            byte pclo = memory.ReadByte(registers.PC);
            registers.PC++;
            byte hi = (byte)(((registers.PC) >> 8) & 0xFF);
            StackPush(hi);
            byte lo = (byte)((registers.PC) & 0xFF);
            StackPush(lo);
            byte pchi = memory.ReadByte(registers.PC);
            registers.PC = (ulong)((pchi << 8) | pclo);
        }
        #endregion

        #region RT*
        private void RTI()
        {
            byte flags = StackPop();
            byte lo = StackPop();
            byte hi = StackPop();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.Flags.SetFlagsFromByte(flags, 0b11001111);
        }
        private void RTS()
        {
            byte lo = StackPop();
            byte hi = StackPop();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.PC++;
        }
        #endregion

        #endregion

    }
}
