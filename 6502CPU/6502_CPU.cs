using System.Diagnostics;

namespace _6502CPU
{
    public class _6502_CPU
    {
        public Registers registers = new Registers();

        public Memory memory = new Memory(0x10000);

        private bool running = true;
        public bool Running { get { return running; } }

        private readonly List<ulong> IRQ_Buffer = new List<ulong>();
        private readonly List<ulong> NMI_Buffer = new List<ulong>();

        public void InitiateIRQ(ulong value)
        {
            IRQ_Buffer.Add(value);
        }

        public void InitiateNMI(ulong value)
        {
            NMI_Buffer.Add(value);
        }

        private int cyclesThisOperation = 0;

        public _6502_CPU()
        {
            Initialise();
        }

        public void Initialise()
        {
            registers = new Registers();
            registers.Clear();
            memory = new Memory(0x10000);
        }

        public void Run(ulong startVector = 0xFFFC)
        {
            registers.PC = memory.ReadWord(startVector);
            running = true;
            while (running)
            {
                Stopwatch s = new Stopwatch();
                s.Start();
                cyclesThisOperation = 0;

                while (NMI_Buffer.Count > 0)
                {
                    ulong value = NMI_Buffer[0];
                    NMI_Buffer.RemoveAt(0);
                    if(value != 0xFFFA)
                        ProcessNMI(value);
                    else
                        ProcessNMI();
                }
                while (IRQ_Buffer.Count > 0 && !registers.Flags.I)
                {
                    ulong value = IRQ_Buffer[0];
                    IRQ_Buffer.RemoveAt(0);
                    if (value != 0xFFFE)
                        ProcessIRQ(value);
                    else
                        ProcessIRQ();
                }

                Execute(GetNextInstruction());

            }
        }

