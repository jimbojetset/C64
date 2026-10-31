

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace C64.CPU
{

    public class CPU_6502
    {
        /// <summary>Requests 1 ms Windows timer resolution for accurate CPU pacing.</summary>
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        /// <summary>Restores the Windows timer resolution after paced CPU execution stops.</summary>
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        [SupportedOSPlatform("windows")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private const uint TimerResolutionMs = 1;

        public Registers registers = new();

        public Memory memory = new(0x10000);
        private ICpuBus bus = null!;

        private bool running = true;
        private bool paused;
        public bool Paused => Volatile.Read(ref paused);
        private bool jammed;
        private bool jamReported;
        private ulong jamAddress;

        private readonly int clockFreq = 2000000;

        private readonly ConcurrentQueue<ulong> IRQ_Buffer = new();
        private readonly ConcurrentQueue<ulong> NMI_Buffer = new();
        private int irqLineAsserted;

        /// <summary>Queues an IRQ request for the CPU.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        public void InitiateIRQ(ulong value)
        {
            IRQ_Buffer.Enqueue(value);
        }

        /// <summary>Sets the level-sensitive IRQ input line for device cores that hold IRQ active until acknowledged.</summary>
        /// <param name="asserted">Whether the IRQ line is currently asserted.</param>
        public void SetIrqLine(bool asserted)
        {
            Volatile.Write(ref irqLineAsserted, asserted ? 1 : 0);
        }

        /// <summary>Queues an NMI request for the CPU.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        public void InitiateNMI(ulong value)
        {
            NMI_Buffer.Enqueue(value);
        }

        private int cyclesThisOperation = 0;
        private long totalCycles;
        public Action<int>? OnCyclesExecuted;
        private int externalStallCycles;
        public bool PacingEnabled { get; set; } = true;

        /// <summary>Adds DMA or bus-contention cycles that must elapse before the next instruction.</summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        public void RequestExternalStallCycles(int cycles)
        {
            if (cycles <= 0) return;
            Interlocked.Add(ref externalStallCycles, cycles);
        }

        /// <summary>Creates a 6502 core with an internal 64 KiB bus at the requested clock rate.</summary>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6502(int freq = 1000000)
        {
            Initialise();
            clockFreq = freq;
        }

        /// <summary>Creates a 6502 core connected to the supplied bus at the requested clock rate.</summary>
        /// <param name="bus">The CPU-visible bus implementation.</param>
        /// <param name="freq">The target CPU frequency in cycles per second.</param>
        public CPU_6502(ICpuBus bus, int freq = 1000000)
        {
            Initialise();
            Bus = bus;
            clockFreq = freq;
        }

        public ICpuBus Bus
        {
            get => bus;
            set => bus = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Initializes registers, stack state, and the default 64 KiB memory bus.</summary>
        public void Initialise()
        {
            registers = new Registers();
            registers.Clear();
            registers.S = 0xFF;
            memory = new Memory(0x10000);
            bus = memory;
        }

        public Action? OnReset;
        private int resetPending;

        /// <summary>Requests a CPU reset at the next safe point.</summary>
        public void RequestReset() => Interlocked.Exchange(ref resetPending, 1);

        /// <summary>Immediately resets CPU registers and loads the reset vector on the current thread.</summary>
        public void ResetNow()
        {
            DoReset();
        }

        /// <summary>Pauses or resumes instruction execution without changing CPU state.</summary>
        public void SetPaused(bool value) => Volatile.Write(ref paused, value);

        /// <summary>Resets CPU state on the CPU thread.</summary>
        private void DoReset()
        {
            OnReset?.Invoke();
            registers.Clear();
            registers.S = 0xFF;
            registers.Flags.I = true;
            registers.PC = ReadWordFromBus(0xFFFC);
            jammed = false;
            jamReported = false;
            jamAddress = 0;

            while (IRQ_Buffer.TryDequeue(out _)) { }
            while (NMI_Buffer.TryDequeue(out _)) { }
            Interlocked.Exchange(ref totalCycles, 0);
        }

        private const int SliceCycles = 64;

        /// <summary>Runs paced instruction slices from the supplied vector until CPU execution stops.</summary>
        /// <param name="startVector">The start vector value used by the operation.</param>
        public void Run(ulong startVector = 0xFFFC)
        {
            registers.PC = ReadWordFromBus(startVector);
            running = true;

            int sliceCycles = SliceCycles;
            long ticksPerSlice = Math.Max(1, Stopwatch.Frequency * SliceCycles / clockFreq);

            long nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;

            bool timerRaised = TryBeginHighResolutionTimer();
            try
            {
                while (running)
                {
                    if (Volatile.Read(ref paused))
                    {
                        Thread.Sleep(2);
                        nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;
                        continue;
                    }

                    if (Interlocked.Exchange(ref resetPending, 0) == 1)
                    {
                        DoReset();
                        nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;
                    }

                    if (jammed)
                    {
                        if (!jamReported)
                        {
                            Console.Error.WriteLine($"CPU jammed at ${jamAddress & 0xFFFF:X4}");
                            jamReported = true;
                        }

                        WaitUntil(nextDeadline);
                        nextDeadline += ticksPerSlice;

                        long nowJammed = Stopwatch.GetTimestamp();
                        if (nextDeadline < nowJammed - ticksPerSlice * 4)
                            nextDeadline = nowJammed + ticksPerSlice;
                        continue;
                    }

                    cyclesThisOperation = 0;
                    while (cyclesThisOperation < sliceCycles)
                    {
                        int stall = Interlocked.Exchange(ref externalStallCycles, 0);
                        if (stall > 0)
                        {
                            cyclesThisOperation += stall;
                            Interlocked.Add(ref totalCycles, stall);
                            continue;
                        }

                        while (NMI_Buffer.TryDequeue(out ulong nmiValue))
                        {
                            if (nmiValue != 0xFFFA)
                                ProcessNMI(nmiValue);
                            else
                                ProcessNMI();
                        }
                        while (!registers.Flags.I && IRQ_Buffer.TryDequeue(out ulong irqValue))
                        {
                            if (irqValue != 0xFFFE)
                                ProcessIRQ(irqValue);
                            else
                                ProcessIRQ();
                        }
                        if (!registers.Flags.I && Volatile.Read(ref irqLineAsserted) != 0)
                            ProcessIRQ();
                        int beforeCycles = cyclesThisOperation;
                        Execute(GetNextByteInstruction());
                        int deltaCycles = cyclesThisOperation - beforeCycles;
                        if (deltaCycles > 0)
                        {
                            Interlocked.Add(ref totalCycles, deltaCycles);
                            OnCyclesExecuted?.Invoke(deltaCycles);
                        }
                        if (jammed)
                            break;
                    }

                    if (PacingEnabled)
                    {
                        WaitUntil(nextDeadline);

                        nextDeadline += ticksPerSlice;

                        long now = Stopwatch.GetTimestamp();
                        if (nextDeadline < now - ticksPerSlice * 4)
                            nextDeadline = now + ticksPerSlice;
                    }
                    else
                    {
                        nextDeadline = Stopwatch.GetTimestamp() + ticksPerSlice;
                    }
                }
            }
            finally
            {
                if (timerRaised) TryEndHighResolutionTimer();
            }
        }

        /// <summary>Requests the finer Windows timer resolution used by the CPU pacing loop.</summary>
        private static bool TryBeginHighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { return TimeBeginPeriod(TimerResolutionMs) == 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>Releases the finer Windows timer resolution when CPU pacing ends.</summary>
        private static void TryEndHighResolutionTimer()
        {
            if (!OperatingSystem.IsWindows()) return;
            try { TimeEndPeriod(TimerResolutionMs); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        /// <summary>Sleeps and then spin-waits until the next paced CPU slice is due.</summary>
        /// <param name="deadlineTicks">The high-resolution timer deadline to wait for.</param>
        private static void WaitUntil(long deadlineTicks)
        {
            long remaining = deadlineTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;

            long remainingMs = remaining * 1000 / Stopwatch.Frequency;

            if (remainingMs > 1)
                Thread.Sleep((int)(remainingMs - 1));

            while (Stopwatch.GetTimestamp() < deadlineTicks)
                Thread.SpinWait(64);
        }

        /// <summary>Dispatches a decoded opcode to its 6502 instruction handler and accounts for the instruction cycles.</summary>
        /// <param name="opcode">The 6510 opcode byte to execute.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Execute(byte opcode)
        {
            switch (opcode)
            {
                #region Documented Opcodes

                #region NOP

                case 0xEA:
                    cyclesThisOperation += 2;
                    break;

                #endregion NOP

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

                #endregion LD*

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

                #endregion ST*

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

                #endregion T**

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

                #endregion SE*

                #region PH*

                case 0x48:
                    PHA();
                    break;

                case 0x08:
                    PHP();
                    break;

                #endregion PH*

                #region PL*

                case 0x68:
                    PLA();
                    break;

                case 0x28:
                    PLP();
                    break;

                #endregion PL*

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

                #endregion CL*

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

                #endregion DE*

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

                #endregion IN*

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

                #endregion CM*

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

                #endregion CPX

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

                #endregion CPY

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

                #endregion ADC

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

                #endregion SBC

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

                #endregion EOR

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

                #endregion ORA

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

                #endregion AND

                #region BIT

                case 0x2C:
                    BITA();
                    break;

                case 0x24:
                    BITZ();
                    break;

                #endregion BIT

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

                #endregion ASL

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

                #endregion LSR

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

                #endregion ROL

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

                #endregion ROR

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

                #endregion BRANCH

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

                #endregion J**

                #region RT*

                case 0x40:
                    RTI();
                    break;

                case 0x60:
                    RTS();
                    break;

                #endregion RT*

                #region Illegal / undocumented opcodes

                case 0xA3: LAX(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 6; break;
                case 0xA7: LAX(Zero_Page()); cyclesThisOperation += 3; break;
                case 0xAF: LAX(Absolute()); cyclesThisOperation += 4; break;
                case 0xB3: LAX(Zero_Page_Indirect_Y_Indexed()); cyclesThisOperation += 5; break;
                case 0xB7: LAX(Y_Indexed_Zero_Page()); cyclesThisOperation += 4; break;
                case 0xBF: LAX(Y_Indexed_Absolute()); cyclesThisOperation += 4; break;

                case 0x83: SAX(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 6; break;
                case 0x87: SAX(Zero_Page()); cyclesThisOperation += 3; break;
                case 0x8F: SAX(Absolute()); cyclesThisOperation += 4; break;
                case 0x97: SAX(Y_Indexed_Zero_Page()); cyclesThisOperation += 4; break;

                case 0xC3: DCP(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0xC7: DCP(Zero_Page()); cyclesThisOperation += 5; break;
                case 0xCF: DCP(Absolute()); cyclesThisOperation += 6; break;
                case 0xD3: DCP(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0xD7: DCP(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0xDB: DCP(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0xDF: DCP(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                case 0xE3: ISC(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0xE7: ISC(Zero_Page()); cyclesThisOperation += 5; break;
                case 0xEF: ISC(Absolute()); cyclesThisOperation += 6; break;
                case 0xF3: ISC(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0xF7: ISC(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0xFB: ISC(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0xFF: ISC(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                case 0x03: SLO(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x07: SLO(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x0F: SLO(Absolute()); cyclesThisOperation += 6; break;
                case 0x13: SLO(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x17: SLO(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x1B: SLO(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x1F: SLO(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                case 0x43: SRE(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x47: SRE(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x4F: SRE(Absolute()); cyclesThisOperation += 6; break;
                case 0x53: SRE(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x57: SRE(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x5B: SRE(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x5F: SRE(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                case 0x23: RLA(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x27: RLA(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x2F: RLA(Absolute()); cyclesThisOperation += 6; break;
                case 0x33: RLA(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x37: RLA(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x3B: RLA(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x3F: RLA(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                case 0x63: RRA(X_Indexed_Zero_Page_Indirect()); cyclesThisOperation += 8; break;
                case 0x67: RRA(Zero_Page()); cyclesThisOperation += 5; break;
                case 0x6F: RRA(Absolute()); cyclesThisOperation += 6; break;
                case 0x73: RRA(Zero_Page_Indirect_Y_Indexed(false)); cyclesThisOperation += 8; break;
                case 0x77: RRA(X_Indexed_Zero_Page()); cyclesThisOperation += 6; break;
                case 0x7B: RRA(Y_Indexed_Absolute(false)); cyclesThisOperation += 7; break;
                case 0x7F: RRA(X_Indexed_Absolute(false)); cyclesThisOperation += 7; break;

                case 0x1A:
                case 0x3A:
                case 0x5A:
                case 0x7A:
                case 0xDA:
                case 0xFA:
                    cyclesThisOperation += 2; break;
                case 0x80:
                case 0x82:
                case 0x89:
                case 0xC2:
                case 0xE2:
                    Immediate(); cyclesThisOperation += 2; break;
                case 0x04:
                case 0x44:
                case 0x64:
                    Zero_Page(); cyclesThisOperation += 3; break;
                case 0x14:
                case 0x34:
                case 0x54:
                case 0x74:
                case 0xD4:
                case 0xF4:
                    X_Indexed_Zero_Page(); cyclesThisOperation += 4; break;
                case 0x0C:
                    Absolute(); cyclesThisOperation += 4; break;
                case 0x1C:
                case 0x3C:
                case 0x5C:
                case 0x7C:
                case 0xDC:
                case 0xFC:
                    X_Indexed_Absolute(); cyclesThisOperation += 4; break;

                case 0x0B: case 0x2B: ANC_IM(); cyclesThisOperation += 2; break;
                case 0x4B: ALR_IM(); cyclesThisOperation += 2; break;
                case 0x6B: ARR_IM(); cyclesThisOperation += 2; break;
                case 0x8B: XAA_IM(); cyclesThisOperation += 2; break;
                case 0xAB: LAX_IM(); cyclesThisOperation += 2; break;
                case 0xBB: LAS_AY(); cyclesThisOperation += 4; break;
                case 0xCB: AXS_IM(); cyclesThisOperation += 2; break;
                case 0xEB: SBCI(); break;

                case 0x93: AHX_IY(); cyclesThisOperation += 6; break;
                case 0x9B: TAS_AY(); cyclesThisOperation += 5; break;
                case 0x9C: SHY_AX(); cyclesThisOperation += 5; break;
                case 0x9E: SHX_AY(); cyclesThisOperation += 5; break;
                case 0x9F: AHX_AY(); cyclesThisOperation += 5; break;

                case 0x02:
                case 0x12:
                case 0x22:
                case 0x32:
                case 0x42:
                case 0x52:
                case 0x62:
                case 0x72:
                case 0x92:
                case 0xB2:
                case 0xD2:
                case 0xF2:
                    jamAddress = (registers.PC - 1) & 0xFFFF;
                    jammed = true; cyclesThisOperation += 2; break;

                #endregion Illegal / undocumented opcodes

                default:
                    throw new InvalidOperationException($"Unhandled opcode ${opcode:X2} at ${((registers.PC - 1) & 0xFFFF):X4}");

                    #endregion Documented Opcodes
            }
        }

        /// <summary>Executes a single instruction and returns the number of cycles it consumed.</summary>
        public int StepInstruction()
        {
            if (jammed)
            {
                if (!jamReported)
                {
                    Console.Error.WriteLine($"CPU jammed at ${jamAddress & 0xFFFF:X4}");
                    jamReported = true;
                }
                return 0;
            }

            int beforeCycles = cyclesThisOperation;
            while (!registers.Flags.I && IRQ_Buffer.TryDequeue(out ulong irqValue))
            {
                if (irqValue != 0xFFFE)
                    ProcessIRQ(irqValue);
                else
                    ProcessIRQ();
            }
            if (!registers.Flags.I && Volatile.Read(ref irqLineAsserted) != 0)
                ProcessIRQ();

            Execute(GetNextByteInstruction());
            int elapsed = cyclesThisOperation - beforeCycles;
            if (elapsed > 0)
            {
                Interlocked.Add(ref totalCycles, elapsed);
                OnCyclesExecuted?.Invoke(elapsed);
            }

            return elapsed;
        }

        #region Illegal opcode helpers

        /// <summary>Loads both A and X from the byte at the supplied address and updates N and Z.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAX(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.A = v;
            registers.X = v;
            Set_FlagsNZ(v);
        }

        /// <summary>Stores A AND X at the supplied address.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SAX(ulong addr)
        {
            WriteByteToMemory(addr, (byte)(registers.A & registers.X));
        }

        /// <summary>Executes undocumented DCP by decrementing memory, then comparing it with A.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DCP(ulong addr)
        {
            byte v = (byte)(ReadByteFromMemory(addr) - 1);
            WriteByteToMemory(addr, v);
            int diff = registers.A - v;
            registers.Flags.C = (diff & 0x100) == 0;
            Set_FlagsNZ((byte)diff);
        }

        /// <summary>Executes undocumented ISC by incrementing memory, then subtracting it from A.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ISC(ulong addr)
        {
            byte v = (byte)(ReadByteFromMemory(addr) + 1);
            WriteByteToMemory(addr, v);
            SBC(v);
        }

        /// <summary>Executes undocumented SLO by shifting memory left, then ORing it into A.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SLO(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.Flags.C = (v & 0x80) != 0;
            v <<= 1;
            WriteByteToMemory(addr, v);
            registers.A |= v;
            Set_FlagsNZ(registers.A);
        }

        /// <summary>Executes undocumented SRE by shifting memory right, then XORing it into A.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SRE(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            registers.Flags.C = (v & 0x01) != 0;
            v >>= 1;
            WriteByteToMemory(addr, v);
            registers.A ^= v;
            Set_FlagsNZ(registers.A);
        }

        /// <summary>Executes undocumented RLA by rotating memory left, then ANDing it into A.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RLA(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            bool oldC = registers.Flags.C;
            registers.Flags.C = (v & 0x80) != 0;
            v = (byte)((v << 1) | (oldC ? 1 : 0));
            WriteByteToMemory(addr, v);
            registers.A &= v;
            Set_FlagsNZ(registers.A);
        }

        /// <summary>Executes undocumented RRA by rotating memory right, then adding it to A.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RRA(ulong addr)
        {
            byte v = ReadByteFromMemory(addr);
            bool oldC = registers.Flags.C;
            registers.Flags.C = (v & 0x01) != 0;
            v = (byte)((v >> 1) | (oldC ? 0x80 : 0));
            WriteByteToMemory(addr, v);
            ADC(v);
        }

        /// <summary>Executes undocumented ANC by ANDing an immediate byte into A and copying bit 7 to carry.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ANC_IM()
        {
            registers.A &= Immediate();
            Set_FlagsNZ(registers.A);
            registers.Flags.C = registers.Flags.N;
        }

        /// <summary>Executes undocumented ALR by ANDing an immediate byte into A, then shifting A right.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ALR_IM()
        {
            byte v = (byte)(registers.A & Immediate());
            registers.Flags.C = (v & 0x01) != 0;
            registers.A = (byte)(v >> 1);
            Set_FlagsNZ(registers.A);
        }

        /// <summary>Executes undocumented ARR by ANDing an immediate byte into A, then rotating A right through carry.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ARR_IM()
        {
            byte v = (byte)(registers.A & Immediate());
            byte r = (byte)((v >> 1) | (registers.Flags.C ? 0x80 : 0));
            registers.A = r;
            Set_FlagsNZ(r);
            if (registers.Flags.D)
            {
                registers.Flags.V = ((v ^ r) & 0x40) != 0;
                if (((v & 0x0F) + (v & 0x01)) > 0x05)
                    registers.A = (byte)((registers.A & 0xF0) | ((registers.A + 0x06) & 0x0F));

                if (((v & 0xF0) + (v & 0x10)) > 0x50)
                {
                    registers.Flags.C = true;
                    registers.A += 0x60;
                }
                else
                {
                    registers.Flags.C = false;
                }
            }
            else
            {
                registers.Flags.C = (r & 0x40) != 0;
                registers.Flags.V = ((r ^ (r << 1)) & 0x40) != 0;
            }
        }

        /// <summary>Executes undocumented AXS by subtracting an immediate byte from A AND X and storing the result in X.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AXS_IM()
        {
            int t = (registers.A & registers.X) - Immediate();
            registers.X = (byte)t;
            registers.Flags.C = (t & 0x100) == 0;
            Set_FlagsNZ(registers.X);
        }

        /// <summary>Executes undocumented XAA by combining A and X with an immediate byte and updating N and Z.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void XAA_IM()
        {
            registers.A = (byte)((registers.A | 0xEE) & registers.X & Immediate());
            Set_FlagsNZ(registers.A);
        }

        /// <summary>Executes undocumented immediate LAX by loading the operand into both A and X.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAX_IM()
        {
            byte v = (byte)((registers.A | 0xEE) & Immediate());
            registers.A = v;
            registers.X = v;
            Set_FlagsNZ(v);
        }

        /// <summary>Executes undocumented LAS by ANDing absolute Y-indexed memory with the stack pointer and loading A, X, and S.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LAS_AY()
        {
            ulong addr = Y_Indexed_Absolute();
            byte v = (byte)(ReadByteFromMemory(addr) & registers.S);
            registers.A = v;
            registers.X = v;
            registers.S = v;
            Set_FlagsNZ(v);
        }

        /// <summary>Executes undocumented AHX by storing A AND X with the effective-address high byte through indirect Y addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AHX_IY()
        {
            (ulong addr, byte value) = StoreHighIndirectY((byte)(registers.A & registers.X));
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes undocumented AHX by storing A AND X with the effective-address high byte through absolute Y addressing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AHX_AY()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute((byte)(registers.A & registers.X), registers.Y);
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes undocumented TAS by loading A AND X into S and storing it with the effective-address high byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TAS_AY()
        {
            byte s = (byte)(registers.A & registers.X);
            registers.S = s;
            (ulong addr, byte value) = StoreHighIndexedAbsolute(s, registers.Y);
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes undocumented SHY by storing Y masked by the effective-address high byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SHY_AX()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute(registers.Y, registers.X);
            WriteByteToMemory(addr, value);
        }

        /// <summary>Executes undocumented SHX by storing X masked by the effective-address high byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SHX_AY()
        {
            (ulong addr, byte value) = StoreHighIndexedAbsolute(registers.X, registers.Y);
            WriteByteToMemory(addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (ulong Address, byte Value) StoreHighIndexedAbsolute(byte source, byte index)
        {
            ulong baseAddr = Absolute();
            ulong indexedAddr = baseAddr + index;
            byte value = (byte)(source & (((baseAddr >> 8) + 1) & 0xFF));
            byte writeHigh = CrossBoundary(indexedAddr, baseAddr) ? value : (byte)((indexedAddr >> 8) & 0xFF);
            ulong writeAddr = ((ulong)writeHigh << 8) | (indexedAddr & 0xFF);
            return (writeAddr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (ulong Address, byte Value) StoreHighIndirectY(byte source)
        {
            byte zeroPage = GetNextByteInstruction();
            byte low = ReadByteFromMemory(zeroPage);
            byte high = ReadByteFromMemory((byte)(zeroPage + 1));
            ulong baseAddr = (ulong)((high << 8) | low);
            ulong indexedAddr = baseAddr + registers.Y;
            byte value = (byte)(source & (((baseAddr >> 8) + 1) & 0xFF));
            byte writeHigh = CrossBoundary(indexedAddr, baseAddr) ? value : (byte)((indexedAddr >> 8) & 0xFF);
            ulong writeAddr = ((ulong)writeHigh << 8) | (indexedAddr & 0xFF);
            return (writeAddr, value);
        }

        #endregion Illegal opcode helpers

        #region Addressing Modes

        /// <summary>Fetches an immediate operand from PC and advances PC by one byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Immediate()
        {
            byte addr = GetNextByteInstruction();
            return addr;
        }

        /// <summary>Fetches a little-endian absolute operand address and advances PC by two bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Absolute()
        {
            ulong addr = GetNextWordInstruction();
            return addr & 0xFFFF;
        }

        /// <summary>Resolves an indirect JMP target while reproducing the 6502 page-boundary wrap quirk.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        /// <summary>Fetches an absolute base address, adds X, and optionally charges the page-crossing cycle.</summary>
        /// <param name="checkBoundary">The check boundary value used by the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong X_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong baseAddr = Absolute();
            ulong addr = baseAddr + registers.X;
            if (CrossBoundary(addr, baseAddr) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }

        /// <summary>Fetches an absolute base address, adds Y, and optionally charges the page-crossing cycle.</summary>
        /// <param name="checkBoundary">The check boundary value used by the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Y_Indexed_Absolute(bool checkBoundary = true)
        {
            ulong baseAddr = Absolute();
            ulong addr = baseAddr + registers.Y;
            if (CrossBoundary(addr, baseAddr) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }

        /// <summary>Fetches the next operand byte as a zero-page address.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Zero_Page()
        {
            byte addr = GetNextByteInstruction();
            return addr;
        }

        /// <summary>Adds X to the next operand with zero-page wrapping.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte X_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.X) & 0xFF);
            return addr;
        }

        /// <summary>Adds Y to the next operand with zero-page wrapping.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private byte Y_Indexed_Zero_Page()
        {
            byte addr = (byte)((Zero_Page() + registers.Y) & 0xFF);
            return addr;
        }

        /// <summary>Adds X to the zero-page operand, then reads the wrapped 16-bit effective address.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong X_Indexed_Zero_Page_Indirect()
        {
            byte value = (byte)(GetNextByteInstruction() + registers.X);
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong addr = (ulong)((value2 << 8) | value1);
            return addr & 0xFFFF;
        }

        /// <summary>Reads a wrapped zero-page pointer, adds Y, and charges a page-crossing cycle when requested.</summary>
        /// <param name="checkBoundary">The check boundary value used by the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private ulong Zero_Page_Indirect_Y_Indexed(bool checkBoundary = true)
        {
            byte value = GetNextByteInstruction();
            byte value1 = ReadByteFromMemory(value);
            byte value2 = (byte)(ReadByteFromMemory(value += 1) & 0xFF);
            ulong value3 = (ulong)((value2 << 8) | value1);
            ulong addr = value3 + registers.Y;
            if (CrossBoundary(addr, value3) && checkBoundary) { cyclesThisOperation += 1; }
            return addr & 0xFFFF;
        }

        /// <summary>Updates the negative and zero flags from an 8-bit result.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Set_FlagsNZ(byte value)
        {
            registers.Flags.Z = (value == 0);
            registers.Flags.N = ((value & 0x80) != 0);
        }

        #endregion Addressing Modes

        #region Documented Opcodes

        #region LD*

        /// <summary>Loads the immediate operand into A and updates the negative and zero flags.</summary>
        private void LDA_IM()
        {
            registers.A = Immediate();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        /// <summary>Loads the absolute operand into A and updates the negative and zero flags.</summary>
        private void LDA_AB()
        {
            registers.A = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the absolute X-indexed operand into A and updates the negative and zero flags.</summary>
        private void LDA_ABX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the absolute Y-indexed operand into A and updates the negative and zero flags.</summary>
        private void LDA_ABY()
        {
            registers.A = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the zero-page operand into A and updates the negative and zero flags.</summary>
        private void LDA_ZP()
        {
            registers.A = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 3;
        }

        /// <summary>Loads the zero-page X-indexed operand into A and updates the negative and zero flags.</summary>
        private void LDA_ZPX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the indexed-indirect zero-page operand into A and updates the negative and zero flags.</summary>
        private void LDA_ZPIX()
        {
            registers.A = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 6;
        }

        /// <summary>Loads the indirect-indexed zero-page operand into A and updates the negative and zero flags.</summary>
        private void LDA_ZPIY()
        {
            registers.A = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 5;
        }

        /// <summary>Loads the immediate operand into X and updates the negative and zero flags.</summary>
        private void LDX_IM()
        {
            registers.X = Immediate();
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        /// <summary>Loads the absolute operand into X and updates the negative and zero flags.</summary>
        private void LDX_AB()
        {
            registers.X = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the absolute Y-indexed operand into X and updates the negative and zero flags.</summary>
        private void LDX_ABY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Absolute());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the zero-page operand into X and updates the negative and zero flags.</summary>
        private void LDX_ZP()
        {
            registers.X = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 3;
        }

        /// <summary>Loads the zero-page Y-indexed operand into X and updates the negative and zero flags.</summary>
        private void LDX_ZPY()
        {
            registers.X = ReadByteFromMemory(Y_Indexed_Zero_Page());
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the immediate operand into Y and updates the negative and zero flags.</summary>
        private void LDY_IM()
        {
            registers.Y = Immediate();
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }

        /// <summary>Loads the absolute operand into Y and updates the negative and zero flags.</summary>
        private void LDY_AB()
        {
            registers.Y = ReadByteFromMemory(Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the absolute X-indexed operand into Y and updates the negative and zero flags.</summary>
        private void LDY_ABX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Absolute());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        /// <summary>Loads the zero-page operand into Y and updates the negative and zero flags.</summary>
        private void LDY_ZP()
        {
            registers.Y = ReadByteFromMemory(Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 3;
        }

        /// <summary>Loads the zero-page X-indexed operand into Y and updates the negative and zero flags.</summary>
        private void LDY_ZPX()
        {
            registers.Y = ReadByteFromMemory(X_Indexed_Zero_Page());
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 4;
        }

        #endregion LD*

        #region ST*

        /// <summary>Stores A at the absolute effective address.</summary>
        private void STA_AB()
        {
            WriteByteToMemory(Absolute(), registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Stores A at the absolute X-indexed effective address.</summary>
        private void STA_ABX()
        {
            WriteByteToMemory(X_Indexed_Absolute(false), registers.A);
            cyclesThisOperation += 5;
        }

        /// <summary>Stores A at the absolute Y-indexed effective address.</summary>
        private void STA_ABY()
        {
            WriteByteToMemory(Y_Indexed_Absolute(false), registers.A);
            cyclesThisOperation += 5;
        }

        /// <summary>Stores A at the zero-page effective address.</summary>
        private void STA_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.A);
            cyclesThisOperation += 3;
        }

        /// <summary>Stores A at the zero-page X-indexed effective address.</summary>
        private void STA_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Stores A at the indexed-indirect zero-page effective address.</summary>
        private void STA_ZPIX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page_Indirect(), registers.A);
            cyclesThisOperation += 6;
        }

        /// <summary>Stores A at the indirect-indexed zero-page effective address.</summary>
        private void STA_ZPIY()
        {
            WriteByteToMemory(Zero_Page_Indirect_Y_Indexed(false), registers.A);
            cyclesThisOperation += 6;
        }

        /// <summary>Stores X at the absolute effective address.</summary>
        private void STX_AB()
        {
            WriteByteToMemory(Absolute(), registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Stores X at the zero-page effective address.</summary>
        private void STX_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.X);
            cyclesThisOperation += 3;
        }

        /// <summary>Stores X at the zero-page Y-indexed effective address.</summary>
        private void STX_ZPY()
        {
            WriteByteToMemory(Y_Indexed_Zero_Page(), registers.X);
            cyclesThisOperation += 4;
        }

        /// <summary>Stores Y at the absolute effective address.</summary>
        private void STY_AB()
        {
            WriteByteToMemory(Absolute(), registers.Y);
            cyclesThisOperation += 4;
        }

        /// <summary>Stores Y at the zero-page effective address.</summary>
        private void STY_ZP()
        {
            WriteByteToMemory(Zero_Page(), registers.Y);
            cyclesThisOperation += 3;
        }

        /// <summary>Stores Y at the zero-page X-indexed effective address.</summary>
        private void STY_ZPX()
        {
            WriteByteToMemory(X_Indexed_Zero_Page(), registers.Y);
            cyclesThisOperation += 4;
        }

        #endregion ST*

        #region T**

        /// <summary>Copies A into X and updates the negative and zero flags.</summary>
        private void TAX()
        {
            registers.X = registers.A;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        /// <summary>Copies A into Y and updates the negative and zero flags.</summary>
        private void TAY()
        {
            registers.Y = registers.A;
            Set_FlagsNZ(registers.Y);
            cyclesThisOperation += 2;
        }

        /// <summary>Copies the stack pointer into X and updates the negative and zero flags.</summary>
        private void TSX()
        {
            registers.X = registers.S;
            Set_FlagsNZ(registers.X);
            cyclesThisOperation += 2;
        }

        /// <summary>Copies X into A and updates the negative and zero flags.</summary>
        private void TXA()
        {
            registers.A = registers.X;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        /// <summary>Copies X into the stack pointer without changing status flags.</summary>
        private void TXS()
        {
            registers.S = registers.X;
            cyclesThisOperation += 2;
        }

        /// <summary>Copies Y into A and updates the negative and zero flags.</summary>
        private void TYA()
        {
            registers.A = registers.Y;
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 2;
        }

        #endregion T**

        #region SE*

        /// <summary>Implements SEC by setting the carry flag used by arithmetic and rotate instructions.</summary>
        private void SEC()
        {
            registers.Flags.C = true;
            cyclesThisOperation += 2;
        }

        /// <summary>Implements SED by enabling binary-coded-decimal ADC and SBC behavior.</summary>
        private void SED()
        {
            registers.Flags.D = true;
            cyclesThisOperation += 2;
        }

        /// <summary>Masks maskable interrupts by setting the interrupt-disable flag.</summary>
        private void SEI()
        {
            registers.Flags.I = true;
            cyclesThisOperation += 2;
        }

        #endregion SE*

        #region PH*

        /// <summary>Pushes the accumulator onto the 6502 hardware stack.</summary>
        private void PHA()
        {
            PushByteToStack(registers.A);
            cyclesThisOperation += 3;
        }

        /// <summary>Pushes the processor status byte onto the 6502 hardware stack.</summary>
        private void PHP()
        {
            byte addr = registers.Flags.GetFlagsAsByte();
            addr = (byte)(addr | (1 << 4));
            addr = (byte)(addr | (1 << 5));
            PushByteToStack(addr);
            cyclesThisOperation += 3;
        }

        #endregion PH*

        #region PL*

        /// <summary>Pulls the accumulator from the stack and updates N and Z.</summary>
        private void PLA()
        {
            registers.A = PopByteFromStack();
            Set_FlagsNZ(registers.A);
            cyclesThisOperation += 4;
        }

        /// <summary>Pulls the processor status flags from the 6502 hardware stack.</summary>
        private void PLP()
        {
            byte value = PopByteFromStack();
            registers.Flags.SetFlagsFromByte(value, 0xCF);
            cyclesThisOperation += 4;
        }

        #endregion PL*

        #region CL*

        /// <summary>Implements CLC by clearing the carry input used by arithmetic and rotate instructions.</summary>
        private void CLC()
        {
            registers.Flags.C = false;
            cyclesThisOperation += 2;
        }

        /// <summary>Implements CLD by restoring binary ADC and SBC behavior.</summary>
        private void CLD()
        {
            registers.Flags.D = false;
            cyclesThisOperation += 2;
        }

        /// <summary>Allows maskable interrupts by clearing the interrupt-disable flag.</summary>
        private void CLI()
        {
            registers.Flags.I = false;
            cyclesThisOperation += 2;
        }

        /// <summary>Implements CLV by clearing the signed-arithmetic overflow flag.</summary>
        private void CLV()
        {
            registers.Flags.V = false;
            cyclesThisOperation += 2;
        }

        #endregion CL*

        #region DE*

        /// <summary>Decrements the absolute memory operand and updates N and Z.</summary>
        private void DECA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Decrements the absolute X-indexed memory operand and updates N and Z.</summary>
        private void DECXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Decrements the zero-page memory operand and updates N and Z.</summary>
        private void DECZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Decrements the zero-page X-indexed memory operand and updates N and Z.</summary>
        private void DECXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            byte value2 = (byte)((value1 + (~0x01)) + 1);
            WriteByteToMemory(addr, value2);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Decrements X and updates the negative and zero flags.</summary>
        private void DEX()
        {
            byte value2 = (byte)(registers.X - 1);
            registers.X = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        /// <summary>Decrements Y and updates the negative and zero flags.</summary>
        private void DEY()
        {
            byte value2 = (byte)(registers.Y - 1);
            registers.Y = value2;
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        #endregion DE*

        #region IN*

        /// <summary>Increments the absolute memory operand and updates N and Z.</summary>
        private void INCA()
        {
            ulong addr = Absolute();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 6;
        }

        /// <summary>Increments the absolute X-indexed memory operand and updates N and Z.</summary>
        private void INCXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 7;
        }

        /// <summary>Increments the zero-page memory operand and updates N and Z.</summary>
        private void INCZP()
        {
            ulong addr = Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 5;
        }

        /// <summary>Increments the zero-page X-indexed memory operand and updates N and Z.</summary>
        private void INCXZP()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value1 = ReadByteFromMemory(addr);
            value1++;
            WriteByteToMemory(addr, value1);
            Set_FlagsNZ(value1);
            cyclesThisOperation += 6;
        }

        /// <summary>Increments X and updates the negative and zero flags.</summary>
        private void INX()
        {
            byte value1 = (byte)(registers.X + 1);
            registers.X = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }

        /// <summary>Increments Y and updates the negative and zero flags.</summary>
        private void INY()
        {
            byte value1 = (byte)(registers.Y + 1);
            registers.Y = value1;
            Set_FlagsNZ(value1);
            cyclesThisOperation += 2;
        }

        #endregion IN*

        #region CM*

        /// <summary>Compares the accumulator with the immediate operand and updates N, Z, and C.</summary>
        private void CMPI()
        {
            byte addr = Immediate();
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 2;
        }

        /// <summary>Compares the accumulator with the absolute operand and updates N, Z, and C.</summary>
        private void CMPA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Compares the accumulator with the absolute X-indexed operand and updates N, Z, and C.</summary>
        private void CMPXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Compares the accumulator with the absolute Y-indexed operand and updates N, Z, and C.</summary>
        private void CMPYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Compares the accumulator with the zero-page operand and updates N, Z, and C.</summary>
        private void CMPZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 3;
        }

        /// <summary>Compares the accumulator with the zero-page X-indexed operand and updates N, Z, and C.</summary>
        private void CMPXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 4;
        }

        /// <summary>Compares the accumulator with the indexed-indirect operand and updates N, Z, and C.</summary>
        private void CMPXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Compares the accumulator with the indirect-indexed operand and updates N, Z, and C.</summary>
        private void CMPYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value2 = (byte)(registers.A - addr);
            registers.Flags.C = (addr <= registers.A);
            Set_FlagsNZ(value2);
            cyclesThisOperation += 5;
        }

        #endregion CM*

        #region CPX

        /// <summary>Compares X with the immediate operand and updates N, Z, and C.</summary>
        private void CPXI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Compares X with the absolute operand and updates N, Z, and C.</summary>
        private void CPXA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Compares X with the zero-page operand and updates N, Z, and C.</summary>
        private void CPXZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.X - addr);
            registers.Flags.C = (registers.X >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        #endregion CPX

        #region CPY

        /// <summary>Compares Y with the immediate operand and updates N, Z, and C.</summary>
        private void CPYI()
        {
            byte addr = Immediate();
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Compares Y with the absolute operand and updates N, Z, and C.</summary>
        private void CPYA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Compares Y with the zero-page operand and updates N, Z, and C.</summary>
        private void CPYZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)((registers.Y + (~addr)) + 1);
            registers.Flags.C = (registers.Y >= value);
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        #endregion CPY

        #region ADC

        /// <summary>Adds the immediate operand and carry to the accumulator.</summary>
        private void ADCI()
        {
            byte value = Immediate();
            ADC(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Adds the absolute operand and carry to the accumulator.</summary>
        private void ADCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Adds the absolute X-indexed operand and carry to the accumulator.</summary>
        private void ADCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Adds the absolute Y-indexed operand and carry to the accumulator.</summary>
        private void ADCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Adds the zero-page operand and carry to the accumulator.</summary>
        private void ADCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            ADC(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Adds the zero-page X-indexed operand and carry to the accumulator.</summary>
        private void ADCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            ADC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Adds the indexed-indirect operand and carry to the accumulator.</summary>
        private void ADCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            ADC(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Adds the indirect-indexed operand and carry to the accumulator.</summary>
        private void ADCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            ADC(value);
            cyclesThisOperation += 5;
        }

        /// <summary>Adds the supplied operand and carry to the accumulator, including 6502 decimal-mode behavior.</summary>
        /// <param name="value">The value supplied to the operation.</param>
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

        #endregion ADC

        #region SBC

        /// <summary>Subtracts the immediate operand and inverse carry from the accumulator.</summary>
        private void SBCI()
        {
            byte value = Immediate();
            SBC(value);
            cyclesThisOperation += 2;
        }

        /// <summary>Subtracts the absolute operand and inverse carry from the accumulator.</summary>
        private void SBCA()
        {
            byte value = ReadByteFromMemory(Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Subtracts the absolute X-indexed operand and inverse carry from the accumulator.</summary>
        private void SBCXA()
        {
            byte value = ReadByteFromMemory(X_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Subtracts the absolute Y-indexed operand and inverse carry from the accumulator.</summary>
        private void SBCYA()
        {
            byte value = ReadByteFromMemory(Y_Indexed_Absolute());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Subtracts the zero-page operand and inverse carry from the accumulator.</summary>
        private void SBCZ()
        {
            byte value = ReadByteFromMemory(Zero_Page());
            SBC(value);
            cyclesThisOperation += 3;
        }

        /// <summary>Subtracts the zero-page X-indexed operand and inverse carry from the accumulator.</summary>
        private void SBCXZ()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page());
            SBC(value);
            cyclesThisOperation += 4;
        }

        /// <summary>Subtracts the indexed-indirect operand and inverse carry from the accumulator.</summary>
        private void SBCXZI()
        {
            byte value = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            SBC(value);
            cyclesThisOperation += 6;
        }

        /// <summary>Subtracts the indirect-indexed operand and inverse carry from the accumulator.</summary>
        private void SBCYZI()
        {
            byte value = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            SBC(value);
            cyclesThisOperation += 5;
        }

        /// <summary>Subtracts the supplied operand and inverse carry from the accumulator, including 6502 decimal-mode behavior.</summary>
        /// <param name="value">The value supplied to the operation.</param>
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

        #endregion SBC

        #region EOR

        /// <summary>XORs the accumulator with the immediate operand and updates N and Z.</summary>
        private void EORI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>XORs the accumulator with the absolute operand and updates N and Z.</summary>
        private void EORA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>XORs the accumulator with the absolute X-indexed operand and updates N and Z.</summary>
        private void EORXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>XORs the accumulator with the absolute Y-indexed operand and updates N and Z.</summary>
        private void EORYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>XORs the accumulator with the zero-page operand and updates N and Z.</summary>
        private void EORZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        /// <summary>XORs the accumulator with the zero-page X-indexed operand and updates N and Z.</summary>
        private void EORXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>XORs the accumulator with the indexed-indirect operand and updates N and Z.</summary>
        private void EORXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        /// <summary>XORs the accumulator with the indirect-indexed operand and updates N and Z.</summary>
        private void EORYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A ^ addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }

        #endregion EOR

        #region ORA

        /// <summary>ORs the accumulator with the immediate operand and updates N and Z.</summary>
        private void ORAI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>ORs the accumulator with the absolute operand and updates N and Z.</summary>
        private void ORAA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ORs the accumulator with the absolute X-indexed operand and updates N and Z.</summary>
        private void ORAXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ORs the accumulator with the absolute Y-indexed operand and updates N and Z.</summary>
        private void ORAYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ORs the accumulator with the zero-page operand and updates N and Z.</summary>
        private void ORAZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        /// <summary>ORs the accumulator with the zero-page X-indexed operand and updates N and Z.</summary>
        private void ORAXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ORs the accumulator with the indexed-indirect operand and updates N and Z.</summary>
        private void ORAXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        /// <summary>ORs the accumulator with the indirect-indexed operand and updates N and Z.</summary>
        private void ORAYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A | addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }

        #endregion ORA

        #region AND

        /// <summary>ANDs the accumulator with the immediate operand and updates N and Z.</summary>
        private void ANDI()
        {
            byte addr = Immediate();
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 2;
        }

        /// <summary>ANDs the accumulator with the absolute operand and updates N and Z.</summary>
        private void ANDA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ANDs the accumulator with the absolute X-indexed operand and updates N and Z.</summary>
        private void ANDXA()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ANDs the accumulator with the absolute Y-indexed operand and updates N and Z.</summary>
        private void ANDYA()
        {
            byte addr = ReadByteFromMemory(Y_Indexed_Absolute());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ANDs the accumulator with the zero-page operand and updates N and Z.</summary>
        private void ANDZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 3;
        }

        /// <summary>ANDs the accumulator with the zero-page X-indexed operand and updates N and Z.</summary>
        private void ANDXZ()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 4;
        }

        /// <summary>ANDs the accumulator with the indexed-indirect operand and updates N and Z.</summary>
        private void ANDXZI()
        {
            byte addr = ReadByteFromMemory(X_Indexed_Zero_Page_Indirect());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 6;
        }

        /// <summary>ANDs the accumulator with the indirect-indexed operand and updates N and Z.</summary>
        private void ANDYZI()
        {
            byte addr = ReadByteFromMemory(Zero_Page_Indirect_Y_Indexed());
            byte value = (byte)(registers.A & addr);
            registers.A = value;
            Set_FlagsNZ(value);
            cyclesThisOperation += 5;
        }

        #endregion AND

        #region BIT

        /// <summary>Tests A against an absolute memory byte and updates N, V, and Z.</summary>
        private void BITA()
        {
            byte addr = ReadByteFromMemory(Absolute());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 4;
        }

        /// <summary>Tests A against a zero-page memory byte and updates N, V, and Z.</summary>
        private void BITZ()
        {
            byte addr = ReadByteFromMemory(Zero_Page());
            byte value = (byte)(registers.A & addr);
            registers.Flags.N = ((addr & (1 << 7)) != 0);
            registers.Flags.V = ((addr & (1 << 6)) != 0);
            registers.Flags.Z = (value == 0);
            cyclesThisOperation += 3;
        }

        #endregion BIT

        #region ASL

        /// <summary>Shifts the accumulator operand left, moving bit 7 into carry.</summary>
        private void ASLAC()
        {
            byte addr = registers.A;
            registers.Flags.N = ((addr & (1 << 6)) != 0);
            registers.Flags.C = ((addr & (1 << 7)) != 0);
            byte value = (byte)(addr << 1);
            registers.Flags.Z = (value == 0);
            registers.A = value;
            cyclesThisOperation += 2;
        }

        /// <summary>Shifts the absolute operand left, moving bit 7 into carry.</summary>
        private void ASLA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Shifts the absolute X-indexed operand left, moving bit 7 into carry.</summary>
        private void ASLXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Shifts the zero-page operand left, moving bit 7 into carry.</summary>
        private void ASLZ()
        {
            ulong addr = Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 5;
        }

        /// <summary>Shifts the zero-page X-indexed operand left, moving bit 7 into carry.</summary>
        private void ASLXZ()
        {
            ulong addr = X_Indexed_Zero_Page();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = ((value & (1 << 6)) != 0);
            registers.Flags.C = ((value & (1 << 7)) != 0);
            byte value2 = (byte)(value << 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        #endregion ASL

        #region LSR

        /// <summary>Shifts the accumulator operand right, moving bit 0 into carry.</summary>
        private void LSRAC()
        {
            byte addr = registers.A;
            registers.Flags.N = false;
            registers.Flags.C = ((addr & (1 << 0)) != 0);
            byte value = (byte)(addr >> 1);
            registers.Flags.Z = (value == 0);
            registers.A = value;
            cyclesThisOperation += 2;
        }

        /// <summary>Shifts the absolute operand right, moving bit 0 into carry.</summary>
        private void LSRA()
        {
            ulong addr = Absolute();
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 6;
        }

        /// <summary>Shifts the absolute X-indexed operand right, moving bit 0 into carry.</summary>
        private void LSRXA()
        {
            ulong addr = X_Indexed_Absolute(false);
            byte value = ReadByteFromMemory(addr);
            registers.Flags.N = false;
            registers.Flags.C = ((value & (1 << 0)) != 0);
            byte value2 = (byte)(value >> 1);
            registers.Flags.Z = (value2 == 0);
            WriteByteToMemory(addr, value2);
            cyclesThisOperation += 7;
        }

        /// <summary>Shifts the zero-page operand right, moving bit 0 into carry.</summary>
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

        /// <summary>Shifts the zero-page X-indexed operand right, moving bit 0 into carry.</summary>
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

        #endregion LSR

        #region ROL

        /// <summary>Rotates the accumulator operand left through carry.</summary>
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

        /// <summary>Rotates the absolute operand left through carry.</summary>
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

        /// <summary>Rotates the absolute X-indexed operand left through carry.</summary>
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

        /// <summary>Rotates the zero-page operand left through carry.</summary>
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

        /// <summary>Rotates the zero-page X-indexed operand left through carry.</summary>
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

        #endregion ROL

        #region ROR

        /// <summary>Rotates the accumulator operand right through carry.</summary>
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

        /// <summary>Rotates the absolute operand right through carry.</summary>
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
            cyclesThisOperation += 6;
        }

        /// <summary>Rotates the absolute X-indexed operand right through carry.</summary>
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

        /// <summary>Rotates the zero-page operand right through carry.</summary>
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

        /// <summary>Rotates the zero-page X-indexed operand right through carry.</summary>
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

        #endregion ROR

        #region BRANCH

        /// <summary>Branches when the carry flag is clear.</summary>
        private void BCC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.C)
                Branch(value);
        }

        /// <summary>Branches when the carry flag is set.</summary>
        private void BCS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.C)
                Branch(value);
        }

        /// <summary>Branches when the zero flag is set.</summary>
        private void BEQ()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.Z)
                Branch(value);
        }

        /// <summary>Branches when the negative flag is set.</summary>
        private void BMI()
        {
            byte value = ReadByteFromMemory(registers.PC);
            cyclesThisOperation += 2;
            IncrementProgramCounter();
            if (registers.Flags.N)
                Branch(value);
        }

        /// <summary>Branches when the zero flag is clear.</summary>
        private void BNE()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.Z)
                Branch(value);
        }

        /// <summary>Branches when the negative flag is clear.</summary>
        private void BPL()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.N)
                Branch(value);
        }

        /// <summary>Branches when the overflow flag is clear.</summary>
        private void BVC()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (!registers.Flags.V)
                Branch(value);
        }

        /// <summary>Branches when the overflow flag is set.</summary>
        private void BVS()
        {
            cyclesThisOperation += 2;
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            if (registers.Flags.V)
                Branch(value);
        }

        /// <summary>Enters the maskable interrupt handler through the BRK/IRQ vector.</summary>
        private void BRK()
        {
            IncrementProgramCounter();

            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));
            PushByteToStack((byte)(registers.P | 0x30));
            registers.Flags.I = true;
            registers.PC = (ulong)(ReadByteFromMemory(0xFFFE) + ReadByteFromMemory(0xFFFF) * 0x100);
            cyclesThisOperation += 7;
        }

        /// <summary>Applies a relative branch target and cycle penalty.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        private void Branch(ulong value)
        {

            ulong oldPc = registers.PC;
            int offset = (sbyte)(byte)value;
            registers.PC = (ulong)((long)registers.PC + offset) & 0xFFFF;

            cyclesThisOperation += 1;
            if (CrossBoundary(oldPc, registers.PC))
                cyclesThisOperation += 1;
        }

        #endregion BRANCH

        #region J**

        /// <summary>Loads the program counter from an absolute jump target.</summary>
        private void JMPA()
        {
            ulong value = Absolute();
            registers.PC = value;
            cyclesThisOperation += 3;
        }

        /// <summary>Loads the program counter through an indirect pointer, including the original 6502 page-wrap quirk.</summary>
        private void JMPAI()
        {
            ulong addr = AbsoluteIndirect();
            registers.PC = addr;
            cyclesThisOperation += 5;
        }

        /// <summary>Pushes the return address and jumps to an absolute subroutine target.</summary>
        private void JSRA()
        {
            byte pclo = ReadByteFromMemory(registers.PC);
            registers.PC++;
            byte hi = (byte)(((registers.PC) >> 8) & 0xFF);
            PushByteToStack(hi);
            byte lo = (byte)((registers.PC) & 0xFF);
            PushByteToStack(lo);
            byte pchi = ReadByteFromMemory(registers.PC);
            registers.PC = (ulong)((pchi << 8) | pclo);
            cyclesThisOperation += 6;
        }

        #endregion J**

        #region RT*

        /// <summary>Restores processor status and the program counter after an interrupt.</summary>
        private void RTI()
        {
            byte flags = PopByteFromStack();
            byte lo = PopByteFromStack();
            byte hi = PopByteFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.Flags.SetFlagsFromByte(flags, 0b11001111);
            cyclesThisOperation += 6;
        }

        /// <summary>Returns from a subroutine by restoring and incrementing the saved program counter.</summary>
        private void RTS()
        {
            byte lo = PopByteFromStack();
            byte hi = PopByteFromStack();
            registers.PC = (ulong)((hi << 8) | lo);
            registers.PC++;
            cyclesThisOperation += 6;
        }

        #endregion RT*

        #endregion Documented Opcodes

        /// <summary>Advances the 16-bit program counter with address-space wrapping.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementProgramCounter(ulong value = 1)
        {

            registers.PC = (registers.PC + value) & 0xFFFF;
        }

        /// <summary>Reads one byte from the CPU bus after wrapping the address to 16 bits.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadByteFromMemory(ulong addr)
        {
            return bus.ReadByte(addr & 0xFFFF);
        }

        /// <summary>Writes one byte to the CPU bus after wrapping the address to 16 bits.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteByteToMemory(ulong addr, byte value)
        {
            bus.WriteByte(addr & 0xFFFF, value);
        }

        /// <summary>Reads a little-endian 16-bit word from the CPU bus.</summary>
        /// <param name="addr">The emulated address to access.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadWordFromBus(ulong addr)
        {
            byte lo = ReadByteFromMemory(addr);
            byte hi = ReadByteFromMemory((addr + 1) & 0xFFFF);
            return (ulong)(lo | (hi << 8));
        }

        /// <summary>Reads the next instruction byte and advances PC.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetNextByteInstruction()
        {
            byte value = ReadByteFromMemory(registers.PC);
            IncrementProgramCounter();
            return value;
        }

        /// <summary>Reads the next instruction word and advances PC.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong GetNextWordInstruction()
        {
            byte value1 = GetNextByteInstruction();
            byte value2 = GetNextByteInstruction();
            return (ulong)(value1 | (value2 << 8));
        }

        /// <summary>Writes a byte to page $01 at S, then decrements the 8-bit stack pointer.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushByteToStack(byte value)
        {
            WriteByteToMemory((ulong)(registers.S + 0x100), value);
            registers.S--;
        }

        /// <summary>Increments the 8-bit stack pointer, then reads the byte from page $01.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte PopByteFromStack()
        {
            registers.S++;
            return ReadByteFromMemory((ulong)(registers.S + 0x100));
        }

        /// <summary>Stacks PC and processor status, masks IRQ, and loads the supplied NMI vector.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        private void ProcessNMI(ulong value = 0xFFFA)
        {
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));

            PushByteToStack((byte)((registers.P & 0xEF) | 0x20));
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        /// <summary>Stacks PC and processor status, masks further IRQs, and loads the supplied IRQ vector.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        private void ProcessIRQ(ulong value = 0xFFFE)
        {
            PushByteToStack((byte)((registers.PC >> 8) & 0xFF));
            PushByteToStack((byte)(registers.PC & 0xFF));

            PushByteToStack((byte)((registers.P & 0xEF) | 0x20));
            registers.Flags.I = true;
            registers.PC = (ushort)(ReadByteFromMemory(value) | (ReadByteFromMemory(value + 1) << 8));
            cyclesThisOperation += 7;
        }

        /// <summary>Determines whether two addresses cross a page boundary.</summary>
        /// <param name="addr1">The first address to compare.</param>
        /// <param name="addr2">The second address to compare.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CrossBoundary(ulong addr1, ulong addr2)
        {
            return (addr1 & 0xff00) != (addr2 & 0xff00);
        }
    }

}
