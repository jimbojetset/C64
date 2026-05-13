using _6502CPU;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static SDL2.SDL;

namespace C64
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            NativeLibrary.SetDllImportResolver(typeof(SDL2.SDL).Assembly, ResolveNativeLibrary);

            try
            {
                using var emu = new C64Emulator();
                if (args.Length > 0 && File.Exists(args[0]))
                    emu.QueueLoad(args[0]);
                emu.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal: {ex}");
                return 1;
            }
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "SDL2") return IntPtr.Zero;

            string[] candidates;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidates = new[]
                {
                    "/opt/homebrew/lib/libSDL2.dylib",      // Apple Silicon Homebrew
                    "/opt/homebrew/opt/sdl2/lib/libSDL2.dylib",
                    "/usr/local/lib/libSDL2.dylib",         // Intel Homebrew / manual install
                    "/usr/local/opt/sdl2/lib/libSDL2.dylib",
                    "libSDL2.dylib",
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                candidates = new[]
                {
                    "libSDL2-2.0.so.0",
                    "libSDL2.so",
                };
            }
            else
            {
                candidates = new[] { "SDL2.dll" };
            }

            foreach (string c in candidates)
            {
                if (NativeLibrary.TryLoad(c, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        }
    }

    // Split across multiple files for organisation:
    //   Program.cs   - emulator core, CPU/CIA wiring, file load/save, main loop
    //   Keyboard.cs  - PETSCII translation, keyboard matrix, joystick port 2
    //   Display.cs   - SDL window/renderer/texture and per-scanline VIC-II rendering
    internal sealed partial class C64Emulator : IDisposable
    {
        private readonly _6502_CPU cpu;
        private const int Clock_PAL = 985_248;   // 6510 @ PAL
        private const int Clock_NTSC = 1_022_727; // 6510 @ NTSC

        // CIA helper thread cadence. Timer A is decremented by computed
        // elapsed CPU cycles each tick, not by a fixed IRQ cadence.
        private const int CiaTickHz = 1000;

        // The raster thread now drives rendering scanline-by-scanline, so
        // mid-frame writes from the game's raster IRQ handler (changing
        // $D018, $D020, $D021, sprite positions, etc.) take effect on the
        // remaining scanlines - making split-screen tricks and sprite
        // multiplexers visible. See Display.cs for the framebuffers,
        // foreground/sprite masks and the per-scanline VIC-II renderer.

        // SDL display + per-scanline VIC-II renderer + raster thread.
        // Owns the raster compare value, the diagnostic line counter
        // and the reset coordination flags - we route through these
        // properties when host code needs to read or update them.
        private readonly Display display;

        // CIA-1 timer A / ICR state.
        private readonly object cia1Lock = new object();
        private ushort cia1TimerALatch = 0xFFFF;
        private ushort cia1TimerACounter = 0xFFFF;
        private ushort cia1TimerBLatch = 0xFFFF;
        private ushort cia1TimerBCounter = 0xFFFF;
        private byte cia1Cra;
        private byte cia1Crb;
        private byte cia1IcrMask;
        private byte cia1IcrStatus;
        // CIA CNT pin model. Without a full IEC/serial implementation we
        // approximate CNT as high and generate pulses when CIA serial
        // output mode is active (CRA bit 6), clocked by timer-A underflow.
        private bool cia1CntHigh = true;

        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Thread? cpuThread;
        private Thread? irqThread;

        // CIA-1 port latches / data direction registers.
        private byte cia1PortA = 0xFF;
        private byte cia1PortB = 0xFF;
        private byte cia1Ddra = 0x00;
        private byte cia1Ddrb = 0x00;

        // CIA-2 minimal state. Port A ($DD00) is used for VIC bank
        // select (bits 0-1) and IEC serial lines; bits 6/7 are inputs
        // that idle high on a real C64 when no device pulls them low.
        private readonly object cia2Lock = new object();
        private byte cia2PortA = 0x17;
        private byte cia2Ddra = 0x3F;
        private ushort cia2TimerALatch = 0xFFFF;
        private ushort cia2TimerACounter = 0xFFFF;
        private ushort cia2TimerBLatch = 0xFFFF;
        private ushort cia2TimerBCounter = 0xFFFF;
        private byte cia2Cra;
        private byte cia2Crb;

        private readonly ConcurrentQueue<string> pendingLoads = new ConcurrentQueue<string>();

        public C64Emulator()
        {
            cpu = new _6502_CPU(Clock_PAL);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "basic.901226-01.bin"), Memory.BankSlot.Basic);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "kernal.901227-03.bin"), Memory.BankSlot.Kernal);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "characters.901225-01.bin"), Memory.BankSlot.Char);
            display = new Display(cpu);

            byte[] kernal = cpu.memory.GetBankedROM(Memory.BankSlot.Kernal)!;
            kernal[0xFCF5 - 0xE000] = 0xEA;
            kernal[0xFCF6 - 0xE000] = 0xEA;
            kernal[0xFCF7 - 0xE000] = 0xEA;

            InitHardware();

            cpu.OnReset = InitHardware;

            display.RasterCompare = 0;

            cpu.memory.OnIOWrite = OnIOWrite;
            cpu.memory.OnIORead = OnIORead;
            cpu.memory.OnIOPostRead = OnIOPostRead;
        }

        private void InitHardware()
        {
            byte[] m = cpu.memory.memory;

            Array.Clear(m, 0x0000, m.Length);

            m[0x0000] = 0x2F;
            m[0x0001] = 0x37;

            cia1PortA = 0xFF;
            cia1PortB = 0xFF;
            cia1Ddra = 0x00;
            cia1Ddrb = 0x00;
            m[0xDC00] = cia1PortA;
            m[0xDC01] = cia1PortB;
            m[0xDC02] = cia1Ddra;
            m[0xDC03] = cia1Ddrb;

            lock (cia1Lock)
            {
                cia1TimerALatch = 0xFFFF;
                cia1TimerACounter = 0xFFFF;
                cia1TimerBLatch = 0xFFFF;
                cia1TimerBCounter = 0xFFFF;
                cia1Cra = 0x00;
                cia1Crb = 0x00;
                cia1IcrMask = 0x00;
                cia1IcrStatus = 0x00;
                cia1CntHigh = true;
            }
            m[0xDC04] = 0xFF;
            m[0xDC05] = 0xFF;
            m[0xDC06] = 0xFF;
            m[0xDC07] = 0xFF;
            m[0xDC0D] = 0x00;
            m[0xDC0E] = 0x00;
            m[0xDC0F] = 0x00;

            m[0x0281] = 0x00; m[0x0282] = 0x08; // MEMSTR = $0800
            m[0x0283] = 0x00; m[0x0284] = 0xA0; // MEMSIZ = $A000
            m[0x0288] = 0x04;                   // screen page = $0400

            m[0xDC00] = 0xFF;
            m[0xDC01] = 0xFF;

            cia2PortA = 0x17;
            cia2Ddra = 0x3F;
            m[0xDD00] = 0x17;
            m[0xDD02] = 0x3F;
            lock (cia2Lock)
            {
                cia2TimerALatch = 0xFFFF;
                cia2TimerACounter = 0xFFFF;
                cia2TimerBLatch = 0xFFFF;
                cia2TimerBCounter = 0xFFFF;
                cia2Cra = 0x00;
                cia2Crb = 0x00;
            }
            m[0xDD04] = 0xFF;
            m[0xDD05] = 0xFF;
            m[0xDD06] = 0xFF;
            m[0xDD07] = 0xFF;
            m[0xDD0E] = 0x00;
            m[0xDD0F] = 0x00;

            m[0xD011] = 0x1B; // DEN, RSEL, YSCROLL=3
            m[0xD016] = 0xC8; // (top bits), CSEL, XSCROLL=0
            m[0xD018] = 0x14; // screen $0400, char ROM shadow $1000
            m[0xD020] = 0x0E; // border  = light blue
            m[0xD021] = 0x06; // bg 0    = blue
            m[0xD022] = 0x01; // bg 1    = white
            m[0xD023] = 0x02; // bg 2    = red
            m[0xD024] = 0x03; // bg 3    = cyan

            m[0xD015] = 0x00;
            m[0xD017] = 0x00;
            m[0xD01B] = 0x00;
            m[0xD01C] = 0x00;
            m[0xD01D] = 0x00;
            m[0xD019] = 0x00;
            m[0xD01A] = 0x00;
            m[0xD01E] = 0x00;
            m[0xD01F] = 0x00;

            for (int a = 0xD800; a <= 0xDBE7; a++) m[a] = 0x0E;

            for (int a = 0x0400; a <= 0x07E7; a++) m[a] = 0x20;

            while (keyQueue.TryDequeue(out _)) { }

            display.EndReset();
        }

        private bool OnIOWrite(ulong addr, byte value)
        {
            switch (addr)
            {
                case 0xD012:
                    display.RasterCompare = (display.RasterCompare & 0x100) | value;
                    return true;
                case 0xD011:
                    {
                        display.RasterCompare = (display.RasterCompare & 0xFF) | ((value & 0x80) << 1);
                        byte oldHigh = (byte)(cpu.memory.memory[0xD011] & 0x80);
                        cpu.memory.memory[0xD011] = (byte)((value & 0x7F) | oldHigh);
                        return true;
                    }
                case 0xD019:
                    {
                        byte cur = cpu.memory.memory[0xD019];
                        byte next = (byte)(cur & ~value);
                        if ((next & 0x0F) == 0) next &= 0x7F;
                        cpu.memory.memory[0xD019] = next;
                        return true;
                    }
                case 0xDC0D:
                    {
                        bool raiseIrq = false;
                        lock (cia1Lock)
                        {
                            byte bits = (byte)(value & 0x1F);
                            if ((value & 0x80) != 0)
                                cia1IcrMask |= bits;
                            else
                                cia1IcrMask = (byte)(cia1IcrMask & ~bits);

                            if ((cia1IcrStatus & cia1IcrMask & 0x1F) != 0)
                            {
                                bool wasSet = (cia1IcrStatus & 0x80) != 0;
                                cia1IcrStatus |= 0x80;
                                if (!wasSet) raiseIrq = true;
                            }
                            else
                            {
                                cia1IcrStatus = (byte)(cia1IcrStatus & 0x7F);
                            }
                            cpu.memory.memory[0xDC0D] = cia1IcrStatus;
                        }
                        if (raiseIrq) cpu.InitiateIRQ(0xFFFE);
                        return true;
                    }
                case 0xDC04:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerALatch = (ushort)((cia1TimerALatch & 0xFF00) | value);
                            if ((cia1Cra & 0x01) == 0)
                                cia1TimerACounter = cia1TimerALatch;
                            cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                            cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                        }
                        return true;
                    }
                case 0xDC05:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerALatch = (ushort)((cia1TimerALatch & 0x00FF) | (value << 8));
                            if ((cia1Cra & 0x01) == 0)
                                cia1TimerACounter = cia1TimerALatch;
                            cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                            cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                        }
                        return true;
                    }
                case 0xDC0E:
                    {
                        lock (cia1Lock)
                        {
                            if ((value & 0x10) != 0)
                            {
                                cia1TimerACounter = cia1TimerALatch;
                                cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                                cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                            }

                            cia1Cra = (byte)(value & 0xEF);
                            cpu.memory.memory[0xDC0E] = cia1Cra;
                        }
                        return true;
                    }
                case 0xDC06:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerBLatch = (ushort)((cia1TimerBLatch & 0xFF00) | value);
                            if ((cia1Crb & 0x01) == 0)
                                cia1TimerBCounter = cia1TimerBLatch;
                            cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                            cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                        }
                        return true;
                    }
                case 0xDC07:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerBLatch = (ushort)((cia1TimerBLatch & 0x00FF) | (value << 8));
                            if ((cia1Crb & 0x01) == 0)
                                cia1TimerBCounter = cia1TimerBLatch;
                            cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                            cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                        }
                        return true;
                    }
                case 0xDC0F:
                    {
                        lock (cia1Lock)
                        {
                            if ((value & 0x10) != 0)
                            {
                                cia1TimerBCounter = cia1TimerBLatch;
                                cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                                cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                            }

                            cia1Crb = (byte)(value & 0xEF);
                            cpu.memory.memory[0xDC0F] = cia1Crb;
                        }
                        return true;
                    }
                case 0xDC00:
                    cia1PortA = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDC01:
                    cia1PortB = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDC02:
                    cia1Ddra = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDC03:
                    cia1Ddrb = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDD00:
                    cia2PortA = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDD02:
                    cia2Ddra = (byte)(value & 0x3F);
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDD04:
                    lock (cia2Lock)
                    {
                        cia2TimerALatch = (ushort)((cia2TimerALatch & 0xFF00) | value);
                        if ((cia2Cra & 0x01) == 0)
                            cia2TimerACounter = cia2TimerALatch;
                        cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                        cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                    }
                    return true;
                case 0xDD05:
                    lock (cia2Lock)
                    {
                        cia2TimerALatch = (ushort)((cia2TimerALatch & 0x00FF) | (value << 8));
                        if ((cia2Cra & 0x01) == 0)
                            cia2TimerACounter = cia2TimerALatch;
                        cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                        cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                    }
                    return true;
                case 0xDD06:
                    lock (cia2Lock)
                    {
                        cia2TimerBLatch = (ushort)((cia2TimerBLatch & 0xFF00) | value);
                        if ((cia2Crb & 0x01) == 0)
                            cia2TimerBCounter = cia2TimerBLatch;
                        cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                        cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                    }
                    return true;
                case 0xDD07:
                    lock (cia2Lock)
                    {
                        cia2TimerBLatch = (ushort)((cia2TimerBLatch & 0x00FF) | (value << 8));
                        if ((cia2Crb & 0x01) == 0)
                            cia2TimerBCounter = cia2TimerBLatch;
                        cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                        cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                    }
                    return true;
                case 0xDD0E:
                    lock (cia2Lock)
                    {
                        if ((value & 0x10) != 0)
                        {
                            cia2TimerACounter = cia2TimerALatch;
                            cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                            cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                        }
                        cia2Cra = (byte)(value & 0xEF);
                        cpu.memory.memory[0xDD0E] = cia2Cra;
                    }
                    return true;
                case 0xDD0F:
                    lock (cia2Lock)
                    {
                        if ((value & 0x10) != 0)
                        {
                            cia2TimerBCounter = cia2TimerBLatch;
                            cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                            cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                        }
                        cia2Crb = (byte)(value & 0xEF);
                        cpu.memory.memory[0xDD0F] = cia2Crb;
                    }
                    return true;
            }
            return false;
        }

        private byte OnIORead(ulong addr, byte fallback)
        {
            switch (addr)
            {
                case 0xDC00:
                    return ReadCia1PortA();
                case 0xDC01:
                    return ReadCia1PortB();
                case 0xDC02:
                    return cia1Ddra;
                case 0xDC03:
                    return cia1Ddrb;
                case 0xDD00:
                    {
                        byte external = 0xFF;
                        byte v = MergeCiaPortRead(cia2PortA, cia2Ddra, external);
                        return (byte)(v | 0xC0);
                    }
                case 0xDD02:
                    return cia2Ddra;
                case 0xDD04:
                    lock (cia2Lock)
                        return (byte)(cia2TimerACounter & 0xFF);
                case 0xDD05:
                    lock (cia2Lock)
                        return (byte)(cia2TimerACounter >> 8);
                case 0xDD06:
                    lock (cia2Lock)
                        return (byte)(cia2TimerBCounter & 0xFF);
                case 0xDD07:
                    lock (cia2Lock)
                        return (byte)(cia2TimerBCounter >> 8);
                case 0xDD0E:
                    return cia2Cra;
                case 0xDD0F:
                    return cia2Crb;
                case 0xDC04:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerACounter & 0xFF);
                    }
                case 0xDC05:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerACounter >> 8);
                    }
                case 0xDC06:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerBCounter & 0xFF);
                    }
                case 0xDC07:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerBCounter >> 8);
                    }
                case 0xD01E:
                case 0xD01F:
                    {
                        byte value = cpu.memory.memory[addr];
                        cpu.memory.memory[addr] = 0;
                        return value;
                    }
                case 0xDC0D:
                    {
                        lock (cia1Lock)
                        {
                            byte value = cia1IcrStatus;
                            cia1IcrStatus = 0x00;
                            cpu.memory.memory[0xDC0D] = 0x00;
                            return value;
                        }
                    }
                case 0xDD0D:
                    {
                        byte value = cpu.memory.memory[addr];
                        cpu.memory.memory[addr] = 0;
                        return value;
                    }
                default:
                    return fallback;
            }
        }

        private byte ReadCia1PortA()
        {
            byte external = 0xFF;
            external &= ReadKeyboardColumns(cia1PortB, cia1Ddrb);
            external &= joystick2;
            return MergeCiaPortRead(cia1PortA, cia1Ddra, external);
        }

        private byte ReadCia1PortB()
        {
            byte external = ReadKeyboardColumns(cia1PortA, cia1Ddra);
            return MergeCiaPortRead(cia1PortB, cia1Ddrb, external);
        }

        private static byte MergeCiaPortRead(byte latch, byte ddr, byte external)
        {
            byte outBits = (byte)((latch & external) & ddr);
            byte inBits = (byte)(external & (byte)~ddr);
            return (byte)(outBits | inBits);
        }

        private byte ReadKeyboardColumns(byte rowLatch, byte rowDdr)
        {
            byte activeRows = (byte)(~rowLatch & rowDdr);
            if (activeRows == 0)
                return 0xFF;

            byte columns = 0xFF;
            for (int row = 0; row < keyboardMatrix.Length; row++)
            {
                if ((activeRows & (1 << row)) == 0)
                    continue;
                columns &= keyboardMatrix[row];
            }
            return columns;
        }

        private static int CountUnderflows(ref ushort counter, ushort latch, uint ticks, bool oneShot, ref byte control)
        {
            if (ticks == 0 || (control & 0x01) == 0)
                return 0;

            int underflows = 0;
            uint remaining = ticks;
            while (remaining > 0 && (control & 0x01) != 0)
            {
                uint stepsToUnderflow = (uint)counter + 1u;
                if (remaining < stepsToUnderflow)
                {
                    counter = (ushort)(counter - remaining);
                    break;
                }

                remaining -= stepsToUnderflow;
                underflows++;
                counter = latch;
                if (oneShot)
                    control = (byte)(control & ~0x01);
            }

            return underflows;
        }

        private void StepCia1Timers(uint cycles)
        {
            if (cycles == 0) return;

            bool raiseIrq = false;
            lock (cia1Lock)
            {
                uint cntPulses = cia1CntHigh ? cycles : 0u;

                uint ticksA = (cia1Cra & 0x20) == 0 ? cycles : cntPulses;
                int underA = CountUnderflows(
                    ref cia1TimerACounter,
                    cia1TimerALatch,
                    ticksA,
                    (cia1Cra & 0x08) != 0,
                    ref cia1Cra);
                if (underA > 0)
                {
                    cia1IcrStatus |= 0x01;
                    if ((cia1IcrMask & 0x01) != 0)
                    {
                        cia1IcrStatus |= 0x80;
                        raiseIrq = true;
                    }
                }

                if ((cia1Cra & 0x40) != 0 && underA > 0)
                    cntPulses = Math.Max(cntPulses, (uint)underA);

                int tbMode = (cia1Crb >> 5) & 0x03;
                uint ticksB = 0;
                if (tbMode == 0)
                    ticksB = cycles;
                else if (tbMode == 1)
                    ticksB = cntPulses;
                else if (tbMode == 2)
                    ticksB = (uint)underA;
                else
                    ticksB = cia1CntHigh ? (uint)underA : 0;

                if (ticksB > 0)
                {
                    int underB = CountUnderflows(
                        ref cia1TimerBCounter,
                        cia1TimerBLatch,
                        ticksB,
                        (cia1Crb & 0x08) != 0,
                        ref cia1Crb);
                    if (underB > 0)
                    {
                        cia1IcrStatus |= 0x02;
                        if ((cia1IcrMask & 0x02) != 0)
                        {
                            cia1IcrStatus |= 0x80;
                            raiseIrq = true;
                        }
                    }
                }

                if ((cia1IcrStatus & cia1IcrMask & 0x1F) != 0)
                    cia1IcrStatus |= 0x80;
                else
                    cia1IcrStatus = (byte)(cia1IcrStatus & 0x7F);

                cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                cpu.memory.memory[0xDC0D] = cia1IcrStatus;
                cpu.memory.memory[0xDC0E] = cia1Cra;
                cpu.memory.memory[0xDC0F] = cia1Crb;
            }

            if (raiseIrq)
                cpu.InitiateIRQ(0xFFFE);
        }

        private void StepCia2Timers(uint cycles)
        {
            if (cycles == 0) return;

            lock (cia2Lock)
            {
                int underA = CountUnderflows(
                    ref cia2TimerACounter,
                    cia2TimerALatch,
                    cycles,
                    (cia2Cra & 0x08) != 0,
                    ref cia2Cra);

                uint ticksB = cycles;
                int tbMode = (cia2Crb >> 5) & 0x03;
                if (tbMode == 2)
                    ticksB = (uint)Math.Max(underA, 0);
                else if (tbMode != 0)
                    ticksB = 0;

                if (ticksB > 0)
                {
                    CountUnderflows(
                        ref cia2TimerBCounter,
                        cia2TimerBLatch,
                        ticksB,
                        (cia2Crb & 0x08) != 0,
                        ref cia2Crb);
                }

                cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                cpu.memory.memory[0xDD0E] = cia2Cra;
                cpu.memory.memory[0xDD0F] = cia2Crb;
            }
        }

        private string BuildDebugStateLine(string prefix)
        {
            ulong pc = cpu.registers.PC;
            byte[] m = cpu.memory.memory;
            byte a = cpu.registers.A;
            byte x = cpu.registers.X;
            byte y = cpu.registers.Y;
            byte s = cpu.registers.S;
            byte p = cpu.registers.P;

            ushort ta;
            ushort tb;
            byte cra;
            byte crb;
            byte icrMask;
            byte icrStatus;
            lock (cia1Lock)
            {
                ta = cia1TimerACounter;
                tb = cia1TimerBCounter;
                cra = cia1Cra;
                crb = cia1Crb;
                icrMask = cia1IcrMask;
                icrStatus = cia1IcrStatus;
            }

            return
                $"{prefix} PC=${pc:X4} A=${a:X2} X=${x:X2} Y=${y:X2} S=${s:X2} P=${p:X2} " +
                $"RAST={display.CurrentRasterLine:D3}/{display.RasterCompare:D3} DD00=${m[0xDD00]:X2} DD02=${m[0xDD02]:X2} D011=${m[0xD011]:X2} D012=${m[0xD012]:X2} D016=${m[0xD016]:X2} D018=${m[0xD018]:X2} D019=${m[0xD019]:X2} D01A=${m[0xD01A]:X2} " +
                $"CIA TA=${ta:X4} TB=${tb:X4} CRA=${cra:X2} CRB=${crb:X2} ICRM=${icrMask:X2} ICRS=${icrStatus:X2} DC0D=${m[0xDC0D]:X2}";
        }

        private void OnIOPostRead(ulong addr)
        {
        }

        public void Run()
        {
            display.Init();

            var token = cts.Token;

            cpuThread = new Thread(() =>
            {
                try { cpu.Run(); }
                catch (Exception ex) { Debug.WriteLine($"CPU thread crashed: {ex}"); }
            })
            {
                IsBackground = true,
                Name = "6502",
                Priority = ThreadPriority.AboveNormal
            };
            cpuThread.Start();

            display.Start(token);

            irqThread = new Thread(() => IrqLoop(token))
            {
                IsBackground = true,
                Name = "CIA-1 IRQ"
            };
            irqThread.Start();

            bool quit = false;
            uint nextDraw = SDL_GetTicks();
            const uint drawIntervalMs = 16; // ~60 Hz upper bound; vsync paces actual present

            while (!quit)
            {
                while (SDL_PollEvent(out SDL_Event ev) != 0)
                {
                    switch (ev.type)
                    {
                        case SDL_EventType.SDL_QUIT:
                            quit = true;
                            break;
                        case SDL_EventType.SDL_KEYDOWN:
                            if (HandleKeyDown(ev.key)) quit = true;
                            break;
                        case SDL_EventType.SDL_KEYUP:
                            HandleKeyUp(ev.key);
                            break;
                        case SDL_EventType.SDL_DROPFILE:
                            {
                                IntPtr p = ev.drop.file;
                                string? droppedPath = Marshal.PtrToStringUTF8(p);
                                if (!string.IsNullOrWhiteSpace(droppedPath))
                                    QueueLoad(droppedPath);
                                break;
                            }
                    }
                }

                while (pendingLoads.TryDequeue(out string? path))
                    DoLoad(path);

                uint now = SDL_GetTicks();
                if ((int)(now - nextDraw) >= 0)
                {
                    display.RedrawScreen();
                    nextDraw = now + drawIntervalMs;
                }
                else
                {
                    SDL_Delay(1);
                }
            }

            cts.Cancel();
        }

        public void Dispose()
        {
            try { cts.Cancel(); } catch { }
            display.Dispose();
            SDL_Quit();
        }

        private void IrqLoop(CancellationToken token)
        {
            long ticksPerTick = Stopwatch.Frequency / CiaTickHz;
            long next = Stopwatch.GetTimestamp() + ticksPerTick;
            long remCyclesNumerator = 0;
            long lastStamp = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                DrainKeyboardQueue();

                long now = Stopwatch.GetTimestamp();
                long elapsedTicks = now - lastStamp;
                if (elapsedTicks < 0) elapsedTicks = 0;
                lastStamp = now;

                if (display.IsResetting)
                {
                    long pauseRemaining = next - Stopwatch.GetTimestamp();
                    if (pauseRemaining > 0)
                    {
                        while (Stopwatch.GetTimestamp() < next)
                            Thread.SpinWait(32);
                    }
                    next += ticksPerTick;
                    continue;
                }

                long numer = elapsedTicks * Clock_PAL + remCyclesNumerator;
                if (numer >= Stopwatch.Frequency)
                {
                    uint cycles = (uint)(numer / Stopwatch.Frequency);
                    remCyclesNumerator = numer % Stopwatch.Frequency;
                    StepCia1Timers(cycles);
                    StepCia2Timers(cycles);
                }
                else
                {
                    remCyclesNumerator = numer;
                }

                long remaining = next - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    long remainingMs = remaining * 1000 / Stopwatch.Frequency;
                    if (remainingMs > 2)
                        Thread.Sleep((int)(remainingMs - 1));
                    while (Stopwatch.GetTimestamp() < next)
                        Thread.SpinWait(32);
                }
                next += ticksPerTick;
            }
        }

        

        private void HardReset()
        {
            display.BeginReset();

            joystick2 = 0xFF;
            for (int i = 0; i < keyboardMatrix.Length; i++)
                keyboardMatrix[i] = 0xFF;

            cpu.RequestReset();
        }

        public void QueueLoad(string path) => pendingLoads.Enqueue(path);

        private void LoadProgram()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.Write("Load file (.prg, .bas, .txt) - path: ");
                    string? path = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(path)) return;
                    path = path.Trim().Trim('"', '\'');
                    pendingLoads.Enqueue(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Load prompt failed: {ex.Message}");
                }
            });
        }

        private void DoLoad(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Load failed: file not found: {path}");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".bas" || ext == ".txt")
                    LoadText(path);
                else
                    LoadPrg(path);
                Console.WriteLine($"Loaded {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Load failed: {ex.Message}");
            }
        }

        private void LoadPrg(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 3)
                throw new InvalidDataException("PRG file is too small (need 2-byte header + body).");

            ushort loadAddr = (ushort)(data[0] | (data[1] << 8));
            int progLen = data.Length - 2;
            byte[] mem = cpu.memory.memory;

            if (loadAddr + progLen > mem.Length)
                throw new InvalidDataException("PRG file would load past end of memory.");

            Array.Copy(data, 2, mem, loadAddr, progLen);

            if (loadAddr == 0x0801)
            {
                int endAddr = loadAddr + progLen;
                cpu.memory.WriteByte(0x002D, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x002E, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x002F, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0030, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x0031, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0032, (byte)(endAddr >> 8));
            }
        }

        private void LoadText(string path)
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.TrimEnd('\r');
                foreach (char ch in line)
                {
                    byte pet = AsciiCharToPetscii(ch);
                    if (pet != 0) keyQueue.Enqueue(pet);
                }
                keyQueue.Enqueue(0x0D); // RETURN
            }
        }

        private static byte AsciiCharToPetscii(char ch)
        {
            if (ch >= 'a' && ch <= 'z') return (byte)('A' + (ch - 'a'));
            if (ch >= ' ' && ch <= '~') return (byte)ch;
            return 0;
        }

        private void SaveProgram()
        {
            byte[] mem = cpu.memory.memory;
            int endAddr = mem[0x2D] | (mem[0x2E] << 8);
            int progLen = endAddr - 0x0801;
            if (progLen <= 2)
            {
                Console.WriteLine("No BASIC program in memory.");
                return;
            }

            Console.Write("Save .prg - path: ");
            string? path = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim().Trim('"', '\'');
            if (!path.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
                path += ".prg";

            try
            {
                using var fs = File.Create(path);
                fs.WriteByte(0x01); // load address lo
                fs.WriteByte(0x08); // load address hi -> $0801
                fs.Write(mem, 0x0801, progLen);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Save failed: {ex.Message}");
            }
        }
    }
}