        public void Execute(byte opcode)
        {        
            switch (opcode)
            {
                #region Documented Opcodes

                #region NOP
                case 0xEA:
                    cyclesThisOperation += 2;
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

                #region Undocumented Opcodes

                #region LAX - Load A and X
                case 0xA7:
                    LAX_ZP();
                    break;
                case 0xB7:
                    LAX_ZPY();
                    break;
                case 0xAF:
                    LAX_AB();
                    break;
                case 0xBF:
                    LAX_ABY();
                    break;
                case 0xA3:
                    LAX_ZPIX();
                    break;
                case 0xB3:
                    LAX_ZPIY();
                    break;
                #endregion

                #region SAX - Store A AND X
                case 0x87:
                    SAX_ZP();
                    break;
                case 0x97:
                    SAX_ZPY();
                    break;
                case 0x8F:
                    SAX_AB();
                    break;
                case 0x83:
                    SAX_ZPIX();
                    break;
                #endregion

                #region DCP - DEC then CMP
                case 0xC7:
                    DCP_ZP();
                    break;
                case 0xD7:
                    DCP_ZPX();
                    break;
                case 0xCF:
                    DCP_AB();
                    break;
                case 0xDF:
                    DCP_ABX();
                    break;
                case 0xDB:
                    DCP_ABY();
                    break;
                case 0xC3:
                    DCP_ZPIX();
                    break;
                case 0xD3:
                    DCP_ZPIY();
                    break;
                #endregion

                #region ISC/ISB - INC then SBC
                case 0xE7:
                    ISC_ZP();
                    break;
                case 0xF7:
                    ISC_ZPX();
                    break;
                case 0xEF:
                    ISC_AB();
                    break;
                case 0xFF:
                    ISC_ABX();
                    break;
                case 0xFB:
                    ISC_ABY();
                    break;
                case 0xE3:
                    ISC_ZPIX();
                    break;
                case 0xF3:
                    ISC_ZPIY();
                    break;
                #endregion

                #region SLO - ASL then ORA
                case 0x07:
                    SLO_ZP();
                    break;
                case 0x17:
                    SLO_ZPX();
                    break;
                case 0x0F:
                    SLO_AB();
                    break;
                case 0x1F:
                    SLO_ABX();
                    break;
                case 0x1B:
                    SLO_ABY();
                    break;
                case 0x03:
                    SLO_ZPIX();
                    break;
                case 0x13:
                    SLO_ZPIY();
                    break;
                #endregion

                #region RLA - ROL then AND
                case 0x27:
                    RLA_ZP();
                    break;
                case 0x37:
                    RLA_ZPX();
                    break;
                case 0x2F:
                    RLA_AB();
                    break;
                case 0x3F:
                    RLA_ABX();
                    break;
                case 0x3B:
                    RLA_ABY();
                    break;
                case 0x23:
                    RLA_ZPIX();
                    break;
                case 0x33:
                    RLA_ZPIY();
                    break;
                #endregion

                #region SRE - LSR then EOR
                case 0x47:
                    SRE_ZP();
                    break;
                case 0x57:
                    SRE_ZPX();
                    break;
                case 0x4F:
                    SRE_AB();
                    break;
                case 0x5F:
                    SRE_ABX();
                    break;
                case 0x5B:
                    SRE_ABY();
                    break;
                case 0x43:
                    SRE_ZPIX();
                    break;
                case 0x53:
                    SRE_ZPIY();
                    break;
                #endregion

                #region RRA - ROR then ADC
                case 0x67:
                    RRA_ZP();
                    break;
                case 0x77:
                    RRA_ZPX();
                    break;
                case 0x6F:
                    RRA_AB();
                    break;
                case 0x7F:
                    RRA_ABX();
                    break;
                case 0x7B:
                    RRA_ABY();
                    break;
                case 0x63:
                    RRA_ZPIX();
                    break;
                case 0x73:
                    RRA_ZPIY();
                    break;
                #endregion

                #region Undocumented NOPs
                case 0x1A:
                case 0x3A:
                case 0x5A:
                case 0x7A:
                case 0xDA:
                case 0xFA:
                    NOP();
                    break;
                case 0x04:
                case 0x44:
                case 0x64:
                    NOP_ZP();
                    break;
                case 0x14:
                case 0x34:
                case 0x54:
                case 0x74:
                case 0xD4:
                case 0xF4:
                    NOP_ZPX();
                    break;
                case 0x0C:
                    NOP_AB();
                    break;
                case 0x1C:
                case 0x3C:
                case 0x5C:
                case 0x7C:
                case 0xDC:
                case 0xFC:
                    NOP_ABX();
                    break;
                case 0x80:
                case 0x82:
                case 0x89:
                case 0xC2:
                case 0xE2:
                    NOP_IM();
                    break;
                #endregion

                #region SBC Undocumented
                case 0xEB:
                    SBCI();
                    break;
                #endregion

                #region ANC - AND then copy N to C
                case 0x0B:
                case 0x2B:
                    ANC();
                    break;
                #endregion

                #region ALR - AND then LSR
                case 0x4B:
                    ALR();
                    break;
                #endregion

                #region ARR - AND then ROR
                case 0x6B:
                    ARR();
                    break;
                #endregion

                #region XAA - Transfer X to A then AND
                case 0x8B:
                    XAA();
                    break;
                #endregion

                #region AXS - (A AND X) - value
                case 0xCB:
                    AXS();
                    break;
                #endregion

                #region AHX - A AND X AND H
                case 0x9F:
                    AHX_ABY();
                    break;
                case 0x93:
                    AHX_ZPIY();
                    break;
                #endregion

                #region SHY - Y AND H
                case 0x9C:
                    SHY();
                    break;
                #endregion

                #region SHX - X AND H
                case 0x9E:
                    SHX();
                    break;
                #endregion

                #region TAS - Transfer A AND X to S, then A AND X AND H
                case 0x9B:
                    TAS();
                    break;
                #endregion

                #region LAS - (value AND S) to A, X, and S
                case 0xBB:
                    LAS();
                    break;
                #endregion

                #endregion

                default:
                    break;
            }
        }

        private void IncrementProgramCounter(ulong value = 1)
        {
            registers.PC += value;
            if (registers.PC >= 65536) registers.PC = registers.PC - 65536;
        }

        private byte ReadByteFromMemory(ulong addr)
        {
            return memory.ReadByte(addr);
        }

        private void WriteByteToMemory(ulong addr, byte value)
        {
            memory.WriteByte(addr, value);
        }

        public byte GetNextInstruction()
        {
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            return value;
        }

        private ulong GetInstructionWord()
        {
            byte value1 = GetNextInstruction();
            byte value2 = GetNextInstruction();
            ulong value3 = (ulong)(value1 + value2 * 0x100);// (value2 << 8) | value1);
            return value3 & 0xFFFF;
        }

        private void PushToStack(byte value)
        {
            WriteByteToMemory((ulong)(registers.S + 0x100), value);
            registers.S--;
        }

        private byte PopFromStack()
        {
            registers.S++;
            return ReadByteFromMemory((ulong)(registers.S + 0x100));
        }

        private void ProcessNMI(ulong value = 0xFFFA)
        {
            PushToStack((byte)((registers.PC >> 8) & 0xFF));
            PushToStack((byte)(registers.PC & 0xFF));
            PushToStack(registers.P);
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        private void ProcessIRQ(ulong value = 0xFFFE)
        {
            PushToStack((byte)((registers.PC >> 8) & 0xFF)); 
            PushToStack((byte)(registers.PC & 0xFF));        
            PushToStack((byte)(registers.P | 0x20)); 
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        private static bool CrossBoundary(ulong addr1, ulong addr2)
        {
            return (addr1 & 0xff00) != (addr2 & 0xff00);
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
                cyclesThisOperation += 2;
                lo = ReadByteFromMemory((addr & 0xFF00) + 0xFF);
                hi = ReadByteFromMemory((addr & 0xFF00));
            }
            else
            {
                lo = ReadByteFromMemory(addr);
                hi = ReadByteFromMemory((addr + 1));
            }
            ulong value = (ulong)((hi << 8) | lo);
            return value & 0xFFFF;
        }
        private ulong X_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong addr = (Absolute() + registers.X);
            if (CrossBoundary(addr, registers.PC + 1) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }
        private ulong Y_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong addr = (Absolute() + registers.Y);
            if (CrossBoundary(addr, registers.PC + 1) && checkBoundary) { cyclesThisOperation += 1; }
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
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong addr = (ulong)((value2 << 8) | value1);
            return addr & 0xFFFF;
        }
        private ulong Zero_Page_Indirect_Y_Indexed(bool checkBoundary = true)
        {
            byte value = GetNextInstruction();
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong value3 = (ulong)((value2 << 8) | value1);
            ulong addr = value3 + registers.Y;
            if (CrossBoundary(addr, registers.PC + 1) && checkBoundary) { cyclesThisOperation += 1; }
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
            cyclesThisOperation += 2;
        }
        private void LDA_AB()
        {
            registers.A = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }
        private void LDA_ABX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }
        private void LDA_ABY()
        {
            registers.A = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }
        private void LDA_ZP()
        {
            registers.A = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 3;
        }
        private void LDA_ZPX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }
        private void LDA_ZPIX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void LDA_ZPIY()
        {
            registers.A = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }
        private void LDX_IM()
        {
            registers.X = Immediate();
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }
        private void LDX_AB()
        {
            registers.X = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }
        private void LDX_ABY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }
        private void LDX_ZP()
        {
            registers.X = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 3;
        }
        private void LDX_ZPY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }
        private void LDY_IM()
        {
            registers.Y = Immediate();
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }
        private void LDY_AB()
        {
            registers.Y = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }
        private void LDY_ABX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }
        private void LDY_ZP()
        {
            registers.Y = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 3;
        }
        private void LDY_ZPX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }
        #endregion

        #region ST*
        private void STA_AB()
        {
            WriteByteToMemory(Absolute(), registers.A);
            cyclesThisOperation += 4;
        }
        private void STA_ABX()
        {
            WriteByteToMemory(X_Indexed_Absolute(false), registers.A);
            cyclesThisOperation += 5;
        }
        private void STA_ABY()
        {
            WriteByteToMemory(Y_Indexed_Absolute(false), registers.A);
            cyclesThisOperation += 5;
        }
        private void STA_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.A);
            cyclesThisOperation += 3;
        }
        private void STA_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.A);
            cyclesThisOperation += 4;
        }
        private void STA_ZPIX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page_Indirect(), registers.A);
            cyclesThisOperation += 6;
        }
        private void STA_ZPIY()
        {
            WriteByteToMemory(Zero_Page_Indirect_Y_Indexed(false), registers.A);
            cyclesThisOperation += 6;
        }
        private void STX_AB()
        {
            WriteByteToMemory(Absolute(), registers.X);
            cyclesThisOperation += 4;
        }
        private void STX_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.X);
            cyclesThisOperation += 3;
        }
        private void STX_ZPY()
        {
            WriteByteToMemory(Y_Indexed_Zero_Page(), registers.X);
            cyclesThisOperation += 4;
        }
        private void STY_AB()
        {
            WriteByteToMemory(Absolute(), registers.Y);
            cyclesThisOperation += 4;
        }
        private void STY_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.Y);
            cyclesThisOperation += 3;
        }
        private void STY_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.Y);
            cyclesThisOperation += 4;
        }
        #endregion

        #region T**
        private void TAX()
        {
            registers.X = registers.A;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }
        private void TAY()
        {
            registers.Y = registers.A;
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }
        private void TSX()
        {
            registers.X = registers.S;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }
        private void TXA()
        {
            registers.A = registers.X;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }
        private void TXS()
        {
            registers.S = registers.X;
            cyclesThisOperation += 2;
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
            cyclesThisOperation += 2;
        }
        private void SED()
        {
            registers.Flags.D = true;
            cyclesThisOperation += 2;
        }
        private void SEI()
        {
            registers.Flags.I = true;
            cyclesThisOperation += 2;
        }
        #endregion

        #region PH*
        private void PHA()
        {
            PushToStack(registers.A);
            cyclesThisOperation += 3;
        }
        private void PHP()
        {
            byte addr = registers.Flags.GetFlagsAsByte();
            addr = (byte)(addr | (1 << 4));
            addr = (byte)(addr | (1 << 5));
            PushToStack(addr);
            cyclesThisOperation += 3;
        }
        #endregion

        #region PL*
        private void PLA()
        {
            registers.A = PopFromStack();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }
        private void PLP()
        {
            byte value = PopFromStack();
            registers.Flags.SetFlagsFromByte(value, 0xCF); //ignore bits 5 & 6
            cyclesThisOperation += 4;
        }
        #endregion

        #region CL*
        private void CLC()
        {
            registers.Flags.C = false;
            cyclesThisOperation += 2;
        }
        private void CLD()
        {
            registers.Flags.D = false;
            cyclesThisOperation += 2;
        }
        private void CLI()
        {
            registers.Flags.I = false;
            cyclesThisOperation += 2;
        }
        private void CLV()
        {
            registers.Flags.V = false;
            cyclesThisOperation += 2;
        }
        #endregion

        #region DE*
        private void DECA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }
        private void DECXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 7;
        }
        private void DECZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }
        private void DECXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }
        private void DEX()
        {
            byte value1 = registers.X;
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            registers.X = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }
        private void DEY()
        {
            byte value1 = registers.Y;
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            if (value2 < 0) value2 = (byte)(0xFF - value2);
            registers.Y = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }
        #endregion

        #region IN*
        private void INCA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 6;
        }
        private void INCXA()
        {
            ulong addr = X_Indexed_Absolute();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 7;
        }
        private void INCZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 5;
        }
        private void INCXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            value1 = (byte)(value1 & 0xFF);
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 6;
        }
        private void INX()
        {
            byte value1 = (byte)(registers.X + 1);
            if (value1 < 0) value1 = (byte)(0xFF - value1);
            registers.X = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }
        private void INY()
        {
            byte value1 = (byte)(registers.Y + 1);
            if (value1 < 0) value1 = (byte)(0xFF - value1);
            registers.Y = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }
        #endregion

        #region CM*
        private void CMPI()
        {
            byte addr = Immediate();
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }
        private void CMPA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }
        private void CMPXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }
        private void CMPYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }
        private void CMPZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 3;
        }
        private void CMPXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }
        private void CMPXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }
        private void CMPYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }
        #endregion

        #region CPX
        private void CPXI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }
        private void CPXA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void CPXZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }
        #endregion

        #region CPY
        private void CPYI()
        {
            byte addr = Immediate();
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }
        private void CPYA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void CPYZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }
        #endregion

        #region ADC
        private void ADCI()
        {
            byte value = Immediate();
            ADC(value);
            cyclesThisOperation += 2;
        }
        private void ADCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }
        private void ADCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }
        private void ADCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }
        private void ADCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            ADC(value);
            cyclesThisOperation += 3;
        }
        private void ADCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            ADC(value);
            cyclesThisOperation += 4;
        }
        private void ADCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            ADC(value);
            cyclesThisOperation += 6;
        }
        private void ADCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            ADC(value);
            cyclesThisOperation += 5;
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
            cyclesThisOperation += 3;
        }
        private void SBCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }
        private void SBCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }
        private void SBCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }
        private void SBCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            SBC(value);
            cyclesThisOperation += 3;
        }
        private void SBCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            SBC(value);
            cyclesThisOperation += 4;
        }
        private void SBCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            SBC(value);
            cyclesThisOperation += 6;
        }
        private void SBCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            SBC(value);
            cyclesThisOperation += 5;
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
            cyclesThisOperation += 2;
        }
        private void EORA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void EORXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void EORYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void EORZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }
        private void EORXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void EORXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }
        private void EORYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region ORA
        private void ORAI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }
        private void ORAA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ORAXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ORAYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ORAZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }
        private void ORAXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ORAXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }
        private void ORAYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region AND
        private void ANDI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }
        private void ANDA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ANDXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ANDYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ANDZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }
        private void ANDXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void ANDXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }
        private void ANDYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region BIT
        private void BITA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 4;
        }
        private void BITZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 3;
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
            cyclesThisOperation += 2;
        }
        private void ASLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }
        private void ASLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }
        private void ASLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }
        private void ASLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 0));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
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
            cyclesThisOperation += 2;
        }
        private void LSRA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }
        private void LSRXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }
        private void LSRZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }
        private void LSRXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            value2 ^= (byte)((-0 ^ value2) & (1 << 7));
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
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
            cyclesThisOperation += 2;
        }
        private void ROLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }
        private void ROLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }
        private void ROLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }
        private void ROLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C == true ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
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
            cyclesThisOperation += 2;
        }
        private void RORA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }
        private void RORXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }
        private void RORZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }
        private void RORXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            Set_FlagsNZ(value2);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }
        #endregion

        #region BRANCH
        private void BCC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.C)
                Branch(value);
            cyclesThisOperation += 4;        }
        private void BCS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.C)
                Branch(value);
        }
        private void BEQ()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.Z)
                Branch(value);
        }
        private void BMI()
        {
            byte value = ReadByteFromMemory(registers.PC);
            cyclesThisOperation += 2;
            IncrementProgramCounter();
            if (registers.Flags.N)
                Branch(value);
        }
        private void BNE()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.Z)
                Branch(value);
        }
        private void BPL()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.N)
                Branch(value);
        }
        private void BVC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.V)
                Branch(value);
        }
        private void BVS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.V)
                Branch(value);
        }
        private void BRK()
        {
            IncrementProgramCounter();
            registers.Flags.B = true;
            PushToStack((byte)((registers.PC >> 8) & 0xFF));
            PushToStack((byte)(registers.PC & 0xFF));
            PushToStack(registers.P);
            registers.Flags.B = false;
            registers.Flags.I = true;
            registers.PC = (ulong)(ReadByteFromMemory(0xFFFE) + ReadByteFromMemory(0xFFFF) * 0x100);
            cyclesThisOperation += 7;
        }
        private void Branch(ulong value)
        {
            cyclesThisOperation += 2;
            if ((value & 0x80) == 0)
                registers.PC = (registers.PC + value) & 0xFFFF;
            else
            {

                int x = (byte)(~(value - 0x01)) * -1;
                int y = (int)registers.PC;
                y += x;
                registers.PC = (ulong)y & 0xFFFF;
            }
            if (CrossBoundary(value, registers.PC))
                cyclesThisOperation += 1;
        }
        #endregion

        #region J**
        private void JMPA()
        {
            ulong value = Absolute();
            registers.PC = value;
            cyclesThisOperation += 3;
        }
        private void JMPAI()
        {
            ulong addr = AbsoluteIndirect();
            registers.PC = addr;
            cyclesThisOperation += 5;
        }
        private void JSRA()
        {
            byte pclo = ReadByteFromMemory(registers.PC);
            registers.PC++;
            byte hi = (byte)(((registers.PC) >> 8) & 0xFF);
            PushToStack(hi);
            byte lo = (byte)((registers.PC) & 0xFF);
            PushToStack(lo);
            byte pchi = ReadByteFromMemory(registers.PC);
            registers.PC = (ulong)((pchi << 8) | pclo);
            cyclesThisOperation += 6;
        }
        #endregion

        #region RT*
        private void RTI()
        {
            byte flags = PopFromStack();
            byte lo = PopFromStack();
            byte hi = PopFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.Flags.SetFlagsFromByte(flags, 0b11001111);
            cyclesThisOperation += 6;
        }
        private void RTS()
        {
            byte lo = PopFromStack();
            byte hi = PopFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.PC++;
            cyclesThisOperation += 6;
        }
        #endregion

        #endregion

        #region Undocumented Opcodes Implementation

        #region LAX - Load A and X
        private void LAX_ZP()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            registers.A = value;
            registers.X = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }
        private void LAX_ZPY()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Zero_Page());
            registers.A = value;
            registers.X = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void LAX_AB()
        {
            byte value = ReadByteFromMemory(Absolute());
            registers.A = value;
            registers.X = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void LAX_ABY()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            registers.A = value;
            registers.X = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }
        private void LAX_ZPIX()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            registers.A = value;
            registers.X = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }
        private void LAX_ZPIY()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            registers.A = value;
            registers.X = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region SAX - Store A AND X
        private void SAX_ZP()
        {
            byte value = (byte)(registers.A & registers.X);
            WriteByteToMemory(Zero_Page(), value);
            cyclesThisOperation += 3;
        }
        private void SAX_ZPY()
        {
            byte value = (byte)(registers.A & registers.X);
            WriteByteToMemory(Y_Indexed_Zero_Page(), value);
            cyclesThisOperation += 4;
        }
        private void SAX_AB()
        {
            byte value = (byte)(registers.A & registers.X);
            WriteByteToMemory(Absolute(), value);
            cyclesThisOperation += 4;
        }
        private void SAX_ZPIX()
        {
            byte value = (byte)(registers.A & registers.X);
            WriteByteToMemory(X_Indexed_Zero_Page_Indirect(), value);
            cyclesThisOperation += 6;
        }
        #endregion

        #region DCP - DEC then CMP
        private void DCP_ZP()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 5;
        }
        private void DCP_ZPX()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 6;
        }
        private void DCP_AB()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 6;
        }
        private void DCP_ABX()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 7;
        }
        private void DCP_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 7;
        }
        private void DCP_ZPIX()
        {
            ulong addr = X_Indexed_Zero_Page_Indirect();
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 8;
        }
        private void DCP_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte value = ReadByteFromMemory(addr);
            value = (byte)((value + (~0x01)) + 1);
            WriteByteToMemory(addr, value);
            byte result = (byte)(registers.A - value);
            registers.Flags.C = (value <= registers.A);
            Set_FlagsNZ(result);
            cyclesThisOperation += 8;
        }
        #endregion

        #region ISC/ISB - INC then SBC
        private void ISC_ZP()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 5;
        }
        private void ISC_ZPX()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 6;
        }
        private void ISC_AB()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 6;
        }
        private void ISC_ABX()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 7;
        }
        private void ISC_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 7;
        }
        private void ISC_ZPIX()
        {
            ulong addr = X_Indexed_Zero_Page_Indirect();
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 8;
        }
        private void ISC_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte value = ReadByteFromMemory(addr);
            value++;
            value = (byte)(value & 0xFF);
            WriteByteToMemory(addr, value);
            SBC(value);
            cyclesThisOperation += 8;
        }
        #endregion

        #region SLO - ASL then ORA
        private void SLO_ZP()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }
        private void SLO_ZPX()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void SLO_AB()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void SLO_ABX()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 7;
        }
        private void SLO_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 7;
        }
        private void SLO_ZPIX()
        {
            ulong addr = X_Indexed_Zero_Page_Indirect();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 8;
        }
        private void SLO_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            value = (byte)(value << 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A | value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 8;
        }
        #endregion

        #region RLA - ROL then AND
        private void RLA_ZP()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }
        private void RLA_ZPX()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void RLA_AB()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void RLA_ABX()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 7;
        }
        private void RLA_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 7;
        }
        private void RLA_ZPIX()
        {
            ulong addr = X_Indexed_Zero_Page_Indirect();
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 8;
        }
        private void RLA_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte value = ReadByteFromMemory(addr);
            int carry = registers.Flags.C ? 1 : 0;
            byte value2 = (byte)((value << 1) + carry);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            WriteByteToMemory(addr, value2);
            registers.A = (byte)(registers.A & value2);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 8;
        }
        #endregion

        #region SRE - LSR then EOR
        private void SRE_ZP()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }
        private void SRE_ZPX()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void SRE_AB()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }
        private void SRE_ABX()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 7;
        }
        private void SRE_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 7;
        }
        private void SRE_ZPIX()
        {
            ulong addr = X_Indexed_Zero_Page_Indirect();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 8;
        }
        private void SRE_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.C = ((value & (1 << 0)) != 0);
            value = (byte)(value >> 1);
            WriteByteToMemory(addr, value);
            registers.A = (byte)(registers.A ^ value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 8;
        }
        #endregion

        #region RRA - ROR then ADC
        private void RRA_ZP()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 5;
        }
        private void RRA_ZPX()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 6;
        }
        private void RRA_AB()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 6;
        }
        private void RRA_ABX()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 7;
        }
        private void RRA_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 7;
        }
        private void RRA_ZPIX()
        {
            ulong addr = X_Indexed_Zero_Page_Indirect();
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 8;
        }
        private void RRA_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte value = ReadByteFromMemory(addr);
            byte value2 = (byte)(value >> 1);
            if (registers.Flags.C)
                value2 += 0x80;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            WriteByteToMemory(addr, value2);
            ADC(value2);
            cyclesThisOperation += 8;
        }
        #endregion

        #region Undocumented NOPs
        private void NOP()
        {
            cyclesThisOperation += 2;
        }
        private void NOP_ZP()
        {
            Zero_Page();
            cyclesThisOperation += 3;
        }
        private void NOP_ZPX()
        {
            X_Indexed_Zero_Page();
            cyclesThisOperation += 4;
        }
        private void NOP_AB()
        {
            Absolute();
            cyclesThisOperation += 4;
        }
        private void NOP_ABX()
        {
            X_Indexed_Absolute();
            cyclesThisOperation += 4;
        }
        private void NOP_IM()
        {
            Immediate();
            cyclesThisOperation += 2;
        }
        #endregion

        #region ANC - AND then copy N to C
        private void ANC()
        {
            byte value = Immediate();
            registers.A = (byte)(registers.A & value);
            Set_FlagsNZ(registers.A);
            registers.Flags.C = registers.Flags.N;
            cyclesThisOperation += 2;
        }
        #endregion

        #region ALR - AND then LSR
        private void ALR()
        {
            byte value = Immediate();
            registers.A = (byte)(registers.A & value);
            registers.Flags.C = ((registers.A & (1 << 0)) != 0);
            registers.A = (byte)(registers.A >> 1);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }
        #endregion

        #region ARR - AND then ROR
        private void ARR()
        {
            byte value = Immediate();
            registers.A = (byte)(registers.A & value);
            byte result = (byte)(registers.A >> 1);
            if (registers.Flags.C)
                result += 0x80;
            registers.A = result;
            Set_FlagsNZ(registers.A);
            registers.Flags.C = ((result & (1 << 6)) != 0);
            registers.Flags.V = (((result & (1 << 6)) != 0) ^ ((result & (1 << 5)) != 0));
            cyclesThisOperation += 2;
        }
        #endregion

        #region XAA - Transfer X to A then AND (unstable)
        private void XAA()
        {
            byte value = Immediate();
            registers.A = registers.X;
            registers.A = (byte)(registers.A & value);
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }
        #endregion

        #region AXS - (A AND X) - value
        private void AXS()
        {
            byte value = Immediate();
            byte temp = (byte)(registers.A & registers.X);
            int result = temp - value;
            registers.X = (byte)result;
            registers.Flags.C = (result >= 0);
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }
        #endregion

        #region AHX - A AND X AND H (unstable)
        private void AHX_ABY()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte high = (byte)((addr >> 8) + 1);
            byte value = (byte)(registers.A & registers.X & high);
            WriteByteToMemory(addr, value);
            cyclesThisOperation += 5;
        }
        private void AHX_ZPIY()
        {
            ulong addr = Zero_Page_Indirect_Y_Indexed(false);
            byte high = (byte)((addr >> 8) + 1);
            byte value = (byte)(registers.A & registers.X & high);
            WriteByteToMemory(addr, value);
            cyclesThisOperation += 6;
        }
        #endregion

        #region SHY - Y AND H (unstable)
        private void SHY()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte high = (byte)((addr >> 8) + 1);
            byte value = (byte)(registers.Y & high);
            WriteByteToMemory(addr, value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region SHX - X AND H (unstable)
        private void SHX()
        {
            ulong addr = Y_Indexed_Absolute(false);
            byte high = (byte)((addr >> 8) + 1);
            byte value = (byte)(registers.X & high);
            WriteByteToMemory(addr, value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region TAS - Transfer A AND X to S, then A AND X AND H (unstable)
        private void TAS()
        {
            registers.S = (byte)(registers.A & registers.X);
            ulong addr = Y_Indexed_Absolute(false);
            byte high = (byte)((addr >> 8) + 1);
            byte value = (byte)(registers.A & registers.X & high);
            WriteByteToMemory(addr, value);
            cyclesThisOperation += 5;
        }
        #endregion

        #region LAS - (value AND S) to A, X, and S
        private void LAS()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            byte result = (byte)(value & registers.S);
            registers.A = result;
            registers.X = result;
            registers.S = result;
            Set_FlagsNZ(result);
            cyclesThisOperation += 4;
        }
        #endregion

        #endregion

    }
}
